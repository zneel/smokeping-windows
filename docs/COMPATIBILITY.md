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
for 100 days, hourly for 400 days, twelve-hourly for 3.9 years.

**Smoke shading.** The nested bands and their grey ramp use upstream's formula,
`int(190 / half × (half − i)) + 50`, so the outer, rarer values are lightest and the
band around the median is darkest.

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
`==S`, `==*` — anchored at the newest reading, with the same backtracking over gap
lengths. Loss in percent, rtt in milliseconds. Edge triggering and rule priority
behave as upstream's `edgetrigger` and `priority` do, and the alert history starts
with the same synthetic `S` marker.

**Graph periods.** The detail page shows upstream's four: 3 hours, 30 hours, 10 days,
360 days. Overviews use 10 hours.

**Charts.** Top-N by standard deviation, max round-trip time, loss and median.

**Alert command arguments.** `<name> <target> <loss history> <rtt history> <host>
[<raised>]`, matching upstream's `|command` recipients.

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

**Configuration.** JSON, not upstream's `*** Section ***` format. Existing
`config` files are not readable; the concepts map across directly.

**Notifications.** A webhook and an external command. There is no built-in SMTP
client, no mail templates and no `smokemail`.

**Not implemented.** Master/slave distributed measurement, the CGI and its
`basepage.html` templating, RRDtool integration, hierarchies, the `Chartlist`
navigation cache, matcher plugins (the pattern language covers what the bundled
matchers do), and remote-triggered graph zooming.

**Web interface.** A single-page application over a JSON API, with server-rendered
SVG graphs rather than RRDtool PNGs. Graph URLs are stable and embeddable.

## Migrating

There is no automated import: the file formats have nothing in common. Recreating the
target tree in JSON is quick, and alert patterns can be copied across verbatim. Run
both side by side for a while if the history matters — this one starts with an empty
database.
