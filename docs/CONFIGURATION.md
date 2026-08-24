# Configuration reference

Two formats are accepted, and the file itself decides which: SmokePing's own
`*** Section ***` format, so an existing installation's configuration works unchanged,
or JSON. This reference describes the JSON; for the native format see the mapping at
the end.

One JSON file describes everything. Comments (`//`) and trailing commas are allowed.
Validate a file before relying on it:

```powershell
SmokePing.Net.exe --config config\smokeping.json --check
```

## `general`

| Key | Default | Meaning |
| --- | --- | --- |
| `siteName` | `SmokePing.NET` | Shown in the header and the browser title |
| `owner` | *(empty)* | Displayed under the site name |
| `contactEmail` | *(empty)* | Exposed through `/api/config` |
| `dataDirectory` | `data` | Where the `.spd` databases live. Relative paths resolve against the configuration file |
| `listenUrl` | `http://localhost:8081` | Address the web server binds to |

To reach the interface from other machines, bind to a specific address —
`http://192.168.1.10:8081` — or to `http://+:8081` for every interface. Binding to
`+` needs either administrator rights or a URL reservation:

```powershell
netsh http add urlacl url=http://+:8081/ user=DOMAIN\ServiceAccount
```

## `defaults` and inheritance

`defaults` holds measurement settings inherited by every target. Any target node may
override any of them, and the override applies to that node and everything below it.
The order of precedence is: the node itself, then each ancestor in turn, then
`defaults`, then the built-in values.

| Key | Default | Meaning |
| --- | --- | --- |
| `probe` | `icmp` | `icmp`, `tcp`, `dns` or `http` |
| `step` | `300` | Seconds between measurement rounds |
| `pings` | `20` | Probes sent per round |
| `pingIntervalMs` | `500` | Milliseconds between probes inside a round |
| `timeoutMs` | `1500` | Per-probe timeout |
| `packetSize` | `56` | ICMP payload bytes |
| `port` | *(probe specific)* | TCP port, or DNS server port |
| `query` | `localhost` | Name the DNS probe looks up |
| `url` | *(from host)* | URL the HTTP probe fetches |
| `alertRules` | `[]` | Names of the alert rules applied |

### Why 300 seconds

`step` defaults to 300 because that is SmokePing's own default, and it is SmokePing's
default because RRDtool and MRTG — same author — settled on a five minute sample as
the unit of network graphing. Everything else follows from it: the archive tiers are
multiples of the step, so a five minute step is what gives 100 days of full
resolution history in 2.5 MB.

It is not sacred. `step` is inheritable like any other setting, so it can be changed
globally or for one target:

```jsonc
"defaults": { "step": 300 },
"targets": [
  { "id": "gateway", "host": "192.168.1.1", "step": 60, "pings": 10, "pingIntervalMs": 1000 }
]
```

What changes when you lower it:

| Step | Full-resolution history | Database per target | Probes per hour |
| --- | --- | --- | --- |
| 60s | 20 days | 2.5 MB | 1200 |
| 300s (default) | 100 days | 2.5 MB | 240 |
| 900s | 300 days | 2.5 MB | 80 |

The file size does not change, because the archives hold a fixed number of slots — a
shorter step buys resolution and spends history. A 60 second step keeps only 20 days
before the full-resolution tier wraps.

Note that a round is a burst, not a spread: 20 pings 500ms apart occupy ten seconds of
each five minute step and the target is left alone for the rest. That is what upstream
does too. If you want a wider sample, raise `pingIntervalMs` rather than `pings` —
`pings` is what the loss percentage is quantised to, and 20 gives the 5% granularity
the loss colour scale is built around.

A whole round must fit inside one step, or rounds would overlap. The check is
`(pings - 1) × pingIntervalMs + timeoutMs ≤ step × 1000`, and a configuration that
fails it is rejected at start-up with the arithmetic spelled out.

Changing `step` or `pings` changes the shape of the stored records. The existing
database cannot be reinterpreted, so it is renamed to `*.bak` and a fresh one is
started. Back it up first if the history matters.

## `targets`

A tree of nodes:

```jsonc
{
  "id": "internet",          // letters, digits, dash, underscore
  "title": "Internet",       // shown in the menu; defaults to the id
  "description": "...",      // optional, shown on the detail page
  "host": "1.1.1.1",         // present = measured; absent = menu folder only
  "children": [ ... ]
}
```

Ids are joined with slashes to form the path used in URLs and on disk, so the node
above containing a child `cloudflare` yields `internet/cloudflare`. A node needs
either a `host` or children — one with neither measures nothing and is rejected.

### Dynamic hosts

Hard-coding a gateway address makes a configuration wrong on every network but the one
it was written for. Two tokens are resolved at measurement time instead:

| Token | Resolves to |
| --- | --- |
| `%gateway%` | The default gateway of the active interface |
| `%dns%` | The first DNS server the machine is configured with |

```jsonc
{ "id": "gateway", "title": "Default Gateway", "host": "%gateway%" }
{ "id": "dns", "title": "Local DNS", "host": "%dns%", "probe": "dns", "query": "www.example.com" }
```

Resolution reads the operating system's routing and DNS configuration and needs **no
administrator rights**. It is redone about once a minute rather than once at start-up,
so a laptop that moves to another network starts measuring the new gateway without a
restart — and the graph title follows it.

`--check` prints what each token resolved to on this machine, which is the quickest
way to confirm detection worked:

```
- local/gateway -> %gateway% -> 192.168.1.254 (icmp, 20 pings / 300s)
- local/dns     -> %dns%     -> 192.168.1.254 (dns, 20 pings / 300s)
```

Addresses that mean "no gateway" are skipped: the all-zeroes placeholder some drivers
report, and IPv4 link-local (`169.254.x.x`), which means DHCP failed rather than that
a gateway was found. IPv4 is preferred over IPv6 when both exist. If a token cannot be
resolved, that round is recorded as lost and a warning is logged — the target keeps
trying rather than the daemon giving up.

The `http` probe cannot build a URL from a token, so a target using one must set `url`
explicitly. That is rejected at start-up rather than at the first round.

### Probe-specific settings

```jsonc
// TCP: how long the handshake takes. Good where ICMP is filtered.
{ "id": "web", "host": "example.com", "probe": "tcp", "port": 443 }

// DNS: query built and parsed directly, so it measures the named server,
// not the local resolver cache.
{ "id": "resolver", "host": "192.168.1.1", "probe": "dns", "query": "www.example.com" }

// HTTP: full request including the response body.
{
  "id": "api", "host": "example.com", "probe": "http",
  "url": "https://example.com/health",
  "pings": 5, "pingIntervalMs": 2000, "timeoutMs": 10000
}
```

Fewer, slower probes suit HTTP: twenty full requests every five minutes is a load
test, not a measurement.

### Storage format

```jsonc
"database": {
  "format": "rrd",              // "spd" (default) or "rrd"
  "archives": [                 // optional; defaults to upstream's table
    { "steps": 1,   "rows": 28800 },
    { "steps": 12,  "rows": 9600 },
    { "steps": 144, "rows": 2400 }
  ]
}
```

`rrd` writes real RRDtool files with SmokePing's own schema, so an existing
installation's `.rrd` files can be used in place and `rrdtool` reads what this writes.
Pick it when the files have to interoperate. The cost is that the whole file is
rewritten on every round, and jitter is not stored — RRD's schema has nowhere for it.

`spd` is the built-in format: fixed-size records appended in place, and it carries
jitter. It cannot be read by `rrdtool`.

Note that RRDtool cannot change a step in place. If a `.rrd` file's step does not match
the configured one, start-up says so rather than guessing; use `rrdtool tune` or move
the file aside.

### Stored measurements

Each round stores the number of probes sent and lost, eleven quantiles of the
round-trip times, and the jitter. Changing `step` or `pings` changes the record
layout, so the previous database is renamed to `*.bak` and a fresh one started.

## `alerts`

| Key | Default | Meaning |
| --- | --- | --- |
| `name` | *(required)* | Referenced from a target's `alertRules` |
| `type` | `loss` | `loss` (percent) or `rtt` (milliseconds) |
| `pattern` | *(required)* | Detector pattern, see below |
| `comment` | *(empty)* | Included in notifications |
| `edgeTrigger` | `true` | Notify on transitions only, rather than every matching round |
| `priority` | *(none)* | Lower numbers evaluated first; the first prioritised rule to match silences the others for that round |
| `command` | *(none)* | Executable run on notification |
| `webhookUrl` | *(none)* | URL that receives a JSON POST |

### Pattern language

A comma-separated list of tokens matched against the most recent rounds, anchored so
the **last token always tests the newest round**.

| Token | Matches |
| --- | --- |
| `>20%`, `<=5%`, `==0%` | A loss comparison (loss patterns only) |
| `>100`, `<10` | An rtt comparison in milliseconds (rtt patterns only) |
| `>100<200` | Both comparisons — a range |
| `==*` | Any single round |
| `*N*` | Zero to N arbitrary rounds — a variable-length gap |
| `==U` / `!=U` | A round with / without missing data |
| `==S` | The start marker, before the target was ever measured |

```jsonc
">20%,>20%,>20%"            // three consecutive rounds above 20% loss
"==100%,==100%,==100%"      // dead for three rounds
">0%,*12*,>0%,*12*,>0%"     // three lossy rounds, up to 12 clean ones between each
"<10,<10,<10,<10,<100,>100,>100,>100"  // latency stepped up and stayed there
"==S,!=U"                   // the first successful round after a fresh start
```

A numeric token never matches a round with no data — use `==U` for that explicitly.
The sixty-four most recent rounds are kept per target, so a pattern cannot look
further back than that.

### Notifications

`command` is run with the same arguments upstream passes:

```
<name> <target path> "loss: ..." "rtt: ..." <host> <1 raised | 0 cleared>
```

`webhookUrl` receives a JSON POST:

```json
{
  "timestamp": "2026-08-24T15:14:10+00:00",
  "alertName": "hostdown",
  "targetId": "internet/cloudflare",
  "targetTitle": "Cloudflare DNS",
  "host": "1.1.1.1",
  "state": "raised",
  "comment": "host has not answered for three rounds",
  "pattern": "==100%,==100%,==100%",
  "lossHistory": ["S", "0%", "100%", "100%", "100%"],
  "rttHistory": ["S", "12.4ms", "U", "U", "U"]
}
```

`state` is `raised`, `cleared` or — for level-triggered rules — `active`.

Both run on every notification. Neither is retried: a webhook that is down when an
alert fires misses it, so point it at something durable rather than at a pager
directly.

## Reading an existing SmokePing configuration

A file that starts with a `*** Section ***` header is read as SmokePing's own format.
What maps across:

| Upstream | Becomes |
| --- | --- |
| `*** General ***` `owner`, `contact`, `datadir` | `general.owner`, `contactEmail`, `dataDirectory` |
| `*** Database ***` `step`, `pings` | `defaults.step`, `defaults.pings` |
| `*** Database ***` `AVERAGE` rows | The archive tiers, so retention is preserved |
| `*** Alerts ***` `+name` | An alert rule, with `edgetrigger` defaulting off as upstream does |
| `*** Probes ***` `+FPing` parameters | Probe settings applied wherever that probe is used |
| `*** Targets ***` tree | The target hierarchy; `menu` becomes the title |
| `probe = FPing \| TCPPing \| DNS \| AnotherDNS \| Curl` | `icmp` \| `tcp` \| `dns` \| `http` |
| `timeout`, `hostinterval`, `mininterval` | `timeoutMs`, `pingIntervalMs` — seconds converted to milliseconds |
| `urlformat` | `url` |
| `lookup` | `query` |

`MIN` and `MAX` archive rows are ignored, because upstream creates those archives but
none of its own graphs read them.

Ignored without complaint: everything describing machinery this does not have — the
CGI URL, mail hosts and templates, image caches, `*** Presentation ***`, `*** Slaves ***`.

A native configuration has no equivalent of `listenUrl`, because upstream serves
through a CGI rather than binding a port of its own. Use `--listen` to set it:

```powershell
SmokePing.Net.exe --config C:\smokeping\etc\config --listen http://+:8081
```

Reported rather than ignored, because they change what gets measured: an unimplemented
probe class, an alert using a matcher plugin, and targets whose host is `DYNAMIC`, a
list of `/target/paths`, or carries a `~slave` suffix. Pass `--skip-unsupported` to
drop those targets and load the rest; each one is listed on start-up.
