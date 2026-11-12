using Pulse.Core.Diagnostics;

namespace Pulse.Core.Metrics;

/// <summary>
/// The sampling loop: an async <see cref="PeriodicTimer"/> loop on the thread pool, independent of the
/// render loop. It owns no thread of its own and stops cleanly on <see cref="Dispose"/>.
/// </summary>
public sealed class Sampler : IDisposable
{
	private const double SlowWarningIntervalSeconds = 300;

	private readonly SystemMetricsCollector _collector;
	private readonly Action<MetricSnapshot> _publish;
	private readonly PeriodicTimer _timer;
	private readonly CancellationTokenSource _stop = new();
	private Task? _loop;
	private volatile bool _paused;
	private volatile bool _primeNext = true;
	private double _lastSlowWarning = double.NegativeInfinity;
	private bool _disposed;

	/// <param name="collector">The metric sources; used from the sampling loop only.</param>
	/// <param name="interval">Time between samples.</param>
	/// <param name="publish">Receives every sample, on a thread-pool thread.</param>
	public Sampler(SystemMetricsCollector collector, TimeSpan interval, Action<MetricSnapshot> publish)
	{
		_collector = collector;
		_publish = publish;
		_timer = new PeriodicTimer(interval);
	}

	/// <summary>Time between samples; a change takes effect from the next tick.</summary>
	public TimeSpan Interval
	{
		get => _timer.Period;
		set => _timer.Period = value;
	}

	/// <summary>While paused nothing is collected. Resuming primes the rate counters before publishing again.</summary>
	public bool IsPaused
	{
		get => _paused;
		set
		{
			if (!value && _paused)
			{
				_primeNext = true;
			}

			_paused = value;
		}
	}

	public void Start() => _loop ??= Task.Run(RunAsync);

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_stop.Cancel();
		try
		{
			_loop?.Wait(TimeSpan.FromSeconds(3));
		}
		catch (AggregateException ex)
		{
			Log.Warn("The sampling loop ended with an error.", ex);
		}

		_timer.Dispose();
		_stop.Dispose();
	}

	private async Task RunAsync()
	{
		try
		{
			Prime();
			while (await _timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
			{
				if (_paused)
				{
					continue;
				}

				if (_primeNext)
				{
					Prime();
					continue;
				}

				var started = MonotonicClock.Now;
				MetricSnapshot snapshot;
				try
				{
					snapshot = _collector.Collect();
				}
				catch (Exception ex) when (ex is not ObjectDisposedException)
				{
					Log.WarnOnce("sampler-collect", "Collecting metrics failed; the sample is skipped.", ex);
					continue;
				}

				WarnIfSlow(started, MonotonicClock.Now);
				_publish(snapshot);
			}
		}
		catch (OperationCanceledException)
		{
			// Normal shutdown.
		}
		catch (ObjectDisposedException)
		{
			// The collector was disposed while shutting down.
		}
	}

	/// <summary>Rate counters (PDH, network octets) report a value only from their second sample.</summary>
	private void Prime()
	{
		_primeNext = false;
		try
		{
			_ = _collector.Collect();
		}
		catch (Exception ex) when (ex is not ObjectDisposedException)
		{
			Log.WarnOnce("sampler-prime", "Priming the counters failed.", ex);
		}
	}

	private void WarnIfSlow(double started, double finished)
	{
		var seconds = finished - started;
		if (seconds < _timer.Period.TotalSeconds * 0.5 || finished - _lastSlowWarning < SlowWarningIntervalSeconds)
		{
			return;
		}

		_lastSlowWarning = finished;
		Log.Warn($"Collecting metrics took {seconds * 1000:0} ms, half the sampling interval or more.");
	}
}
