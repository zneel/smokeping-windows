# SmokePing.NET

A native Windows reimplementation of [SmokePing](https://oss.oetiker.ch/smokeping/)
in C# / .NET 10. It measures network latency and packet loss, stores the results in a
fixed-size round-robin database, and serves the familiar "smoke" graphs from a
built-in web server.

![](docs/screenshot-detail.svg)

## Why

SmokePing itself is Perl and depends on RRDtool, fping and a CGI-capable web server.
On Windows that means WSL, Cygwin or a container — none of which are pleasant to run
as a long-lived service on a Windows box. This is a from-scratch implementation of
the same ideas with none of that baggage:

* one self-contained executable, no Perl, no RRDtool, no fping, no web server;
* **no NuGet dependencies** — everything runs on the .NET shared framework;
* ICMP works without administrator rights on Windows (it uses the IP Helper API);
* installs as a proper Windows service.

The data model, the graph design, the loss colour scale and the alert pattern
language all follow upstream, so graphs and alert rules are directly comparable with
an existing SmokePing installation. See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)
for exactly what matches and what does not.

## Quick start

```powershell
git clone <this repository>
cd smokeping-windows

# Check the sample configuration, then edit it for your network
dotnet run --project src/SmokePing.Net -- --config config/smokeping.json --check

dotnet run --project src/SmokePing.Net -- --config config/smokeping.json
```

Then open <http://localhost:8081>.

The first graphs appear after one polling interval (five minutes by default) and get
more interesting over the following hours.

## How it reads

Each row of the graph is one **measurement round**: by default twenty ICMP echoes,
half a second apart, every five minutes.

* the **line** is the median round-trip time of the round;
* the **grey smoke** around it is the spread of the individual probes — nested bands
  from the full min-max range on the outside to the tightest pair around the median.
  A thin line means a stable connection; a thick cloud means jitter;
* the **colour of the line** is packet loss: green for a clean round, through blue
  and purple, to dark red when nothing came back at all;
* rounds that lost probes are also **shaded** across the full height, so an outage is
  visible without hunting for a change of colour in the line.

Under the graph, the same figures the original prints: round-trip time as average,
maximum, minimum and current, its standard deviation, packet loss the same four ways,
the loss colour key, and what took the measurements.

That combination is the whole point of SmokePing: latency, jitter and loss in one
picture, at a glance.

### Jitter

Alongside the original's figures, each round also records **jitter**: the mean
absolute difference between the round-trip times of consecutive probes.

This is measured when the probes come back rather than derived afterwards, because it
is a property of the order they arrived in and the stored distribution does not keep
that. A round alternating 10ms and 60ms and a round rising smoothly from 10ms to 60ms
have identical quantiles and completely different jitter. Standard deviation, by
contrast, describes how the median moved *between* rounds; jitter describes variation
*within* one.

It appears on the graph legend, in the hover text of each round, in the target
statistics, in the API, and as a "Top Jitter" chart.

## Configuration

Everything lives in one JSON file. See [config/smokeping.json](config/smokeping.json)
for a worked example and [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full
reference.

```jsonc
{
  "general": {
    "siteName": "SmokePing.NET",
    "dataDirectory": "../data",
    "listenUrl": "http://localhost:8081"
  },

  // Inherited by every target unless overridden further down the tree.
  "defaults": {
    "probe": "icmp",
    "step": 300,
    "pings": 20,
    "alertRules": ["someloss"]
  },

  "alerts": [
    {
      "name": "someloss",
      "type": "loss",
      "pattern": ">0%,*12*,>0%,*12*,>0%",
      "comment": "loss 3 times in a row"
    }
  ],

  "targets": [
    {
      "id": "internet",
      "title": "Internet",
      "children": [
        { "id": "cloudflare", "title": "Cloudflare DNS", "host": "1.1.1.1" },
        { "id": "google",     "title": "Google DNS",     "host": "8.8.8.8" }
      ]
    }
  ]
}
```

Hosts can be dynamic: `"host": "%gateway%"` measures whatever the default gateway
currently is, and `"%dns%"` the machine's first DNS server. Both are re-read
periodically, so a laptop that changes network keeps measuring the right thing, and
neither needs administrator rights. `--check` shows what they resolved to.

Targets form a tree. A node with a `host` is measured; a node without one is just a
menu folder. Settings are inherited down the tree, so `step`, `pings` or a probe type
can be set once on a folder and apply to everything below it.

### Probes

| Probe  | Measures                              | Extra settings   |
| ------ | ------------------------------------- | ---------------- |
| `icmp` | ICMP echo round-trip time (default)   | `packetSize`     |
| `tcp`  | Time to complete a TCP handshake      | `port`           |
| `dns`  | Time for a DNS server to answer       | `port`, `query`  |
| `http` | Time to fetch a URL, body included    | `url`            |

`tcp` is the one to reach for when ICMP is filtered, and it measures what users
actually wait for. `dns` builds and parses the query itself, so it measures the named
server rather than whatever the system resolver has cached.

### Alerts

Alert rules use SmokePing's detector pattern language: a comma-separated list of
comparisons matched against the most recent rounds, anchored at the newest one.

```jsonc
{ "type": "loss", "pattern": "==100%,==100%,==100%" }   // dead for three rounds
{ "type": "loss", "pattern": ">0%,*12*,>0%,*12*,>0%" }  // three lossy rounds, gaps allowed
{ "type": "rtt",  "pattern": "<10,<10,<10,<100,>100" }  // latency stepped up
{ "type": "rtt",  "pattern": ">100<200" }               // newest round inside a range
```

`loss` patterns are in percent, `rtt` patterns in milliseconds. `*N*` matches up to N
arbitrary rounds, `==U` matches a round with no data, `==S` matches the start marker
before a target has ever been measured, and `==*` matches any single round.

When a rule matches, SmokePing.NET logs it, and can POST it to a `webhookUrl` and/or
run a `command`. Edge-triggered rules (the default) notify on the transition into and
out of the alert state; set `"edgeTrigger": false` to be told every round instead.

## Storage

Each target gets one fixed-size `.spd` file that never grows. It holds the same
resolution tiers upstream's default RRA table does, so retention matches an existing
SmokePing installation:

| Resolution     | Retention |
| -------------- | --------- |
| 5 minutes      | 100 days  |
| 1 hour         | 400 days  |
| 12 hours       | 1200 days |

Rather than storing each individual ping, every round is reduced to eleven quantiles
(0%, 10% … 100%) plus the sent and lost counts. That is enough to redraw the smoke
exactly, keeps every record the same 60 bytes, and subsumes the separate MIN/MAX/AVERAGE
archives RRDtool needs — about 2.5 MB per target, forever.

Coarse tiers are recomputed from the full-resolution tier on every write, so a missed
poll or an unclean shutdown can never leave a bucket permanently wrong.

## HTTP API

The web interface is a thin client over a small JSON API. The graph endpoint returns
plain SVG, so it can be dropped straight into a dashboard, a wiki or an e-mail.

| Endpoint | Purpose |
| -------- | ------- |
| `GET /api/config` | Site name and the menu tree |
| `GET /api/targets` | Every measured target |
| `GET /api/targets/{id}?range=3h` | Samples and statistics for one target |
| `GET /api/graph/{id}.svg?range=3h` | A rendered graph |
| `GET /api/charts?range=10h` | Top-N by std deviation, max, loss and median |
| `GET /api/alerts` | Rules, active alerts and recent notifications |
| `GET /api/health` | Liveness and target count |

Graph parameters: `range` (`90m`, `3h`, `10d`, `2w`, `6mon`, `1y`), `width`, `height`,
`theme` (`light` or `dark`), `compact`, and `offsetMinutes` to label the time axis in
a local time zone.

```
http://localhost:8081/api/graph/internet/cloudflare.svg?range=30h&theme=dark&width=900
```

## Command line

```
--config <path>     Configuration file (default: config/smokeping.json)
--check             Validate the configuration and exit
--service           Run under the Windows service control manager
--service-name      Service name to register as (default: SmokePingNet)
--log-file <path>   Write a log file (always on in service mode)
--no-polling        Serve the web interface without taking measurements
--help              Show this help
```

`--check` validates everything up front — pattern syntax, unknown probes, duplicate
ids, alert names that do not exist, and rounds too long to fit in their step — so a
typo is caught at start-up rather than silently leaving a host unmonitored.

## Project layout

Graph layout lives in `SmokeGraphLayout`, which produces a drawing-backend
independent scene; `SvgSceneWriter` turns that into the SVG the web interface and the
HTTP API serve.

## Building and testing

```bash
dotnet build SmokePing.Net.sln
dotnet run --project tests/SmokePing.Net.Tests          # run every test
dotnet run --project tests/SmokePing.Net.Tests -- alert # run a subset
```

The tests are a self-contained runner rather than xunit, for the same reason the
daemon has no NuGet dependencies: it builds and tests on a machine with nothing
installed but the .NET SDK.

## Licence

GPL-2.0-or-later, matching upstream SmokePing, whose data model and graph design this
implementation deliberately follows. SmokePing is copyright Tobias Oetiker; this is an
independent reimplementation in C# and shares none of its code.
