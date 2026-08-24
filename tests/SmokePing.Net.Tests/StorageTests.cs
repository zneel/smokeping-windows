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
