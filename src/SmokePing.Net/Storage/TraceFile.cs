using System.Buffers.Binary;

namespace SmokePing.Net.Storage;

/// <summary>One resolution tier of a <see cref="TraceFile"/>.</summary>
/// <param name="StepSeconds">Seconds covered by a single slot.</param>
/// <param name="SlotCount">Slots before the tier wraps.</param>
/// <param name="Offset">Byte offset of the tier's first slot.</param>
public sealed record TraceTier(int StepSeconds, int SlotCount, long Offset)
{
    /// <summary>How far back the tier reaches, in seconds.</summary>
    public long SpanSeconds => (long)StepSeconds * SlotCount;
}

/// <summary>
/// A recorded, second-by-second trace of one target.
///
/// The measurement archives answer "what is this link normally like": a round of
/// probes every few minutes, kept as a distribution. That is the wrong instrument for
/// finding the two seconds during a game where the line hiccupped - a spike that
/// short is a rounding error in a five minute round, and a live view only exists
/// while somebody is watching it, which is never the moment it happens.
///
/// So this records continuously and keeps the peaks. The fine tier holds one probe
/// per slot for the last day or so. The coarse tier summarises it into longer slots
/// for weeks, and it keeps the <em>maximum</em> alongside the mean, so a single bad
/// second is still visible in a summary that covers a minute or an hour of good ones.
/// An average would have erased exactly the thing worth finding.
///
/// Layout:
///   header (64 bytes) | tier descriptors (16 bytes each) | slot data
/// Each slot is 36 bytes: slot index (8), min (4), mean (4), max (4), jitter (4),
/// jitter max (4), sent (4), lost (4).
/// </summary>
public sealed class TraceFile : IDisposable
{
    private const uint Magic = 0x52545053; // "SPTR" little-endian
    private const int FormatVersion = 1;
    private const int HeaderSize = 64;
    private const int TierDescriptorSize = 16;
    private const int SlotSize = 8 + (4 * 7);

    private const int MinimumOffset = 8;
    private const int MeanOffset = 12;
    private const int MaximumOffset = 16;
    private const int JitterOffset = 20;
    private const int JitterMaximumOffset = 24;
    private const int SentOffset = 28;
    private const int LostOffset = 32;

    private const long EmptySlot = -1;

    private readonly Lock _gate = new();
    private readonly FileStream _stream;
    private readonly byte[] _slotBuffer = new byte[SlotSize];
    private readonly Bucket?[] _buckets;

    private long _newestSlotIndex = long.MinValue;

    private TraceFile(FileStream stream, string path, int stepSeconds, TraceTier[] tiers)
    {
        _stream = stream;
        Path = path;
        StepSeconds = stepSeconds;
        Tiers = tiers;
        _buckets = new Bucket?[tiers.Length];
    }

    public string Path { get; }

    /// <summary>Seconds between recorded probes: the resolution of tier 0.</summary>
    public int StepSeconds { get; }

    public IReadOnlyList<TraceTier> Tiers { get; }

    /// <summary>Bytes a file with this plan occupies, for reporting a target's cost up front.</summary>
    public static long FileSize(IReadOnlyList<(int Multiplier, int SlotCount)> plan) =>
        HeaderSize + ((long)TierDescriptorSize * plan.Count) + plan.Sum(t => (long)t.SlotCount * SlotSize);

    /// <summary>
    /// Opens the trace for a target, creating it when missing. A file written for a
    /// different resolution or retention is moved aside rather than misread; unlike the
    /// measurement archives this history is disposable by design, so recreating it
    /// costs a day of fine detail rather than a year of graphs.
    /// </summary>
    /// <param name="plan">
    /// Tiers as (multiple of <paramref name="stepSeconds"/>, slots). The first must be 1.
    /// </param>
    public static TraceFile OpenOrCreate(
        string path,
        int stepSeconds,
        IReadOnlyList<(int Multiplier, int SlotCount)> plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSeconds, 1);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Count == 0 || plan[0].Multiplier != 1)
        {
            throw new ArgumentException("The first tier must have a step multiplier of 1.", nameof(plan));
        }

        for (var i = 1; i < plan.Count; i++)
        {
            if (plan[i].Multiplier <= plan[i - 1].Multiplier ||
                plan[i].Multiplier % plan[i - 1].Multiplier != 0)
            {
                throw new ArgumentException(
                    "Each tier's step must be a growing multiple of the one before it.", nameof(plan));
            }
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            if (TryOpenExisting(path, stepSeconds, plan, out var existing, out var unreadable))
            {
                return existing;
            }

            if (unreadable is not null)
            {
                throw new IOException(
                    $"Could not open '{path}': {unreadable.Message}", unreadable);
            }

            File.Delete(path);
        }

        return Create(path, stepSeconds, plan);
    }

    private static bool TryOpenExisting(
        string path,
        int stepSeconds,
        IReadOnlyList<(int Multiplier, int SlotCount)> plan,
        out TraceFile file,
        out Exception? unreadable)
    {
        file = null!;
        unreadable = null;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var header = new byte[HeaderSize];
            if (stream.Read(header, 0, HeaderSize) != HeaderSize)
            {
                stream.Dispose();
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != FormatVersion ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != stepSeconds ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != plan.Count)
            {
                stream.Dispose();
                return false;
            }

            var tiers = new TraceTier[plan.Count];
            var descriptors = new byte[TierDescriptorSize * plan.Count];
            if (stream.Read(descriptors, 0, descriptors.Length) != descriptors.Length)
            {
                stream.Dispose();
                return false;
            }

            for (var i = 0; i < tiers.Length; i++)
            {
                var span = descriptors.AsSpan(i * TierDescriptorSize);
                var step = BinaryPrimitives.ReadInt32LittleEndian(span);
                var slots = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);

                if (step != stepSeconds * plan[i].Multiplier || slots != plan[i].SlotCount)
                {
                    stream.Dispose();
                    return false;
                }

                tiers[i] = new TraceTier(step, slots, offset);
            }

            if (stream.Length < tiers[^1].Offset + ((long)tiers[^1].SlotCount * SlotSize))
            {
                stream.Dispose();
                return false;
            }

            file = new TraceFile(stream, path, stepSeconds, tiers);
            file.RecoverNewestSlot();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            unreadable = ex;
            return false;
        }
    }

    private static TraceFile Create(
        string path,
        int stepSeconds,
        IReadOnlyList<(int Multiplier, int SlotCount)> plan)
    {
        var tiers = new TraceTier[plan.Count];
        var offset = (long)HeaderSize + ((long)TierDescriptorSize * plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            tiers[i] = new TraceTier(stepSeconds * plan[i].Multiplier, plan[i].SlotCount, offset);
            offset += (long)plan[i].SlotCount * SlotSize;
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), stepSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), tiers.Length);
        stream.Write(header);

        var descriptors = new byte[TierDescriptorSize * tiers.Length];
        for (var i = 0; i < tiers.Length; i++)
        {
            var span = descriptors.AsSpan(i * TierDescriptorSize);
            BinaryPrimitives.WriteInt32LittleEndian(span, tiers[i].StepSeconds);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], tiers[i].SlotCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], tiers[i].Offset);
        }

        stream.Write(descriptors);

        // Pre-fill so a read of a slot never written returns a gap rather than
        // whatever the filesystem left there. Written a chunk at a time: at one
        // second resolution there are a great many slots.
        var empty = new byte[SlotSize];
        BinaryPrimitives.WriteInt64LittleEndian(empty, EmptySlot);
        foreach (var field in (int[])[MinimumOffset, MeanOffset, MaximumOffset, JitterOffset, JitterMaximumOffset])
        {
            BinaryPrimitives.WriteSingleLittleEndian(empty.AsSpan(field), float.NaN);
        }

        const int SlotsPerChunk = 4096;
        var chunk = new byte[SlotSize * SlotsPerChunk];
        for (var i = 0; i < SlotsPerChunk; i++)
        {
            empty.CopyTo(chunk.AsSpan(i * SlotSize));
        }

        foreach (var tier in tiers)
        {
            var remaining = tier.SlotCount;
            while (remaining > 0)
            {
                var slots = Math.Min(remaining, SlotsPerChunk);
                stream.Write(chunk, 0, slots * SlotSize);
                remaining -= slots;
            }
        }

        stream.Flush(true);
        return new TraceFile(stream, path, stepSeconds, tiers);
    }

    /// <summary>
    /// Records one probe. <paramref name="roundTripMs"/> is null for a lost probe;
    /// <paramref name="jitterMs"/> is the variation from the previous answer, or NaN
    /// when there is no meaningful previous answer to compare against.
    /// </summary>
    public void Record(long timestamp, double? roundTripMs, float jitterMs)
    {
        lock (_gate)
        {
            var slotIndex = timestamp / StepSeconds;

            // A clock that steps backwards would otherwise overwrite slots holding
            // newer data, and the trace would read as though time ran twice.
            if (_newestSlotIndex != long.MinValue && slotIndex < _newestSlotIndex)
            {
                return;
            }

            _newestSlotIndex = slotIndex;

            var value = roundTripMs is { } rtt ? (float)rtt : float.NaN;
            WriteSlot(
                Tiers[0],
                slotIndex,
                new TraceSample(
                    timestamp,
                    Sent: 1,
                    Lost: roundTripMs is null ? 1 : 0,
                    Minimum: value,
                    Mean: value,
                    Maximum: value,
                    Jitter: jitterMs,
                    JitterMaximum: jitterMs));

            for (var i = 1; i < Tiers.Count; i++)
            {
                Accumulate(i, timestamp, value, jitterMs, roundTripMs is null);
            }
        }
    }

    /// <summary>
    /// Folds one probe into a coarse tier's current bucket and rewrites that bucket's
    /// slot.
    ///
    /// Kept in memory and written forward rather than recomputed from the fine tier on
    /// every probe: at one probe a second, rereading a whole bucket each time would be
    /// sixty reads a second per target to produce a number we already have. The cost is
    /// that a restart mid-bucket starts that one bucket over, so the slot covering the
    /// restart describes only the part after it. One bucket, once, and the fine tier
    /// still holds the whole truth.
    /// </summary>
    private void Accumulate(int tierIndex, long timestamp, float value, float jitter, bool lost)
    {
        var tier = Tiers[tierIndex];
        var bucketIndex = timestamp / tier.StepSeconds;
        var bucket = _buckets[tierIndex];

        if (bucket is null || bucket.Index != bucketIndex)
        {
            bucket = new Bucket(bucketIndex);
            _buckets[tierIndex] = bucket;
        }

        bucket.Add(value, jitter, lost);
        WriteSlot(tier, bucketIndex, bucket.ToSample(bucketIndex * tier.StepSeconds));
    }

    private void WriteSlot(TraceTier tier, long slotIndex, TraceSample sample)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_slotBuffer, slotIndex);
        BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(MinimumOffset), sample.Minimum);
        BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(MeanOffset), sample.Mean);
        BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(MaximumOffset), sample.Maximum);
        BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(JitterOffset), sample.Jitter);
        BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(JitterMaximumOffset), sample.JitterMaximum);
        BinaryPrimitives.WriteInt32LittleEndian(_slotBuffer.AsSpan(SentOffset), sample.Sent);
        BinaryPrimitives.WriteInt32LittleEndian(_slotBuffer.AsSpan(LostOffset), sample.Lost);

        _stream.Seek(tier.Offset + ((slotIndex % tier.SlotCount) * SlotSize), SeekOrigin.Begin);
        _stream.Write(_slotBuffer);
    }

    private TraceSample? ReadSlot(TraceTier tier, long slotIndex)
    {
        _stream.Seek(tier.Offset + ((slotIndex % tier.SlotCount) * SlotSize), SeekOrigin.Begin);
        var read = 0;
        while (read < SlotSize)
        {
            var n = _stream.Read(_slotBuffer, read, SlotSize - read);
            if (n <= 0)
            {
                return null;
            }

            read += n;
        }

        if (BinaryPrimitives.ReadInt64LittleEndian(_slotBuffer) != slotIndex)
        {
            return null;
        }

        return new TraceSample(
            slotIndex * tier.StepSeconds,
            BinaryPrimitives.ReadInt32LittleEndian(_slotBuffer.AsSpan(SentOffset)),
            BinaryPrimitives.ReadInt32LittleEndian(_slotBuffer.AsSpan(LostOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(MinimumOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(MeanOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(MaximumOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(JitterOffset)),
            BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(JitterMaximumOffset)));
    }

    /// <summary>
    /// Picks the finest tier that still reaches back far enough for the requested
    /// span, falling back to the coarsest for anything longer than is kept.
    /// </summary>
    public int SelectTier(long spanSeconds)
    {
        for (var i = 0; i < Tiers.Count; i++)
        {
            if (Tiers[i].SpanSeconds >= spanSeconds)
            {
                return i;
            }
        }

        return Tiers.Count - 1;
    }

    /// <summary>
    /// Reads a period at the finest resolution that covers it. Slots never written, or
    /// long since overwritten, come back as gaps so a graph can break the line rather
    /// than drawing through an outage as though it were flat.
    /// </summary>
    public (IReadOnlyList<TraceSample> Samples, int StepSeconds) Read(long fromTimestamp, long toTimestamp)
    {
        var tierIndex = SelectTier(toTimestamp - fromTimestamp);
        return (Read(tierIndex, fromTimestamp, toTimestamp), Tiers[tierIndex].StepSeconds);
    }

    /// <summary>Reads a period from a specific tier.</summary>
    public IReadOnlyList<TraceSample> Read(int tierIndex, long fromTimestamp, long toTimestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tierIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tierIndex, Tiers.Count);

        var tier = Tiers[tierIndex];
        var results = new List<TraceSample>();

        lock (_gate)
        {
            var start = fromTimestamp / tier.StepSeconds;
            var end = toTimestamp / tier.StepSeconds;
            var oldest = end - tier.SlotCount + 1;
            if (start < oldest)
            {
                start = oldest;
            }

            for (var index = start; index <= end; index++)
            {
                results.Add(ReadSlot(tier, index) ?? TraceSample.Empty(index * tier.StepSeconds));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the newest slot already recorded, so the guard against a backwards clock
    /// survives a restart instead of resetting and allowing one rewrite of live data.
    /// </summary>
    private void RecoverNewestSlot()
    {
        var tier = Tiers[0];
        var newest = long.MinValue;

        for (var slot = 0; slot < tier.SlotCount; slot++)
        {
            _stream.Seek(tier.Offset + ((long)slot * SlotSize), SeekOrigin.Begin);
            if (_stream.Read(_slotBuffer, 0, SlotSize) != SlotSize)
            {
                break;
            }

            var stored = BinaryPrimitives.ReadInt64LittleEndian(_slotBuffer);
            if (stored != EmptySlot)
            {
                newest = Math.Max(newest, stored);
            }
        }

        _newestSlotIndex = newest;
    }

    /// <summary>
    /// Pushes writes out. Called on a timer rather than per probe: the measurement
    /// archives fsync every round because losing one costs five minutes of a year-long
    /// graph, but fsyncing once a second per target for a trace that is disposable by
    /// design would be a poor trade for the disk.
    /// </summary>
    public void Flush(bool toDisk)
    {
        lock (_gate)
        {
            _stream.Flush(toDisk);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Dispose();
        }
    }

    /// <summary>A coarse tier's bucket as it fills.</summary>
    private sealed class Bucket(long index)
    {
        private double _sum;
        private int _count;
        private float _min = float.NaN;
        private float _max = float.NaN;
        private double _jitterSum;
        private int _jitterCount;
        private float _jitterMax = float.NaN;
        private int _sent;
        private int _lost;

        public long Index { get; } = index;

        public void Add(float value, float jitter, bool lost)
        {
            _sent++;
            if (lost)
            {
                _lost++;
            }

            if (!float.IsNaN(value))
            {
                _sum += value;
                _count++;
                _min = float.IsNaN(_min) ? value : Math.Min(_min, value);
                _max = float.IsNaN(_max) ? value : Math.Max(_max, value);
            }

            if (!float.IsNaN(jitter))
            {
                _jitterSum += jitter;
                _jitterCount++;
                _jitterMax = float.IsNaN(_jitterMax) ? jitter : Math.Max(_jitterMax, jitter);
            }
        }

        public TraceSample ToSample(long timestamp) => new(
            timestamp,
            _sent,
            _lost,
            _min,
            _count == 0 ? float.NaN : (float)(_sum / _count),
            _max,
            _jitterCount == 0 ? float.NaN : (float)(_jitterSum / _jitterCount),
            _jitterMax);
    }
}
