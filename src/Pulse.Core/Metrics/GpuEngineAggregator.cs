using System.Runtime.InteropServices;

namespace Pulse.Core.Metrics;

/// <summary>
/// Turns per-process GPU engine utilization into one adapter utilization the way Task Manager does:
/// the processes using an engine are summed per engine, and the busiest engine is the GPU utilization.
/// Averaging engines would hide a saturated 3D engine behind idle copy and video engines.
/// </summary>
public sealed class GpuEngineAggregator
{
	private readonly Dictionary<int, double> _engines = [];

	/// <summary>Number of distinct engines seen since the last <see cref="Reset"/>.</summary>
	public int EngineCount => _engines.Count;

	/// <summary>Utilization of the busiest engine in percent, clamped to 0..100; 0 when no engine was seen.</summary>
	public double Busiest
	{
		get
		{
			var busiest = 0.0;
			foreach (var total in _engines.Values)
			{
				busiest = Math.Max(busiest, total);
			}

			return Math.Min(busiest, 100);
		}
	}

	public void Reset() => _engines.Clear();

	/// <summary>Adds the utilization one process has on one engine.</summary>
	public void Add(int physicalAdapter, int engine, double utilization)
	{
		ref var total = ref CollectionsMarshal.GetValueRefOrAddDefault(_engines, (physicalAdapter << 16) | (engine & 0xFFFF), out _);
		if (double.IsFinite(utilization) && utilization > 0)
		{
			total += utilization;
		}
	}
}
