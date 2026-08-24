# Relationship to SmokePing

This is an independent reimplementation, not a port: it shares no code with upstream
SmokePing. What it does share is the data model, the graph design, the loss colour
scale and the alert pattern language, all of which were followed deliberately so that
graphs and alert rules read the same way as they do on an existing installation.

Written against SmokePing 2.9.0.

## What matches

**Measurement model.** A round of `pings` probes every `step` seconds, reduced to a
median, a loss count and the distribution of the individual probes. Defaults are
upstream's: 20 pings every 300 seconds, 56-byte ICMP payload.

**Retention.** The archive tiers mirror upstream's default RRA table: full resolution
for 100 days, hourly for 400 days, twelve-hourly for 1200 days.

**Smoke shading.** The nested bands and their grey ramp use upstream's formula,
`int(190 / half × (half − i)) + 50`, so the outer, rarer values are lightest and the
band around the median is darkest. Lost probes pad the round at both ends exactly as
upstream pads `ping1..pingN` with unknowns, so the band narrows as loss rises rather
than staying full width over fewer and fewer samples.

Because eleven quantiles are stored rather than one value per probe, a 20-ping target
gets five bands where upstream draws ten, and the ramp runs 205…67 rather than
221…50. The shape is the same; the shading is coarser.

**Median.** The middle received probe by position, without interpolation — upstream's
`$times[int($entries/2)]`. For twenty probes timed 1…20 ms that is 11 ms, not the
10.5 ms an interpolated percentile would give. It is stored in its own right, as
upstream keeps it as its own data source.

**Consolidation.** Coarse archives apply upstream's xfiles factor: a bucket built from
fewer than half its rounds is unknown rather than an average of the survivors, so an
hour containing one round renders as a gap and not as a healthy hour.

**Y axis.** Scaled to the highest median with upstream's 1.2 headroom, rigid, with
smoke above the top clipped to the frame. A single slow probe does not rescale the
graph and squash the median line.

**Loss colours.** The exact palette and thresholds:

| Lost | Colour | |
| --- | --- | --- |
| 0 | `#26ff00` | green |
| ≤ max(1%, 1) | `#00b8ff` | |
| ≤ max(5%, 2) | `#0059ff` | |
| ≤ max(10%, 3) | `#7e00ff` | |
| ≤ max(25%, 4) | `#ff00ff` | |
| ≤ max(50%, 5) | `#ff5500` | |
| ≤ all but one | `#ff0000` | |
| all | `#a00000` | dark red |

For short rounds several thresholds collapse onto the same count; as upstream, the
later colour wins, which keeps a totally dead round dark red.

**Alert patterns.** The full detector grammar — comparisons, ranges, `*N*` gaps, `==U`,
`==S`, `==*` — anchored at the newest reading. Loss in percent, rtt in milliseconds.
Edge triggering and rule priority behave as upstream's `edgetrigger` and `priority`
do, including `edgetrigger` defaulting to off, and the alert history starts with the
same synthetic `S` marker.

The gap bounds are reproduced exactly, quirk and all. Upstream tests its gap limit
twice — once as a loop bound, once afterwards with the gap length itself subtracted —
so `*12*` between two tests tolerates six intervening rounds over a fourteen-round
history, not twelve, and a gap pattern cannot match a history exactly as long as its
own fixed tokens. That contradicts upstream's own manual page, which says `*X*`
ignores up to X values. It is reproduced anyway: a rule carried over from an existing
installation has to fire on the same rounds here. This was established by running
upstream's own generated matchers against this implementation over tens of thousands
of paired inputs, not by reading the Perl.

The retained history is sized from the longest pattern in use, as upstream's
`fetchlength` is, and a pattern longer than the maximum is rejected at load time
rather than silently never matching.

**Graph periods.** The detail page shows upstream's four: 3 hours, 30 hours, 10 days,
360 days. Overviews use 10 hours.

**Legend.** The same labelled rows: `median rtt` with average, maximum, minimum,
current, standard deviation and the average-over-deviation ratio; `packet loss` the
same four ways; `loss color`; and `probe` with the description and the end time.

**Charts.** Top-N by standard deviation, max round-trip time, loss and median, plus
a jitter chart this adds.

**Alert command arguments.** `<name> <target> <loss history> <rtt history> <host>
[<raised>]`, matching upstream's `|command` recipients.

## What this adds

**Jitter.** Each round records the mean absolute variation between consecutive
probes, which upstream does not measure. It is stored at measurement time because the
quantiles cannot reconstruct probe order.

**Dynamic hosts.** `%gateway%` and `%dns%` resolve at measurement time. Upstream has
no equivalent; a gateway address has to be written into the configuration by hand.

**Loss background on by default.** Upstream has this as `loss_background`, off by
default; here rounds that lost probes are shaded unless the graph asks otherwise.

## What differs

**Storage.** Upstream stores every individual ping as its own RRD data source
(`ping1`…`ping20`) plus separate MIN/MAX/AVERAGE archives. This stores eleven
quantiles per round instead. The graphs are the same because the smoke is drawn from
the distribution either way, but the files are not RRD files and cannot be read by
`rrdtool` — and eleven quantiles is a lossy summary of twenty pings, so an individual
probe's exact time is not recoverable.

**Probes.** Four built in (`icmp`, `tcp`, `dns`, `http`) against upstream's fifty-odd.
ICMP uses the Windows IP Helper API rather than `fping`, so it needs no external
binary and no administrator rights. There is no plugin mechanism.

**Configuration.** Upstream's own format is read directly - sections, the `+`/`++`
target hierarchy, backslash continuation, `@include`, and the Database archive table.
JSON is also accepted; the file decides. Settings with no meaning here (the CGI URL,
mail templates, image caches) are ignored, but anything that would change what gets
measured is not: an unimplemented probe or an unsupported host form is an error.

**Notifications.** A webhook and an external command. There is no built-in SMTP
client, no mail templates and no `smokemail`.

**Not implemented.** Master/slave distributed measurement, the CGI and its
`basepage.html` templating, RRDtool integration, hierarchies, the `Chartlist`
navigation cache, DYNAMIC hosts, multi-host targets, uptime tracking and its legend
row, `unison_tolerance`, logarithmic axes, and remote-triggered graph zooming.

**Matcher plugins.** Not implemented, and the pattern language does not substitute for
them: `CheckLoss`, `CheckLatency` and `ConsecutiveLoss` branch on whether the alert is
already raised, using a different threshold to clear than to raise, and a pattern has
no access to that state. `Median`, `Medratio` and `Avgratio` compare windows against
each other, and `ExpLoss` keeps an exponentially weighted average. None of these can
be written as a detector pattern.

**Top-N charts.** Upstream ranks on the single most recent round — its `StdDev` sorter
is the deviation across that round's twenty probes. These rank over a ten hour window
instead, on the per-round medians. The chart titles match; the quantities do not.

**Configurations that are rejected here.** A host of `DYNAMIC`, a multi-host list of
`/target/paths`, or a `~slave` suffix is refused at load time rather than measured and
reported as permanently down. Unknown settings are an error rather than being ignored,
`pings` must be at least three as upstream requires, and `port`, `packetSize`,
`timeoutMs` and `pingIntervalMs` are range-checked.

**Web interface.** A single-page application over a JSON API, with server-rendered
SVG graphs rather than RRDtool PNGs. Graph URLs are stable and embeddable.

## Migrating

There is no automated import: the file formats have nothing in common. Recreating the
target tree in JSON is quick, and alert patterns can be copied across verbatim. Run
both side by side for a while if the history matters — this one starts with an empty
database.
