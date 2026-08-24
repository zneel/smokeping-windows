using System.Buffers.Binary;

namespace SmokePing.Net.Storage;

/// <summary>Describes one archive (resolution tier) inside a <see cref="RoundRobinFile"/>.</summary>
/// <param name="StepSeconds">Seconds covered by a single slot.</param>
/// <param name="SlotCount">Number of slots before the archive wraps around.</param>
/// <param name="Offset">Byte offset of the archive's first slot within the file.</param>
public sealed record ArchiveInfo(int StepSeconds, int SlotCount, long Offset)
{
    /// <summary>Total time span the archive can hold, in seconds.</summary>
    public long SpanSeconds => (long)StepSeconds * SlotCount;
}

/// <summary>
/// A fixed-size, self-consolidating round-robin database, in the spirit of RRDtool
/// but purpose-built for SmokePing-style data. Every target gets one file whose size
/// is decided at creation time and never grows: new samples overwrite the oldest ones.
///
/// The file holds one base archive at the polling interval plus a number of coarser
/// archives. Coarser archives are recomputed from the base archive on every write,
/// which keeps consolidation self-healing (a missed poll or an unclean shutdown cannot
/// leave a bucket permanently wrong).
///
/// Layout:
///   header (64 bytes) | archive descriptors (16 bytes each) | slot data
/// Each slot is a fixed 60 bytes: slot index (8), sent (4), lost (4), 11 quantiles (44).
/// </summary>
public sealed class RoundRobinFile : IDisposable
{
    private const uint Magic = 0x53504442; // "SPDB"
    private const int FormatVersion = 1;
    private const int HeaderSize = 64;
    private const int ArchiveDescriptorSize = 16;
    private const int SlotSize = 8 + 4 + 4 + (Sample.QuantileCount * 4);

    /// <summary>Slot index written into unused slots.</summary>
    private const long EmptySlot = -1;

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly byte[] _slotBuffer = new byte[SlotSize];

    private RoundRobinFile(FileStream stream, string path, int stepSeconds, int pingsPerRound, ArchiveInfo[] archives)
    {
        _stream = stream;
        Path = path;
        StepSeconds = stepSeconds;
        PingsPerRound = pingsPerRound;
        Archives = archives;
    }

    public string Path { get; }

    /// <summary>Polling interval in seconds; the resolution of archive 0.</summary>
    public int StepSeconds { get; }

    /// <summary>Number of probes per round this file was created for.</summary>
    public int PingsPerRound { get; }

    public IReadOnlyList<ArchiveInfo> Archives { get; }

    /// <summary>
    /// Opens an existing file, or creates one when it is missing. If an existing file
    /// was written with an incompatible layout (for example the step or the archive
    /// plan changed in the configuration) it is moved aside and recreated, so a
    /// configuration change can never corrupt or silently misread history.
    /// </summary>
    /// <param name="archivePlan">
    /// Pairs of (step multiplier relative to <paramref name="stepSeconds"/>, slot count).
    /// The first entry must have a multiplier of 1.
    /// </param>
    public static RoundRobinFile OpenOrCreate(
        string path,
        int stepSeconds,
        int pingsPerRound,
        IReadOnlyList<(int Multiplier, int SlotCount)> archivePlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSeconds, 1);
        ArgumentNullException.ThrowIfNull(archivePlan);

        if (archivePlan.Count == 0 || archivePlan[0].Multiplier != 1)
        {
            throw new ArgumentException("The first archive must have a step multiplier of 1.", nameof(archivePlan));
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            if (TryOpenExisting(path, stepSeconds, pingsPerRound, archivePlan, out var existing))
            {
                return existing;
            }

            var backup = $"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Move(path, backup, overwrite: true);
        }

        return Create(path, stepSeconds, pingsPerRound, archivePlan);
    }

    private static bool TryOpenExisting(
        string path,
        int stepSeconds,
        int pingsPerRound,
        IReadOnlyList<(int Multiplier, int SlotCount)> archivePlan,
        out RoundRobinFile file)
    {
        file = null!;
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

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var fileStep = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            var filePings = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            var archiveCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));

            if (magic != Magic || version != FormatVersion || fileStep != stepSeconds ||
                filePings != pingsPerRound || archiveCount != archivePlan.Count)
            {
                stream.Dispose();
                return false;
            }

            var archives = new ArchiveInfo[archiveCount];
            var descriptors = new byte[ArchiveDescriptorSize * archiveCount];
            if (stream.Read(descriptors, 0, descriptors.Length) != descriptors.Length)
            {
                stream.Dispose();
                return false;
            }

            for (var i = 0; i < archiveCount; i++)
            {
                var span = descriptors.AsSpan(i * ArchiveDescriptorSize);
                var step = BinaryPrimitives.ReadInt32LittleEndian(span);
                var slots = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);

                if (step != stepSeconds * archivePlan[i].Multiplier || slots != archivePlan[i].SlotCount)
                {
                    stream.Dispose();
                    return false;
                }

                archives[i] = new ArchiveInfo(step, slots, offset);
            }

            var expectedLength = archives[^1].Offset + ((long)archives[^1].SlotCount * SlotSize);
            if (stream.Length < expectedLength)
            {
                stream.Dispose();
                return false;
            }

            file = new RoundRobinFile(stream, path, stepSeconds, pingsPerRound, archives);
            return true;
        }
        catch (IOException)
        {
            stream?.Dispose();
            return false;
        }
        catch (InvalidDataException)
        {
            stream?.Dispose();
            return false;
        }
    }

    private static RoundRobinFile Create(
        string path,
        int stepSeconds,
        int pingsPerRound,
        IReadOnlyList<(int Multiplier, int SlotCount)> archivePlan)
    {
        var archives = new ArchiveInfo[archivePlan.Count];
        var offset = (long)HeaderSize + ((long)ArchiveDescriptorSize * archivePlan.Count);
        for (var i = 0; i < archivePlan.Count; i++)
        {
            var (multiplier, slotCount) = archivePlan[i];
            archives[i] = new ArchiveInfo(stepSeconds * multiplier, slotCount, offset);
            offset += (long)slotCount * SlotSize;
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), stepSeconds);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), pingsPerRound);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), archives.Length);
        stream.Write(header);

        var descriptors = new byte[ArchiveDescriptorSize * archives.Length];
        for (var i = 0; i < archives.Length; i++)
        {
            var span = descriptors.AsSpan(i * ArchiveDescriptorSize);
            BinaryPrimitives.WriteInt32LittleEndian(span, archives[i].StepSeconds);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], archives[i].SlotCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], archives[i].Offset);
        }

        stream.Write(descriptors);

        // Pre-fill every slot as empty so partial reads never see uninitialised data.
        var empty = new byte[SlotSize];
        BinaryPrimitives.WriteInt64LittleEndian(empty, EmptySlot);
        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(empty.AsSpan(16 + (i * 4)), float.NaN);
        }

        foreach (var archive in archives)
        {
            for (var slot = 0; slot < archive.SlotCount; slot++)
            {
                stream.Write(empty);
            }
        }

        stream.Flush(true);
        return new RoundRobinFile(stream, path, stepSeconds, pingsPerRound, archives);
    }

    /// <summary>
    /// Stores one measurement round and refreshes every consolidated archive that
    /// covers it. <paramref name="timestamp"/> is snapped down to the base step.
    /// </summary>
    public void Write(long timestamp, int sent, int lost, float[] quantiles)
    {
        ArgumentNullException.ThrowIfNull(quantiles);
        if (quantiles.Length != Sample.QuantileCount)
        {
            throw new ArgumentException($"Expected {Sample.QuantileCount} quantiles.", nameof(quantiles));
        }

        lock (_gate)
        {
            var slotIndex = timestamp / StepSeconds;
            WriteSlot(Archives[0], slotIndex, sent, lost, quantiles);

            for (var i = 1; i < Archives.Count; i++)
            {
                Consolidate(i, timestamp);
            }

            _stream.Flush();
        }
    }

    /// <summary>
    /// Recomputes one bucket of a consolidated archive from the base archive.
    /// Averaging the quantile vectors gives the same shape a coarse SmokePing graph
    /// shows, and summing sent/lost keeps the loss ratio exact.
    /// </summary>
    private void Consolidate(int archiveIndex, long timestamp)
    {
        var archive = Archives[archiveIndex];
        var bucketIndex = timestamp / archive.StepSeconds;
        var bucketStart = bucketIndex * archive.StepSeconds;
        var bucketEnd = bucketStart + archive.StepSeconds;

        var vectors = new List<float[]>();
        var sent = 0;
        var lost = 0;

        for (var t = bucketStart; t < bucketEnd; t += StepSeconds)
        {
            var sample = ReadSlot(Archives[0], t / StepSeconds);
            if (sample is null)
            {
                continue;
            }

            sent += sample.Sent;
            lost += sample.Lost;
            vectors.Add(sample.Quantiles);
        }

        if (sent == 0)
        {
            return;
        }

        WriteSlot(archive, bucketIndex, sent, lost, Quantiles.Average(vectors));
    }

    private void WriteSlot(ArchiveInfo archive, long slotIndex, int sent, int lost, float[] quantiles)
    {
        var position = archive.Offset + ((slotIndex % archive.SlotCount) * SlotSize);
        BinaryPrimitives.WriteInt64LittleEndian(_slotBuffer, slotIndex);
        BinaryPrimitives.WriteInt32LittleEndian(_slotBuffer.AsSpan(8), sent);
        BinaryPrimitives.WriteInt32LittleEndian(_slotBuffer.AsSpan(12), lost);
        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_slotBuffer.AsSpan(16 + (i * 4)), quantiles[i]);
        }

        _stream.Seek(position, SeekOrigin.Begin);
        _stream.Write(_slotBuffer);
    }

    /// <summary>
    /// Reads a slot, returning null when the slot holds no data or holds a different
    /// (older, not yet overwritten) period than the one requested.
    /// </summary>
    private Sample? ReadSlot(ArchiveInfo archive, long slotIndex)
    {
        var position = archive.Offset + ((slotIndex % archive.SlotCount) * SlotSize);
        _stream.Seek(position, SeekOrigin.Begin);
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

        var storedIndex = BinaryPrimitives.ReadInt64LittleEndian(_slotBuffer);
        if (storedIndex != slotIndex)
        {
            return null;
        }

        var quantiles = new float[Sample.QuantileCount];
        for (var i = 0; i < Sample.QuantileCount; i++)
        {
            quantiles[i] = BinaryPrimitives.ReadSingleLittleEndian(_slotBuffer.AsSpan(16 + (i * 4)));
        }

        return new Sample
        {
            Timestamp = slotIndex * archive.StepSeconds,
            Sent = BinaryPrimitives.ReadInt32LittleEndian(_slotBuffer.AsSpan(8)),
            Lost = BinaryPrimitives.ReadInt32LittleEndian(_slotBuffer.AsSpan(12)),
            Quantiles = quantiles,
        };
    }

    /// <summary>
    /// Picks the finest archive that can cover <paramref name="spanSeconds"/> of history,
    /// falling back to the coarsest one for ranges longer than anything stored.
    /// </summary>
    public int SelectArchive(long spanSeconds)
    {
        for (var i = 0; i < Archives.Count; i++)
        {
            if (Archives[i].SpanSeconds >= spanSeconds)
            {
                return i;
            }
        }

        return Archives.Count - 1;
    }

    /// <summary>
    /// Reads every slot between <paramref name="fromTimestamp"/> and
    /// <paramref name="toTimestamp"/> (inclusive of the start bucket, exclusive of the
    /// end). Gaps are returned as empty samples so a graph can show them as holes
    /// rather than joining across them.
    /// </summary>
    public IReadOnlyList<Sample> Read(int archiveIndex, long fromTimestamp, long toTimestamp)
    {
        if (archiveIndex < 0 || archiveIndex >= Archives.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveIndex));
        }

        var archive = Archives[archiveIndex];
        var results = new List<Sample>();

        lock (_gate)
        {
            var start = fromTimestamp / archive.StepSeconds;
            var end = toTimestamp / archive.StepSeconds;
            var oldest = end - archive.SlotCount + 1;
            if (start < oldest)
            {
                start = oldest;
            }

            for (var index = start; index <= end; index++)
            {
                results.Add(ReadSlot(archive, index) ?? Sample.Empty(index * archive.StepSeconds));
            }
        }

        return results;
    }

    /// <summary>Returns the most recent sample within the base archive, if there is one.</summary>
    public Sample? ReadLatest(long now)
    {
        lock (_gate)
        {
            var slotIndex = now / StepSeconds;
            for (var i = 0; i < Archives[0].SlotCount; i++)
            {
                var sample = ReadSlot(Archives[0], slotIndex - i);
                if (sample is not null && sample.Sent > 0)
                {
                    return sample;
                }
            }
        }

        return null;
    }

    public void Dispose() => _stream.Dispose();
}
