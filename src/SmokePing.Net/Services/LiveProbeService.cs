using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;

namespace SmokePing.Net.Services;

/// <summary>One live measurement: a single probe, not a round.</summary>
/// <param name="Timestamp">When the probe was sent, in unix milliseconds.</param>
/// <param name="RoundTripMilliseconds">Round-trip time, or null when the probe was lost.</param>
public readonly record struct LiveSample(long Timestamp, double? RoundTripMilliseconds);

/// <summary>
/// Probes a target once a second and streams the results to whoever is watching.
///
/// This deliberately does not touch the database. The archives are built on a fixed
/// step, and a second-by-second sample has nowhere to go in them - writing one would
/// either misalign the buckets or need a step so short the retention collapses to
/// hours. So live measurements live in memory, for as long as somebody is looking.
///
/// A session is shared: two browsers watching the same target get the same probes
/// rather than doubling the traffic, and the session stops as soon as the last one
/// goes away. That matters because "every second" is three hundred times the load of
/// the normal five minute round.
/// </summary>
public sealed class LiveProbeService : IDisposable
{
    /// <summary>Samples retained per session, so a client that reconnects sees recent history.</summary>
    public const int WindowSize = 600;

    /// <summary>Sessions allowed at once, so an open tab per target cannot flood a network.</summary>
    public const int MaximumSessions = 20;

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProbeRegistry _probes;
    private readonly HostResolver _hostResolver;
    private readonly ILogger<LiveProbeService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    public LiveProbeService(
        ProbeRegistry probes,
        HostResolver hostResolver,
        ILogger<LiveProbeService> logger,
        TimeProvider timeProvider)
    {
        _probes = probes;
        _hostResolver = hostResolver;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Sessions currently running.</summary>
    public int ActiveSessions => _sessions.Count;

    /// <summary>
    /// Watches a target. The returned sequence starts with whatever recent history the
    /// session already has, then continues with new samples until the caller stops
    /// reading. The session is created on the first watcher and stops after the last.
    /// </summary>
    public async IAsyncEnumerable<LiveSample> WatchAsync(
        MeasuredTarget target,
        int intervalMs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var session = Join(target, intervalMs);
        var reader = session.Subscribe(out var subscriber);

        try
        {
            foreach (var sample in subscriber.Backlog)
            {
                yield return sample;
            }

            await foreach (var sample in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sample;
            }
        }
        finally
        {
            Leave(session, subscriber);
        }
    }

    private Session Join(MeasuredTarget target, int intervalMs)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(target.Id, out var existing))
            {
                return existing;
            }

            if (_sessions.Count >= MaximumSessions)
            {
                throw new InvalidOperationException(
                    $"{MaximumSessions} live sessions are already running; close one before starting another.");
            }

            var session = new Session(target, intervalMs, this);
            _sessions[target.Id] = session;
            session.Start();

            _logger.LogInformation(
                "Live view started for {Target} at one probe every {Interval}ms.",
                target.Id,
                intervalMs);

            return session;
        }
    }

    private void Leave(Session session, Subscriber subscriber)
    {
        lock (_gate)
        {
            if (!session.Unsubscribe(subscriber))
            {
                return;
            }

            // The last watcher has gone, so stop probing rather than leaving a
            // once-a-second load running for nobody.
            _sessions.TryRemove(session.TargetId, out _);
            session.Dispose();
            _logger.LogInformation("Live view stopped for {Target}.", session.TargetId);
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private sealed class Subscriber
    {
        public required Channel<LiveSample> Channel { get; init; }

        public required IReadOnlyList<LiveSample> Backlog { get; init; }
    }

    /// <summary>One target being watched, with its probe loop and its watchers.</summary>
    private sealed class Session : IDisposable
    {
        private readonly List<Subscriber> _subscribers = [];
        private readonly Queue<LiveSample> _window = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly MeasuredTarget _target;
        private readonly int _intervalMs;
        private readonly LiveProbeService _owner;
        private readonly Lock _gate = new();

        private Task? _loop;

        public Session(MeasuredTarget target, int intervalMs, LiveProbeService owner)
        {
            _target = target;
            _intervalMs = intervalMs;
            _owner = owner;
        }

        public string TargetId => _target.Id;

        public void Start() => _loop = Task.Run(() => RunAsync(_stopping.Token));

        public ChannelReader<LiveSample> Subscribe(out Subscriber subscriber)
        {
            lock (_gate)
            {
                subscriber = new Subscriber
                {
                    // Dropping the oldest keeps a stalled reader from stalling the
                    // probe loop; a live view that skips a second is fine.
                    Channel = System.Threading.Channels.Channel.CreateBounded<LiveSample>(
                        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest }),
                    Backlog = [.. _window],
                };

                _subscribers.Add(subscriber);
                return subscriber.Channel.Reader;
            }
        }

        /// <summary>Removes a watcher, returning true when that was the last one.</summary>
        public bool Unsubscribe(Subscriber subscriber)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
                subscriber.Channel.Writer.TryComplete();
                return _subscribers.Count == 0;
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var probe = _owner._probes.Get(_target.ProbeType);

            // One probe per tick, not a whole round: this is a live trace, and the
            // distribution the archives store has no meaning at this resolution.
            var single = _target with { Pings = 1, PingIntervalMs = 0 };
            var stopwatch = Stopwatch.StartNew();
            var tick = 0L;

            while (!cancellationToken.IsCancellationRequested)
            {
                var due = TimeSpan.FromMilliseconds(tick * _intervalMs);
                var wait = due - stopwatch.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                tick++;

                double? rtt = null;
                try
                {
                    var resolved = _target.HasDynamicHost
                        ? _owner._hostResolver.Resolve(_target.Host)
                        : _target.Host;

                    if (resolved is not null)
                    {
                        var measured = await probe
                            .MeasureAsync(single with { Host = resolved }, cancellationToken)
                            .ConfigureAwait(false);

                        rtt = measured.Length > 0 ? measured[0] : null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A failed probe is a lost probe, exactly as in a normal round.
                    _owner._logger.LogDebug(ex, "Live probe for {Target} failed.", _target.Id);
                }

                Broadcast(new LiveSample(_owner._timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), rtt));
            }
        }

        private void Broadcast(LiveSample sample)
        {
            lock (_gate)
            {
                _window.Enqueue(sample);
                while (_window.Count > WindowSize)
                {
                    _window.Dequeue();
                }

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Channel.Writer.TryWrite(sample);
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();

            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Channel.Writer.TryComplete();
                }

                _subscribers.Clear();
            }

            _stopping.Dispose();
        }
    }
}
