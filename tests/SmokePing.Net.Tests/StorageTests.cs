using SmokePing.Net.Storage;

namespace SmokePing.Net.Tests;

public static class StorageTests
{
    private static readonly (int Multiplier, int SlotCount)[] Plan = [(1, 12), (4, 6)];

    public static void Register(TestRunner runner)
    {
        runner.Add("Quantiles: full round produces min, median and max", () =>
        {
            var (sent, lost, quantiles) = Quantiles.Compute([10.0, 20.0, 30.0, 40.0, 50.0]);

            Assert.Equal(5, sent, "all probes counted as sent");
            Assert.Equal(0, lost, "nothing was lost");
            Assert.Close(10, quantiles[0], 0.001, "0% quantile is the fastest probe");
            Assert.Close(30, quantiles[Sample.MedianIndex], 0.001, "50% quantile is the median");
            Assert.Close(50, quantiles[Sample.QuantileCount - 1], 0.001, "100% quantile is the slowest probe");
        });

        runner.Add("Quantiles: lost probes are excluded from the distribution", () =>
        {
            var (sent, lost, quantiles) = Quantiles.Compute([10.0, null, 30.0, null]);

            Assert.Equal(4, sent, "lost probes still count as sent");
            Assert.Equal(2, lost, "two probes were lost");
            Assert.Close(10, quantiles[0], 0.001, "minimum comes from the surviving probes");
            Assert.Close(30, quantiles[Sample.QuantileCount - 1], 0.001, "maximum comes from the surviving probes");
        });

        runner.Add("Quantiles: a total loss yields no distribution at all", () =>
        {
            var (sent, lost, quantiles) = Quantiles.Compute([null, null, null]);

            Assert.Equal(3, sent, "all probes were sent");
            Assert.Equal(3, lost, "all probes were lost");
            foreach (var q in quantiles)
            {
                Assert.IsNaN(q, "a round with no answers has no round trip times");
            }
        });

        runner.Add("Quantiles: averaging ignores rounds with no data", () =>
        {
            var averaged = Quantiles.Average([
                Quantiles.FromSamples([10.0]),
                Sample.CreateNaNQuantiles(),
                Quantiles.FromSamples([30.0]),
            ]);

            Assert.Close(20, averaged[Sample.MedianIndex], 0.001, "NaN rows must not drag the average down");
        });

        runner.Add("Jitter: it measures variation between consecutive probes", () =>
        {
            // Steady arrival: every gap is 10ms.
            Assert.Close(10, Jitter.Compute([10.0, 20.0, 30.0, 40.0]), 0.001, "a smooth ramp jitters by its step");

            // The same four values reordered: gaps of 30, 20 and 10 average to 20.
            Assert.Close(20, Jitter.Compute([10.0, 40.0, 20.0, 30.0]), 0.001, "reordering the same probes changes jitter");

            Assert.Close(0, Jitter.Compute([15.0, 15.0, 15.0]), 0.001, "an unvarying round has no jitter");
        });

        runner.Add("Jitter: quantiles alone cannot tell these rounds apart", () =>
        {
            // The point of storing jitter rather than deriving it: these two rounds
            // have identical distributions and completely different behaviour.
            var smooth = new List<double?> { 10.0, 20.0, 30.0, 40.0, 50.0 };
            var alternating = new List<double?> { 10.0, 50.0, 20.0, 40.0, 30.0 };

            var smoothQuantiles = Quantiles.Compute(smooth).Quantiles;
            var alternatingQuantiles = Quantiles.Compute(alternating).Quantiles;

            for (var i = 0; i < Sample.QuantileCount; i++)
            {
                Assert.Close(smoothQuantiles[i], alternatingQuantiles[i], 0.001, "the distributions are identical");
            }

            Assert.True(
                Jitter.Compute(alternating) > Jitter.Compute(smooth) * 2,
                "but the jitter is far higher for the round that jumps about");
        });

        runner.Add("Jitter: lost probes break the pair without inventing a value", () =>
        {
            // 10 -> 20 is a gap of 10; the lost probe is skipped, 20 -> 30 is another 10.
            Assert.Close(10, Jitter.Compute([10.0, 20.0, null, 30.0]), 0.001, "loss does not distort the average");

            Assert.IsNaN(Jitter.Compute([null, null]), "a round with no answers has no jitter");
            Assert.IsNaN(Jitter.Compute([12.0]), "a single answer has nothing to vary against");
        });

        runner.Add("Jitter: averaging skips rounds that produced none", () =>
        {
            Assert.Close(15, Jitter.Average([10f, float.NaN, 20f]), 0.001, "NaN rounds are ignored");
            Assert.IsNaN(Jitter.Average([float.NaN, float.NaN]), "nothing to average is not zero");
        });

        runner.Add("RoundRobinFile: jitter survives the round trip and consolidation", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 10, Plan);

            file.Write(0, 10, 0, Quantiles.FromSamples([10.0]), 2.0f);
            file.Write(60, 10, 0, Quantiles.FromSamples([10.0]), 4.0f);
            file.Write(120, 10, 0, Quantiles.FromSamples([10.0]), 6.0f);
            file.Write(180, 10, 0, Quantiles.FromSamples([10.0]), 8.0f);

            Assert.Close(2.0, file.Read(0, 0, 0)[0].JitterMilliseconds!.Value, 0.001, "stored jitter reads back");
            Assert.Close(
                5.0,
                file.Read(1, 0, 0)[0].JitterMilliseconds!.Value,
                0.001,
                "the coarse archive averages it");
        });

        runner.Add("RoundRobinFile: a round without jitter reports none rather than zero", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 10, Plan);

            file.Write(0, 10, 10, Sample.CreateNaNQuantiles());

            Assert.True(file.Read(0, 0, 0)[0].JitterMilliseconds is null, "no answers means no jitter figure");
        });

        runner.Add("RoundRobinFile: a written sample reads back unchanged", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            var quantiles = Quantiles.FromSamples([5.0, 6.0, 7.0, 8.0, 9.0]);
            file.Write(600, 5, 0, quantiles);

            var samples = file.Read(0, 600, 600);
            Assert.Equal(1, samples.Count, "one slot was requested");
            Assert.Equal(600L, samples[0].Timestamp, "the timestamp is the slot boundary");
            Assert.Equal(5, samples[0].Sent, "sent count survives the round trip");
            Assert.Close(7.0, samples[0].Median!.Value, 0.001, "the median survives the round trip");
        });

        runner.Add("RoundRobinFile: timestamps are snapped down to the step", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            file.Write(637, 5, 0, Quantiles.FromSamples([12.0]));

            var samples = file.Read(0, 600, 600);
            Assert.Equal(600L, samples[0].Timestamp, "637 belongs to the slot that starts at 600");
            Assert.Close(12.0, samples[0].Median!.Value, 0.001, "the sample landed in that slot");
        });

        runner.Add("RoundRobinFile: gaps come back as empty samples", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            file.Write(600, 5, 0, Quantiles.FromSamples([10.0]));
            file.Write(780, 5, 0, Quantiles.FromSamples([10.0]));

            var samples = file.Read(0, 600, 780);
            Assert.Equal(4, samples.Count, "four slots span 600 to 780");
            Assert.Equal(0, samples[1].Sent, "the skipped slot is empty");
            Assert.True(samples[1].Median is null, "an empty slot has no median");
        });

        runner.Add("RoundRobinFile: old data is overwritten once the archive wraps", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            // 12 slots in the base archive, so writing 20 rounds wraps it.
            for (var i = 0; i < 20; i++)
            {
                file.Write(i * 60, 5, 0, Quantiles.FromSamples([i + 1.0]));
            }

            var recent = file.Read(0, 19 * 60, 19 * 60);
            Assert.Close(20.0, recent[0].Median!.Value, 0.001, "the newest round is present");

            // Slot 0 shares a position with slot 12; reading it must not return slot 12's data.
            var overwritten = file.Read(0, 0, 0);
            Assert.Equal(0, overwritten[0].Sent, "the wrapped-over slot reports no data rather than stale data");
        });

        runner.Add("RoundRobinFile: coarse archives consolidate the base archive", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 10, Plan);

            // Four 60s rounds make up one 240s bucket: two clean, two with heavy loss.
            file.Write(0, 10, 0, Quantiles.FromSamples([10.0]));
            file.Write(60, 10, 0, Quantiles.FromSamples([30.0]));
            file.Write(120, 10, 5, Quantiles.FromSamples([50.0]));
            file.Write(180, 10, 5, Quantiles.FromSamples([70.0]));

            var consolidated = file.Read(1, 0, 0);
            Assert.Equal(1, consolidated.Count, "one coarse bucket covers the four rounds");
            Assert.Equal(40, consolidated[0].Sent, "sent counts are summed");
            Assert.Equal(10, consolidated[0].Lost, "lost counts are summed");
            Assert.Close(40.0, consolidated[0].Median!.Value, 0.001, "medians are averaged across the bucket");
            Assert.Close(0.25, consolidated[0].LossFraction, 0.001, "the loss ratio stays exact");
        });

        runner.Add("RoundRobinFile: reopening keeps the stored history", () =>
        {
            using var directory = new TempDirectory();
            var path = directory.File("t.spd");

            using (var file = RoundRobinFile.OpenOrCreate(path, 60, 5, Plan))
            {
                file.Write(600, 5, 1, Quantiles.FromSamples([42.0]));
            }

            using var reopened = RoundRobinFile.OpenOrCreate(path, 60, 5, Plan);
            Assert.Close(42.0, reopened.Read(0, 600, 600)[0].Median!.Value, 0.001, "data survives a restart");
        });

        runner.Add("RoundRobinFile: an incompatible layout is moved aside, not misread", () =>
        {
            using var directory = new TempDirectory();
            var path = directory.File("t.spd");

            using (var file = RoundRobinFile.OpenOrCreate(path, 60, 5, Plan))
            {
                file.Write(600, 5, 0, Quantiles.FromSamples([42.0]));
            }

            // The step changed in the configuration; the old file cannot be reused.
            using var recreated = RoundRobinFile.OpenOrCreate(path, 120, 5, Plan);

            Assert.Equal(120, recreated.StepSeconds, "the new file uses the new step");
            Assert.Equal(0, recreated.Read(0, 600, 600)[0].Sent, "the new file starts empty");
            Assert.True(
                Directory.GetFiles(directory.Path, "*.bak").Length == 1,
                "the previous database is preserved as a backup");
        });

        runner.Add("RoundRobinFile: the archive chosen covers the requested span", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            Assert.Equal(0, file.SelectArchive(300), "a short range uses the full resolution archive");
            Assert.Equal(1, file.SelectArchive(1000), "a longer range needs the coarse archive");
            Assert.Equal(1, file.SelectArchive(999999), "a range beyond all history falls back to the coarsest archive");
        });

        runner.Add("RoundRobinFile: ReadLatest finds the newest round with data", () =>
        {
            using var directory = new TempDirectory();
            using var file = RoundRobinFile.OpenOrCreate(directory.File("t.spd"), 60, 5, Plan);

            file.Write(300, 5, 0, Quantiles.FromSamples([11.0]));

            var latest = file.ReadLatest(420);
            Assert.True(latest is not null, "the earlier round is found");
            Assert.Close(11.0, latest!.Median!.Value, 0.001, "it is the round that was written");
        });

        runner.Add("DataStore: retention matches the upstream archive table", () =>
        {
            // Pinned because the retention is a documented promise, and a plan that
            // quietly drifts from it would go unnoticed until someone lost history.
            const int Step = 300;
            var expected = new[]
            {
                (StepSeconds: 300, Days: 100.0),
                (StepSeconds: 3600, Days: 400.0),
                (StepSeconds: 43200, Days: 1200.0),
            };

            Assert.Equal(expected.Length, DataStore.ArchivePlan.Length, "three resolution tiers");

            for (var i = 0; i < expected.Length; i++)
            {
                var (multiplier, slots) = DataStore.ArchivePlan[i];
                Assert.Equal(expected[i].StepSeconds, multiplier * Step, $"tier {i} resolution");
                Assert.Close(
                    expected[i].Days,
                    (double)multiplier * Step * slots / 86400,
                    0.01,
                    $"tier {i} retention");
            }
        });

        runner.Add("DataStore: a target database is the size the documentation claims", () =>
        {
            using var directory = new TempDirectory();
            using var store = new DataStore(directory.Path);

            var target = new Configuration.MeasuredTarget
            {
                Id = "sizing",
                Title = "sizing",
                Host = "192.0.2.1",
                ProbeType = "icmp",
                StepSeconds = 300,
                Pings = 20,
                PingIntervalMs = 500,
                TimeoutMs = 1500,
                PacketSize = 56,
                AlertRules = [],
                ParentId = string.Empty,
            };

            store.GetOrOpen(target);
            var megabytes = new FileInfo(store.ResolvePath(target.Id)).Length / 1024.0 / 1024.0;

            Assert.True(megabytes is > 2.0 and < 3.0, $"about 2.5 MB per target, got {megabytes:F2} MB");
        });

        runner.Add("DataStore: target ids map to safe paths", () =>
        {
            using var directory = new TempDirectory();
            using var store = new DataStore(directory.Path);

            var path = store.ResolvePath("internet/../../etc/passwd");

            Assert.True(
                path.StartsWith(store.DataDirectory, StringComparison.Ordinal),
                "a traversal attempt stays inside the data directory");
            Assert.Contains(path, ".spd", "databases keep their extension");
        });
    }
}
