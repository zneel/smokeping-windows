# Configuration reference

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

The desktop client connects to this same address (`--server`), so a daemon that only
listens on `localhost` can only be viewed from the machine it runs on.

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
