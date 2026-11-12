using Pulse.Core.Formatting;
using Pulse.Core.Interop;

namespace Pulse.Core.Metrics;

/// <summary>A process whose private memory kept growing over the last ten minutes.</summary>
/// <param name="Name">The image name without ".exe".</param>
/// <param name="ProcessId">The process.</param>
/// <param name="Bytes">Its private memory now.</param>
/// <param name="GrowthBytes">How much it grew over <paramref name="Minutes"/>, along the fitted line.</param>
/// <param name="Minutes">The time it was followed, about ten minutes.</param>
public sealed record LeakSuspect(string Name, int ProcessId, double Bytes, double GrowthBytes, double Minutes);

/// <summary>
/// Finds processes whose memory keeps growing: every <see cref="SampleSeconds"/> seconds it notes the private memory of
/// every process of at least <see cref="MinimumBytes"/>, and fits a line through the last ten minutes of it. A process
/// whose line rises at least <see cref="MinimumGrowth"/> (and a fifth of where it started) and fits that line closely is
/// a suspect: a steady climb, not a jump. It is reported as an event once, and again after every further
/// <see cref="ReportAgainAfterGrowth"/>.
/// </summary>
internal sealed class LeakDetector
{
	internal const double SampleSeconds = 10;
	internal const double WindowSeconds = 600;
	internal const double MinimumBytes = 100 * 1024 * 1024;
	internal const double MinimumGrowth = 200 * 1024 * 1024;
	internal const double ReportAgainAfterGrowth = 500 * 1024 * 1024;

	/// <summary>How closely the memory must follow a straight line (R²): a steady climb, not one jump.</summary>
	internal const double MinimumFit = 0.85;

	private readonly Dictionary<(int Id, long Created), Track> _tracks = [];
	private double _lastSample = double.NegativeInfinity;
	private IReadOnlyList<LeakSuspect> _suspects = [];

	/// <summary>
	/// Notes the memory of <paramref name="records"/> when a sample is due and returns the current suspects; a process that
	/// becomes a suspect (or grows a lot further) adds a <see cref="EventKind.Leak"/> event to <paramref name="events"/>.
	/// </summary>
	public IReadOnlyList<LeakSuspect> Sample(ReadOnlySpan<ProcessRecord> records, ProcessSnapshot snapshot, double time, DateTimeOffset wallNow, List<PulseEvent> events)
	{
		if (time - _lastSample < SampleSeconds)
		{
			return _suspects;
		}

		_lastSample = time;
		var seen = new HashSet<(int, long)>();
		foreach (ref readonly var record in records)
		{
			var key = (record.Id, record.CreateTime);
			if (record.PrivateBytes < MinimumBytes && !_tracks.ContainsKey(key))
			{
				continue;
			}

			seen.Add(key);
			if (!_tracks.TryGetValue(key, out var track))
			{
				_tracks[key] = track = new Track(TopProcesses.Clean(snapshot.NameOf(record).ToString()), record.Id);
			}

			track.Add(time, record.PrivateBytes);
		}

		foreach (var gone in _tracks.Keys.Where(key => !seen.Contains(key)).ToList())
		{
			_tracks.Remove(gone);
		}

		var suspects = new List<LeakSuspect>();
		foreach (var track in _tracks.Values)
		{
			if (!Evaluate(track.Points, out var growth, out var minutes))
			{
				continue;
			}

			var bytes = track.Points[^1].Bytes;
			var suspect = new LeakSuspect(track.Name, track.Id, bytes, growth, minutes);
			suspects.Add(suspect);
			if (double.IsNaN(track.ReportedBytes) || bytes >= track.ReportedBytes + ReportAgainAfterGrowth)
			{
				track.ReportedBytes = bytes;
				events.Add(new PulseEvent(time, wallNow, EventKind.Leak, "Memory", "leak", Describe(suspect)));
			}
		}

		_suspects = [.. suspects.OrderByDescending(suspect => suspect.GrowthBytes)];
		return _suspects;
	}

	/// <summary>"devenv (pid 1234) +420 MB in 10 min, now 1.6 GB".</summary>
	internal static string Describe(LeakSuspect suspect)
		=> $"{suspect.Name} (pid {suspect.ProcessId}) +{Units.Bytes(suspect.GrowthBytes)} in {suspect.Minutes:0} min, now {Units.Bytes(suspect.Bytes)}";

	/// <summary>
	/// Whether <paramref name="points"/> (time in seconds, bytes) cover about ten minutes and climb steadily: the least
	/// squares line rises at least <see cref="MinimumGrowth"/> and a fifth of the start, the memory ends higher than it
	/// started, and the line explains at least <see cref="MinimumFit"/> of the variation.
	/// </summary>
	internal static bool Evaluate(IReadOnlyList<(double Time, double Bytes)> points, out double growth, out double minutes)
	{
		growth = 0;
		minutes = 0;
		if (points.Count < 3)
		{
			return false;
		}

		var span = points[^1].Time - points[0].Time;
		minutes = span / 60;
		if (span < WindowSeconds - SampleSeconds)
		{
			return false;
		}

		var meanTime = points.Average(point => point.Time);
		var meanBytes = points.Average(point => point.Bytes);
		double covariance = 0, varianceTime = 0, varianceBytes = 0;
		foreach (var (time, bytes) in points)
		{
			covariance += (time - meanTime) * (bytes - meanBytes);
			varianceTime += (time - meanTime) * (time - meanTime);
			varianceBytes += (bytes - meanBytes) * (bytes - meanBytes);
		}

		if (varianceTime <= 0 || varianceBytes <= 0)
		{
			return false;
		}

		var slope = covariance / varianceTime;
		var fit = covariance * covariance / (varianceTime * varianceBytes);
		growth = slope * span;
		return growth >= Math.Max(MinimumGrowth, points[0].Bytes / 5) && points[^1].Bytes > points[0].Bytes && fit >= MinimumFit;
	}

	/// <summary>The memory of one process over the last <see cref="WindowSeconds"/>.</summary>
	private sealed class Track(string name, int id)
	{
		public string Name { get; } = name;

		public int Id { get; } = id;

		public List<(double Time, double Bytes)> Points { get; } = [];

		/// <summary>The memory when it was last reported; NaN before the first report.</summary>
		public double ReportedBytes { get; set; } = double.NaN;

		public void Add(double time, double bytes)
		{
			Points.Add((time, bytes));
			while (Points.Count > 0 && Points[0].Time < time - WindowSeconds)
			{
				Points.RemoveAt(0);
			}
		}
	}
}
