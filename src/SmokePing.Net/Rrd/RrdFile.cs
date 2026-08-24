using System.Buffers.Binary;
using System.Text;

namespace SmokePing.Net.Rrd;

/// <summary>Thrown when a file is not a readable RRD.</summary>
public sealed class RrdFormatException : Exception
{
    public RrdFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads and writes RRDtool database files, so an existing SmokePing installation's
/// history opens here and what this writes stays readable by <c>rrdtool</c>.
///
/// The format is not a wire format: it is a C struct image, so every offset follows
/// from the ABI of the machine that wrote it. This implements the 64-bit
/// little-endian layout, which is what Linux/amd64 and Windows/x64 both produce, and
/// refuses anything else rather than silently misreading it - the float cookie at
/// offset 0x10 catches a foreign byte order, a foreign floating point format and a
/// foreign double alignment all at once, which is exactly what rrdtool uses it for.
/// </summary>
public sealed class RrdFile
{
    private const int StatHeadSize = 128;
    private const int DsDefSize = 120;
    private const int RraDefSize = 120;
    private const int LiveHeadSize = 16;
    private const int PdpPrepSize = 112;
    private const int CdpPrepSize = 80;
    private const int RraPtrSize = 8;
    private const int NameSize = 20;

    /// <summary>Identifies the file, including its terminating NUL.</summary>
    private static readonly byte[] Cookie = [(byte)'R', (byte)'R', (byte)'D', 0];

    /// <summary>
    /// 8.642135e130. Read as a native double and compared exactly; any difference means
    /// the file came from an architecture this cannot read.
    /// </summary>
    private const ulong FloatCookieBits = 0x5B1F2B43C7C0252FUL;

    private RrdFile(
        string version,
        long stepSeconds,
        IReadOnlyList<RrdDataSource> dataSources,
        IReadOnlyList<RrdArchive> archives,
        long lastUpdate)
    {
        Version = version;
        StepSeconds = stepSeconds;
        DataSources = dataSources;
        Archives = archives;
        LastUpdate = lastUpdate;
    }

    public string Version { get; }

    /// <summary>Seconds between primary data points.</summary>
    public long StepSeconds { get; }

    public IReadOnlyList<RrdDataSource> DataSources { get; }

    public IReadOnlyList<RrdArchive> Archives { get; }

    /// <summary>Unix time of the last update.</summary>
    public long LastUpdate { get; private set; }

    /// <summary>Ring buffer positions, one per archive.</summary>
    private int[] _currentRow = [];

    /// <summary>Consolidated values, indexed [archive][row * dsCount + ds].</summary>
    private double[][] _data = [];

    /// <summary>Accumulator state per archive and data source, kept so rrdtool can continue the file.</summary>
    private double[][] _cdpValue = [];

    private long[][] _cdpUnknown = [];

    /// <summary>Creates an empty database, as <c>rrdtool create</c> would.</summary>
    public static RrdFile Create(
        long stepSeconds,
        IReadOnlyList<RrdDataSource> dataSources,
        IReadOnlyList<RrdArchive> archives,
        long start)
    {
        ArgumentNullException.ThrowIfNull(dataSources);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepSeconds, 1);

        if (dataSources.Count == 0)
        {
            throw new ArgumentException("An RRD needs at least one data source.", nameof(dataSources));
        }

        if (archives.Count == 0)
        {
            throw new ArgumentException("An RRD needs at least one archive.", nameof(archives));
        }

        foreach (var ds in dataSources)
        {
            if (Encoding.ASCII.GetByteCount(ds.Name) >= NameSize)
            {
                throw new ArgumentException($"Data source name '{ds.Name}' is longer than 19 bytes.", nameof(dataSources));
            }
        }

        var file = new RrdFile("0003", stepSeconds, dataSources, archives, start)
        {
            _currentRow = new int[archives.Count],
            _data = new double[archives.Count][],
            _cdpValue = new double[archives.Count][],
            _cdpUnknown = new long[archives.Count][],
        };

        for (var r = 0; r < archives.Count; r++)
        {
            // rrdtool starts at the last row so the first update wraps to row 0.
            file._currentRow[r] = archives[r].RowCount - 1;
            file._data[r] = new double[(long)archives[r].RowCount * dataSources.Count];
            Array.Fill(file._data[r], double.NaN);

            file._cdpValue[r] = new double[dataSources.Count];
            Array.Fill(file._cdpValue[r], double.NaN);
            file._cdpUnknown[r] = new long[dataSources.Count];
        }

        return file;
    }

    /// <summary>Reads a database from disk.</summary>
    /// <exception cref="RrdFormatException">The file is not a readable RRD.</exception>
    public static RrdFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>Reads a database from bytes. Exposed for tests.</summary>
    public static RrdFile Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < StatHeadSize)
        {
            throw new RrdFormatException("File is too short to be an RRD.");
        }

        if (!bytes.AsSpan(0, 4).SequenceEqual(Cookie))
        {
            throw new RrdFormatException("Not an RRD file: the RRD cookie is missing.");
        }

        var version = ReadString(bytes, 4, 5);

        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10)) != FloatCookieBits)
        {
            throw new RrdFormatException(
                "This RRD was written on a different architecture - a different byte order, " +
                "floating point format or struct alignment - and cannot be read here.");
        }

        var dsCount = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x18)));
        var rraCount = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x20)));
        var step = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x28)));

        if (dsCount is < 1 or > 10000 || rraCount is < 1 or > 10000)
        {
            throw new RrdFormatException($"Implausible RRD header: {dsCount} data sources, {rraCount} archives.");
        }

        // Versions before 3 have a 32-bit live_head; SmokePing has never written one.
        if (version is not ("0003" or "0004" or "0005"))
        {
            throw new RrdFormatException($"Unsupported RRD version '{version}'.");
        }

        var offset = StatHeadSize;

        var dataSources = new RrdDataSource[dsCount];
        for (var i = 0; i < dsCount; i++)
        {
            var at = offset + (i * DsDefSize);
            dataSources[i] = new RrdDataSource(
                ReadString(bytes, at, NameSize),
                ReadString(bytes, at + NameSize, NameSize),
                checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 0x28))),
                BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 0x30)),
                BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 0x38)));
        }

        offset += dsCount * DsDefSize;

        var archives = new RrdArchive[rraCount];
        for (var i = 0; i < rraCount; i++)
        {
            var at = offset + (i * RraDefSize);
            archives[i] = new RrdArchive(
                ReadString(bytes, at, NameSize),
                checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 0x18))),
                checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 0x20))),
                BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 0x28)));
        }

        offset += rraCount * RraDefSize;

        var lastUpdate = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset)));
        offset += LiveHeadSize;
        offset += dsCount * PdpPrepSize;

        var file = new RrdFile(version, step, dataSources, archives, lastUpdate)
        {
            _currentRow = new int[rraCount],
            _data = new double[rraCount][],
            _cdpValue = new double[rraCount][],
            _cdpUnknown = new long[rraCount][],
        };

        // cdp_prep is archive-major: index rra * dsCount + ds.
        for (var r = 0; r < rraCount; r++)
        {
            file._cdpValue[r] = new double[dsCount];
            file._cdpUnknown[r] = new long[dsCount];

            for (var d = 0; d < dsCount; d++)
            {
                var at = offset + (((r * dsCount) + d) * CdpPrepSize);
                file._cdpValue[r][d] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at));
                file._cdpUnknown[r][d] = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)));
            }
        }

        offset += rraCount * dsCount * CdpPrepSize;

        for (var r = 0; r < rraCount; r++)
        {
            file._currentRow[r] = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + (r * RraPtrSize))));
        }

        offset += rraCount * RraPtrSize;

        for (var r = 0; r < rraCount; r++)
        {
            var count = (long)archives[r].RowCount * dsCount;
            var values = new double[count];

            for (var i = 0L; i < count; i++)
            {
                var at = offset + (i * 8);
                if (at + 8 > bytes.Length)
                {
                    throw new RrdFormatException("The RRD is truncated: the data area is shorter than the header describes.");
                }

                values[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(checked((int)at)));
            }

            file._data[r] = values;
            offset += checked((int)(count * 8));
        }

        return file;
    }

    /// <summary>Byte length of the file this would write.</summary>
    public long FileSize =>
        HeaderSize + (8L * DataSources.Count * Archives.Sum(a => (long)a.RowCount));

    private long HeaderSize =>
        StatHeadSize
        + ((long)DsDefSize * DataSources.Count)
        + ((long)RraDefSize * Archives.Count)
        + LiveHeadSize
        + ((long)PdpPrepSize * DataSources.Count)
        + ((long)CdpPrepSize * Archives.Count * DataSources.Count)
        + ((long)RraPtrSize * Archives.Count);

    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, ToBytes());
    }

    /// <summary>Serialises the database. Exposed for tests.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[FileSize];

        Cookie.CopyTo(bytes, 0);
        WriteString(bytes, 4, 5, Version);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x10), FloatCookieBits);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x18), (ulong)DataSources.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x20), (ulong)Archives.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x28), (ulong)StepSeconds);

        var offset = StatHeadSize;

        for (var i = 0; i < DataSources.Count; i++)
        {
            var ds = DataSources[i];
            var at = offset + (i * DsDefSize);
            WriteString(bytes, at, NameSize, ds.Name);
            WriteString(bytes, at + NameSize, NameSize, ds.Type);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 0x28), (ulong)ds.HeartbeatSeconds);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + 0x30), ds.Minimum);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + 0x38), ds.Maximum);
        }

        offset += DataSources.Count * DsDefSize;

        for (var i = 0; i < Archives.Count; i++)
        {
            var archive = Archives[i];
            var at = offset + (i * RraDefSize);
            WriteString(bytes, at, NameSize, archive.ConsolidationFunction);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 0x18), (ulong)archive.RowCount);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 0x20), (ulong)archive.StepsPerRow);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + 0x28), archive.XFilesFactor);
        }

        offset += Archives.Count * RraDefSize;

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), (ulong)LastUpdate);
        offset += LiveHeadSize;

        // pdp_prep: last_ds text and the in-progress accumulator. Left empty, which
        // rrdtool reads as "nothing pending" - every value written here lands on a
        // step boundary, so there is never a partial primary data point to carry.
        offset += DataSources.Count * PdpPrepSize;

        for (var r = 0; r < Archives.Count; r++)
        {
            for (var d = 0; d < DataSources.Count; d++)
            {
                var at = offset + (((r * DataSources.Count) + d) * CdpPrepSize);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at), _cdpValue[r][d]);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 8), (ulong)_cdpUnknown[r][d]);

                // scratch[8] is the value rrdtool would write to the row next.
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + (8 * 8)), double.NaN);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + (9 * 8)), double.NaN);
            }
        }

        offset += Archives.Count * DataSources.Count * CdpPrepSize;

        for (var r = 0; r < Archives.Count; r++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + (r * RraPtrSize)), (ulong)_currentRow[r]);
        }

        offset += Archives.Count * RraPtrSize;

        for (var r = 0; r < Archives.Count; r++)
        {
            var values = _data[r];
            for (var i = 0L; i < values.LongLength; i++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(checked((int)(offset + (i * 8)))), values[i]);
            }

            offset += checked((int)(values.LongLength * 8));
        }

        return bytes;
    }

    /// <summary>Seconds covered by one row of an archive.</summary>
    public long RowStep(int archiveIndex) => Archives[archiveIndex].StepsPerRow * StepSeconds;

    /// <summary>
    /// Timestamp of the newest row of an archive. A row's timestamp is the end of the
    /// interval it covers, so the row holds the value for (t - rowStep, t].
    /// </summary>
    public long NewestRowTime(int archiveIndex)
    {
        var rowStep = RowStep(archiveIndex);
        return LastUpdate - Modulo(LastUpdate, rowStep);
    }

    /// <summary>Reads an archive in chronological order.</summary>
    public IReadOnlyList<RrdRow> ReadArchive(int archiveIndex)
    {
        if (archiveIndex < 0 || archiveIndex >= Archives.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveIndex));
        }

        var archive = Archives[archiveIndex];
        var rowStep = RowStep(archiveIndex);
        var newest = NewestRowTime(archiveIndex);
        var dsCount = DataSources.Count;
        var rows = new List<RrdRow>(archive.RowCount);

        // Chronological order starts one past the current row and wraps.
        for (var i = 0; i < archive.RowCount; i++)
        {
            var row = (_currentRow[archiveIndex] + 1 + i) % archive.RowCount;
            var timestamp = newest - ((long)(archive.RowCount - 1 - i) * rowStep);

            var values = new double[dsCount];
            Array.Copy(_data[archiveIndex], (long)row * dsCount, values, 0, dsCount);
            rows.Add(new RrdRow(timestamp, values));
        }

        return rows;
    }

    /// <summary>
    /// Appends one primary data point, advancing every archive whose row it completes.
    ///
    /// Values are consolidated exactly as rrdtool does: accumulated per archive, then
    /// written when the row's last step arrives, with the row left unknown if more of
    /// its steps were unknown than the archive's xfiles factor allows.
    /// </summary>
    public void Update(long timestamp, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != DataSources.Count)
        {
            throw new ArgumentException(
                $"Expected {DataSources.Count} values, got {values.Count}.",
                nameof(values));
        }

        if (timestamp <= LastUpdate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                $"An RRD only moves forwards: {timestamp} is not after the last update {LastUpdate}.");
        }

        // rrdtool accepts an update at any moment and splits it across the two steps
        // it straddles. That interpolation is not implemented here, because everything
        // this writes is measured on a step boundary - so an unaligned timestamp means
        // a caller has got something wrong, and silently drifting the archives would
        // hide it.
        if (timestamp % StepSeconds != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                $"{timestamp} is not on a {StepSeconds} second boundary. Updates between steps, which " +
                "rrdtool spreads across both, are not supported.");
        }

        var step = timestamp / StepSeconds;

        for (var r = 0; r < Archives.Count; r++)
        {
            var archive = Archives[r];

            for (var d = 0; d < DataSources.Count; d++)
            {
                var value = ClampToRange(values[d], DataSources[d]);
                Accumulate(r, d, value, archive.ConsolidationFunction);
            }

            // The row closes on the step that is its last.
            if (step % archive.StepsPerRow != 0)
            {
                continue;
            }

            var row = (_currentRow[r] + 1) % archive.RowCount;
            _currentRow[r] = row;

            for (var d = 0; d < DataSources.Count; d++)
            {
                _data[r][((long)row * DataSources.Count) + d] = Consolidate(r, d, archive);
                _cdpValue[r][d] = double.NaN;
                _cdpUnknown[r][d] = 0;
            }
        }

        LastUpdate = timestamp;
    }

    private void Accumulate(int archiveIndex, int dataSource, double value, string consolidationFunction)
    {
        if (double.IsNaN(value))
        {
            _cdpUnknown[archiveIndex][dataSource]++;
            return;
        }

        var current = _cdpValue[archiveIndex][dataSource];
        _cdpValue[archiveIndex][dataSource] = double.IsNaN(current)
            ? value
            : consolidationFunction.ToUpperInvariant() switch
            {
                "MIN" or "MINIMUM" => Math.Min(current, value),
                "MAX" or "MAXIMUM" => Math.Max(current, value),
                "LAST" => value,
                _ => current + value,
            };
    }

    private double Consolidate(int archiveIndex, int dataSource, RrdArchive archive)
    {
        var unknown = _cdpUnknown[archiveIndex][dataSource];

        // The xfiles factor, exactly as rrdtool applies it: strictly more unknown
        // steps than xff allows makes the row unknown, rather than an average of
        // whatever happened to arrive. With xff 0.5 and two steps per row, one
        // unknown step is still a known row; at xff 0.49 it is not.
        if (unknown > archive.XFilesFactor * archive.StepsPerRow)
        {
            return double.NaN;
        }

        var value = _cdpValue[archiveIndex][dataSource];
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        return archive.ConsolidationFunction.ToUpperInvariant() is "AVERAGE"
            ? value / (archive.StepsPerRow - unknown)
            : value;
    }

    /// <summary>A value outside the data source's declared range is unknown, not clamped.</summary>
    private static double ClampToRange(double value, RrdDataSource dataSource)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (!double.IsNaN(dataSource.Minimum) && value < dataSource.Minimum)
        {
            return double.NaN;
        }

        if (!double.IsNaN(dataSource.Maximum) && value > dataSource.Maximum)
        {
            return double.NaN;
        }

        return value;
    }

    private static long Modulo(long value, long modulus) => ((value % modulus) + modulus) % modulus;

    private static string ReadString(byte[] bytes, int offset, int length)
    {
        var span = bytes.AsSpan(offset, length);
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? span : span[..end]);
    }

    private static void WriteString(byte[] bytes, int offset, int length, string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        if (encoded.Length >= length)
        {
            throw new ArgumentException($"'{value}' does not fit in {length} bytes.", nameof(value));
        }

        encoded.CopyTo(bytes, offset);
    }
}
