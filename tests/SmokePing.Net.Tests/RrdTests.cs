using System.Diagnostics;
using System.Globalization;
using SmokePing.Net.Rrd;

namespace SmokePing.Net.Tests;

/// <summary>
/// Tests for RRDtool file compatibility.
///
/// Where <c>rrdtool</c> is on PATH the tests use it as the oracle: a file it wrote must
/// read back identically here, and a file written here must be one it can read, fetch
/// from and update. Without it the structural tests still run, so the suite works on a
/// machine that has no rrdtool.
/// </summary>
public static class RrdTests
{
    private static readonly RrdDataSource[] SmokePingSchema =
    [
        RrdDataSource.Gauge("uptime", 600, 0),
        RrdDataSource.Gauge("loss", 600, 0, 20),
        RrdDataSource.Gauge("median", 600, 0),
        RrdDataSource.Gauge("ping1", 600, 0),
        RrdDataSource.Gauge("ping2", 600, 0),
    ];

    private static readonly RrdArchive[] TwoArchives =
    [
        new RrdArchive("AVERAGE", 10, 1),
        new RrdArchive("AVERAGE", 5, 2),
    ];

    public static void Register(TestRunner runner)
    {
        runner.Add("Rrd: the file is exactly the size the layout implies", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);

            // 128 header + 5*120 ds + 2*120 rra + 16 live + 5*112 pdp
            // + 2*5*80 cdp + 2*8 ptr = 2360, plus 8 * 5 * (10 + 5) = 600 of data.
            Assert.Equal(2960L, file.FileSize, "the same size rrdtool produces for this schema");
            Assert.Equal(2960, file.ToBytes().Length, "and that is what gets written");
        });

        runner.Add("Rrd: a written file reads back with everything intact", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);
            file.Update(1000000500, [100, 1, 11, 10, 12]);
            file.Update(1000000800, [200, 2, 12, 11, 13]);

            var reread = RrdFile.Read(file.ToBytes());

            Assert.Equal("0003", reread.Version, "the version rrdtool 1.7 writes");
            Assert.Equal(300L, reread.StepSeconds, "the step");
            Assert.Equal(1000000800L, reread.LastUpdate, "the last update");
            Assert.Equal(5, reread.DataSources.Count, "all the data sources");
            Assert.Equal("median", reread.DataSources[2].Name, "with their names");
            Assert.Equal(600L, reread.DataSources[1].HeartbeatSeconds, "and heartbeats");
            Assert.Close(20, reread.DataSources[1].Maximum, 0.001, "and bounds");
            Assert.True(double.IsNaN(reread.DataSources[0].Maximum), "an unbounded maximum stays unknown");
            Assert.Equal(2, reread.Archives.Count, "both archives");
            Assert.Close(0.5, reread.Archives[0].XFilesFactor, 0.0001, "with their xfiles factor");
        });

        runner.Add("Rrd: rows come back in chronological order ending at the last update", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);
            for (var i = 1; i <= 4; i++)
            {
                file.Update(1000000200 + (i * 300), [i * 100, 0, 10 + i, 9 + i, 11 + i]);
            }

            var rows = file.ReadArchive(0);

            Assert.Equal(10, rows.Count, "one entry per row of the archive");
            Assert.Equal(1000001400L, rows[^1].Timestamp, "the newest row ends at the last update");
            Assert.Close(400, rows[^1].Values[0], 0.001, "carrying the newest value");
            Assert.Close(300, rows[^2].Values[0], 0.001, "and the one before it");

            for (var i = 1; i < rows.Count; i++)
            {
                Assert.Equal(300L, rows[i].Timestamp - rows[i - 1].Timestamp, "rows are one step apart");
            }
        });

        runner.Add("Rrd: consolidation averages, and the xfiles factor decides unknown", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, [new RrdArchive("AVERAGE", 4, 2)], 1000000200);

            file.Update(1000000500, [10, 0, 10, 10, 10]);
            file.Update(1000000800, [20, 0, 20, 20, 20]);

            var rows = file.ReadArchive(0);
            Assert.Close(15, rows[^1].Values[0], 0.001, "two steps average into one row");

            // One unknown of two, at xff 0.5, is still a known row: rrdtool's test is
            // strictly greater than, not greater or equal.
            file.Update(1000001100, [double.NaN, 0, 10, 10, 10]);
            file.Update(1000001400, [30, 0, 30, 30, 30]);

            Assert.Close(30, file.ReadArchive(0)[^1].Values[0], 0.001, "the row survives one unknown step");
        });

        runner.Add("Rrd: a value outside the declared range is unknown, not clamped", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, [new RrdArchive("AVERAGE", 4, 1)], 1000000200);

            // loss is declared 0..20; 25 is not a smaller loss, it is nonsense.
            file.Update(1000000500, [100, 25, 10, 10, 10]);

            Assert.True(double.IsNaN(file.ReadArchive(0)[^1].Values[1]), "out of range means unknown");
            Assert.Close(100, file.ReadArchive(0)[^1].Values[0], 0.001, "other data sources are unaffected");
        });

        runner.Add("Rrd: an RRD only moves forwards", () =>
        {
            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);
            file.Update(1000000500, [1, 0, 1, 1, 1]);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => file.Update(1000000200, [1, 0, 1, 1, 1]),
                "an update at or before the last one is refused, as rrdtool refuses it");
        });

        runner.Add("Rrd: a file from another architecture is refused, not misread", () =>
        {
            var bytes = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200).ToBytes();

            // Byte-reverse the float cookie, which is how rrdtool itself detects a
            // foreign byte order, floating point format or double alignment.
            Array.Reverse(bytes, 0x10, 8);

            var error = Assert.Throws<RrdFormatException>(
                () => RrdFile.Read(bytes),
                "reading it anyway would silently produce nonsense");

            Assert.Contains(error.Message, "architecture", "and the message says why");
        });

        runner.Add("Rrd: a file that is not an RRD is refused", () =>
        {
            var bytes = new byte[4096];
            Assert.Throws<RrdFormatException>(() => RrdFile.Read(bytes), "no cookie, no RRD");
            Assert.Throws<RrdFormatException>(() => RrdFile.Read([1, 2, 3]), "and a stub is too short");
        });

        runner.Add("Rrd: rrdtool reads what this writes", () =>
        {
            if (!HasRrdTool())
            {
                return;
            }

            using var directory = new TempDirectory();
            var path = directory.File("mine.rrd");

            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);
            for (var i = 1; i <= 8; i++)
            {
                file.Update(1000000200 + (i * 300), [i * 100, i % 3, 10 + i, 9 + i, 11 + i]);
            }

            file.Write(path);

            var info = RunRrdTool("info", path);
            Assert.Contains(info, "rrd_version = \"0003\"", "rrdtool recognises the version");
            Assert.Contains(info, "step = 300", "and the step");
            Assert.Contains(info, "ds[median].type = \"GAUGE\"", "and the data sources");

            var fetched = RunRrdTool("fetch", path, "AVERAGE", "--start", "1000000200", "--end", "1000002600");
            Assert.Contains(fetched, "1000000500:", "and can fetch the values back");
        });

        runner.Add("Rrd: this reads what rrdtool writes, row for row", () =>
        {
            if (!HasRrdTool())
            {
                return;
            }

            using var directory = new TempDirectory();
            var path = directory.File("theirs.rrd");

            RunRrdTool(
                "create", path, "--start", "1000000200", "--step", "300",
                "DS:uptime:GAUGE:600:0:U", "DS:loss:GAUGE:600:0:20", "DS:median:GAUGE:600:0:U",
                "DS:ping1:GAUGE:600:0:U", "DS:ping2:GAUGE:600:0:U",
                "RRA:AVERAGE:0.5:1:10", "RRA:AVERAGE:0.5:2:5");

            for (var i = 1; i <= 8; i++)
            {
                var t = 1000000200 + (i * 300);
                RunRrdTool("update", path, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{t}:{i * 100}:{i % 3}:{10 + i}:{9 + i}:{11 + i}"));
            }

            var file = RrdFile.Read(path);
            Assert.Equal(1000002600L, file.LastUpdate, "the last update matches");
            Assert.Equal(5, file.DataSources.Count, "the data sources match");

            var theirs = DumpRows(RunRrdTool("dump", path));
            var mine = new List<string>();

            for (var r = 0; r < file.Archives.Count; r++)
            {
                foreach (var row in file.ReadArchive(r))
                {
                    mine.Add(Format(row.Timestamp, row.Values));
                }
            }

            Assert.Equal(theirs.Count, mine.Count, "the same number of rows");
            for (var i = 0; i < theirs.Count; i++)
            {
                Assert.Equal(theirs[i], mine[i], $"row {i} matches rrdtool exactly");
            }
        });

        RegisterStoreTests(runner);

        runner.Add("Rrd: rrdtool can update a file this wrote, and this reads the result", () =>
        {
            if (!HasRrdTool())
            {
                return;
            }

            using var directory = new TempDirectory();
            var path = directory.File("shared.rrd");

            var file = RrdFile.Create(300, SmokePingSchema, TwoArchives, 1000000200);
            for (var i = 1; i <= 8; i++)
            {
                file.Update(1000000200 + (i * 300), [i * 100, i % 3, 10 + i, 9 + i, 11 + i]);
            }

            file.Write(path);

            // The real test of the header: rrdtool has to understand the accumulator
            // state well enough to carry on writing into it.
            for (var i = 9; i <= 11; i++)
            {
                var t = 1000000200 + (i * 300);
                RunRrdTool("update", path, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{t}:{i * 100}:{i % 3}:{10 + i}:{9 + i}:{11 + i}"));
            }

            var reread = RrdFile.Read(path);
            Assert.Equal(1000003500L, reread.LastUpdate, "rrdtool's updates landed");

            var theirs = DumpRows(RunRrdTool("dump", path));
            var mine = new List<string>();

            for (var r = 0; r < reread.Archives.Count; r++)
            {
                foreach (var row in reread.ReadArchive(r))
                {
                    mine.Add(Format(row.Timestamp, row.Values));
                }
            }

            for (var i = 0; i < theirs.Count; i++)
            {
                Assert.Equal(theirs[i], mine[i], $"row {i} still matches after rrdtool wrote to it");
            }
        });
    }

    private static void RegisterStoreTests(TestRunner runner)
    {
        runner.Add("RrdStore: the schema is the one upstream creates", () =>
        {
            var schema = RrdTargetStore.SchemaFor(20, 300);

            Assert.Equal(23, schema.Count, "uptime, loss, median and one per probe");
            Assert.Equal("uptime", schema[0].Name, "uptime first");
            Assert.Equal("loss", schema[1].Name, "then loss");
            Assert.Equal("median", schema[2].Name, "then median");
            Assert.Equal("ping1", schema[3].Name, "then the probes");
            Assert.Equal("ping20", schema[^1].Name, "up to the last");
            Assert.Close(20, schema[1].Maximum, 0.001, "loss is bounded by the round size");
            Assert.Equal(600L, schema[0].HeartbeatSeconds, "the heartbeat is twice the step");
        });

        runner.Add("RrdStore: a round is laid out sorted and centred, as upstream lays it out", () =>
        {
            // Six probes, two lost. Upstream puts int(2/2)=1 unknown first, then the
            // four received values sorted, then the remaining unknown.
            var values = RrdTargetStore.BuildValues(6, [30.0, null, 10.0, 40.0, null, 20.0]);

            Assert.Close(2, values[1], 0.001, "two probes were lost");
            Assert.Close(30, values[2], 0.001, "the median is the middle received probe by position");

            var probes = values.Skip(3).ToArray();
            Assert.True(double.IsNaN(probes[0]), "the first slot is a lost probe");
            Assert.Close(10, probes[1], 0.001, "then the received values in order");
            Assert.Close(20, probes[2], 0.001, "sorted ascending");
            Assert.Close(30, probes[3], 0.001, "still ascending");
            Assert.Close(40, probes[4], 0.001, "up to the slowest");
            Assert.True(double.IsNaN(probes[5]), "and the last slot is the other lost probe");
        });

        runner.Add("RrdStore: a clean round fills every probe slot", () =>
        {
            var values = RrdTargetStore.BuildValues(3, [10.0, 20.0, 30.0]);

            Assert.Close(0, values[1], 0.001, "nothing was lost");
            Assert.Close(20, values[2], 0.001, "the median is the middle one");
            Assert.False(values.Skip(3).Any(double.IsNaN), "and no slot is unknown");
        });

        runner.Add("RrdStore: a total loss records the loss and no times", () =>
        {
            var values = RrdTargetStore.BuildValues(3, [null, null, null]);

            Assert.Close(3, values[1], 0.001, "all three lost");
            Assert.True(double.IsNaN(values[2]), "and there is no median");
            Assert.True(values.Skip(3).All(double.IsNaN), "nor any probe times");
        });

        runner.Add("RrdStore: a round survives being written and read back", () =>
        {
            using var directory = new TempDirectory();
            var store = new RrdTargetStore(directory.Path, [(1, 20)]);
            var target = Target(pings: 5, step: 300);

            var start = 1000000200L;
            for (var i = 1; i <= 5; i++)
            {
                store.Write(target, start + (i * 300), [10.0 + i, 20.0 + i, 30.0 + i, 40.0 + i, null]);
            }

            var samples = store.Read(target, start, start + (5 * 300));
            var last = samples[^1];

            Assert.Equal(5, last.Sent, "the round size comes back");
            Assert.Equal(1, last.Lost, "and the loss");
            Assert.Close(35, last.Median!.Value, 0.001, "and the median");
            Assert.True(last.Quantiles.Any(q => float.IsNaN(q)), "the lost probe leaves a hole in the spread");
        });

        runner.Add("RrdStore: rrdtool can read a file the store wrote", () =>
        {
            if (!HasRrdTool())
            {
                return;
            }

            using var directory = new TempDirectory();
            var store = new RrdTargetStore(directory.Path, [(1, 20)]);
            var target = Target(pings: 5, step: 300);

            var start = 1000000200L;
            for (var i = 1; i <= 5; i++)
            {
                store.Write(target, start + (i * 300), [10.0 + i, 20.0 + i, 30.0 + i, 40.0 + i, 50.0 + i]);
            }

            var path = store.ResolvePath(target.Id);
            var info = RunRrdTool("info", path);

            Assert.Contains(info, "ds[median].type = \"GAUGE\"", "the schema is upstream's");
            Assert.Contains(info, "ds[ping5].type = \"GAUGE\"", "with one source per probe");

            var fetched = RunRrdTool("fetch", path, "AVERAGE", "--start", start.ToString(CultureInfo.InvariantCulture), "--end", (start + 1500).ToString(CultureInfo.InvariantCulture));
            Assert.Contains(fetched, "median", "and rrdtool fetches it happily");
        });
    }

    private static SmokePing.Net.Configuration.MeasuredTarget Target(int pings, int step) => new()
    {
        Id = "group/host",
        Title = "host",
        Host = "192.0.2.1",
        ProbeType = "icmp",
        StepSeconds = step,
        Pings = pings,
        PingIntervalMs = 100,
        TimeoutMs = 1000,
        PacketSize = 56,
        AlertRules = [],
        ParentId = "group",
    };

    private static string Format(long timestamp, IReadOnlyList<double> values) =>
        timestamp + " " + string.Join(
            " ",
            values.Select(v => double.IsNaN(v) ? "NaN" : v.ToString("G10", CultureInfo.InvariantCulture)));

    /// <summary>Extracts the data rows from rrdtool's XML dump, in its own order.</summary>
    private static List<string> DumpRows(string dump)
    {
        var rows = new List<string>();

        foreach (var line in dump.Split('\n'))
        {
            var arrow = line.IndexOf("--> <row>", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            var slash = line.LastIndexOf('/', arrow);
            var timestamp = long.Parse(line[(slash + 1)..arrow].Trim(), CultureInfo.InvariantCulture);

            var values = new List<double>();
            var rest = line[arrow..];
            var at = 0;

            while ((at = rest.IndexOf("<v>", at, StringComparison.Ordinal)) >= 0)
            {
                var end = rest.IndexOf("</v>", at, StringComparison.Ordinal);
                var text = rest[(at + 3)..end].Trim();
                values.Add(text.Contains("nan", StringComparison.OrdinalIgnoreCase)
                    ? double.NaN
                    : double.Parse(text, CultureInfo.InvariantCulture));
                at = end;
            }

            rows.Add(Format(timestamp, values));
        }

        return rows;
    }

    private static bool? _hasRrdTool;

    private static bool HasRrdTool()
    {
        _hasRrdTool ??= TryRun("rrdtool", ["--version"], out _);
        return _hasRrdTool.Value;
    }

    private static string RunRrdTool(params string[] arguments)
    {
        if (!TryRun("rrdtool", arguments, out var output))
        {
            throw new AssertionException($"rrdtool {string.Join(" ", arguments)} failed: {output}");
        }

        return output;
    }

    private static bool TryRun(string command, string[] arguments, out string output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output = "could not start";
                return false;
            }

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output = ex.Message;
            return false;
        }
    }
}
