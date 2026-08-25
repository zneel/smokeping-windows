# uPlot 1.6.32

<https://github.com/leeoniya/uPlot> — MIT, see `LICENSE`.

Vendored rather than loaded from a CDN. This is a tool you open *because* the network
is misbehaving, and a chart that has to fetch itself from someone else's server is
blank in exactly the situation it exists for. It also keeps the downloads working on
an isolated network and keeps "unzip and run" true.

Chosen over d3 because the job is dense time series: a three hour window is ten
thousand readings, redrawn every few seconds. uPlot draws that without effort in 52 KB
and brings its own cursor, zoom and scales; d3 is a toolkit that would have left all
of that to be written by hand, at five times the size.

Update by replacing `uPlot.iife.min.js` and `uPlot.min.css` from the `uplot` npm
package and bumping the version above.
