'use strict';

/**
 * SmokePing.NET web interface.
 *
 * Graphs are rendered to SVG on the server and injected here, so what the browser
 * shows and what /api/graph returns for an <img> tag or an e-mail are the same picture.
 */

const state = {
  config: null,
  route: { view: 'overview', id: '' },
  theme: localStorage.getItem('smokeping-theme') || 'dark',
  timer: null,
};

const OVERVIEW_RANGE = '10h';
const REFRESH_MS = 60000;

/** How often the trace panel refetches while looking at a recent window. */
const TRACE_REFRESH_MS = 5000;

/** The trace panel's state: which target, which window, and its refresh timer. */
let trace = null;

/** Minutes east of UTC, so the server can label the time axis in local time. */
function utcOffsetMinutes() {
  return -new Date().getTimezoneOffset();
}

async function getJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.error || `${response.status} ${response.statusText}`);
  }
  return response.json();
}

function graphUrl(id, range, options = {}) {
  const params = new URLSearchParams({
    range,
    theme: state.theme,
    offsetMinutes: String(utcOffsetMinutes()),
    width: String(options.width || 620),
    height: String(options.height || 200),
  });
  if (options.compact) {
    params.set('compact', 'true');
  }
  // The card around the graph already names the target and the period, so the
  // heading drawn into the SVG would only repeat it.
  if (options.title !== undefined) {
    params.set('title', options.title);
  }
  if (options.subtitle !== undefined) {
    params.set('subtitle', options.subtitle);
  }
  return `/api/graph/${id}.svg?${params}`;
}

/** Fetches a rendered graph and places it in the container. */
async function loadGraph(container, id, range, options) {
  try {
    const response = await fetch(graphUrl(id, range, options));
    if (!response.ok) {
      throw new Error(`${response.status}`);
    }
    container.innerHTML = await response.text();
  } catch (error) {
    container.innerHTML = `<p class="error">Could not load graph: ${escapeHtml(error.message)}</p>`;
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function formatMs(value) {
  if (value === null || value === undefined || Number.isNaN(value)) return '-';
  if (value < 10) return `${value.toFixed(2)} ms`;
  if (value < 1000) return `${value.toFixed(1)} ms`;
  return `${(value / 1000).toFixed(2)} s`;
}

/* ---------------------------------------------------------------- routing */

function parseRoute() {
  const hash = location.hash.replace(/^#\/?/, '');
  if (hash === 'charts') return { view: 'charts', id: '' };
  if (hash === 'alerts') return { view: 'alerts', id: '' };
  if (hash.startsWith('target/')) return { view: 'target', id: hash.slice('target/'.length) };
  if (hash.startsWith('folder/')) return { view: 'folder', id: hash.slice('folder/'.length) };
  return { view: 'overview', id: '' };
}

function findNode(nodes, id) {
  for (const node of nodes) {
    if (node.id === id) return node;
    const found = findNode(node.children || [], id);
    if (found) return found;
  }
  return null;
}

/** Every measured node at or below the given subtree. */
function collectTargets(nodes) {
  const result = [];
  for (const node of nodes) {
    if (node.isTarget) result.push(node);
    result.push(...collectTargets(node.children || []));
  }
  return result;
}

/* ------------------------------------------------------------------- menu */

function renderMenu() {
  const menu = document.getElementById('menu');
  menu.innerHTML = buildMenu(state.config.menu);

  for (const link of menu.querySelectorAll('a')) {
    const active = link.getAttribute('href') === location.hash;
    link.classList.toggle('active', active);
  }
}

function buildMenu(nodes) {
  if (!nodes.length) return '';
  const items = nodes.map((node) => {
    const href = node.isTarget ? `#/target/${node.id}` : `#/folder/${node.id}`;
    const cls = node.isTarget ? '' : ' class="folder"';
    return `<li${cls}><a href="${href}" title="${escapeHtml(node.host || node.title)}">${escapeHtml(node.title)}</a>${buildMenu(node.children || [])}</li>`;
  });
  return `<ul>${items.join('')}</ul>`;
}

function highlightNav() {
  const map = { overview: 'overview', folder: 'overview', target: 'overview', charts: 'charts', alerts: 'alerts' };
  const current = map[state.route.view];
  for (const link of document.querySelectorAll('.topnav a')) {
    link.classList.toggle('active', link.dataset.nav === current);
  }
}

/* ------------------------------------------------------------------ views */

async function renderOverview(nodes, title, description) {
  const targets = collectTargets(nodes);
  const content = document.getElementById('content');

  if (!targets.length) {
    content.innerHTML = `<h1>${escapeHtml(title)}</h1><p class="placeholder">No targets here.</p>`;
    return;
  }

  content.innerHTML = `
    <h1>${escapeHtml(title)}</h1>
    <p class="subtitle">${escapeHtml(description || `${targets.length} target(s), last ${OVERVIEW_RANGE}`)}</p>
    <div class="grid-2">
      ${targets.map((t) => `
        <a class="card overview-item" href="#/target/${t.id}">
          <h3>${escapeHtml(t.title)} <span class="host">${escapeHtml(t.host || '')}</span></h3>
          <div class="graph" data-target="${t.id}"></div>
        </a>`).join('')}
    </div>`;

  for (const container of content.querySelectorAll('.graph')) {
    loadGraph(container, container.dataset.target, OVERVIEW_RANGE, {
      width: 440, height: 80, compact: true, title: '',
    });
  }
}

async function renderTarget(id) {
  const content = document.getElementById('content');
  content.innerHTML = '<p class="placeholder">Loading&hellip;</p>';

  let detail;
  try {
    detail = await getJson(`/api/targets/${id}?range=3h`);
  } catch (error) {
    content.innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`;
    return;
  }

  const s = detail.statistics;
  content.innerHTML = `
    <h1>${escapeHtml(detail.title)}</h1>
    <p class="subtitle">${escapeHtml(detail.host)} &middot; ${escapeHtml(detail.probeDescription)}
      ${detail.description ? `<br>${escapeHtml(detail.description)}` : ''}</p>
    <ul class="stats">
      <li><span>median rtt (avg)</span>${formatMs(s.medianAverage)}</li>
      <li><span>max</span>${formatMs(s.medianMaximum)}</li>
      <li><span>min</span>${formatMs(s.medianMinimum)}</li>
      <li><span>now</span>${formatMs(s.medianNow)}</li>
      <li><span>std deviation</span>${formatMs(s.standardDeviation)}</li>
      <li><span>jitter</span>${formatMs(s.jitter)}</li>
      <li><span>packet loss</span>${s.lossAverage.toFixed(2)}%</li>
      <li><span>rounds with data</span>${s.roundsWithData}</li>
    </ul>
    ${state.config.trace && state.config.trace.enabled ? `
      <div class="card trace-card">
        <h3>Second by second
          <span class="trace-ranges">
            ${state.config.trace.ranges.map((r) => `
              <button type="button" data-range="${r.range}">${escapeHtml(r.label)}</button>`).join('')}
            <button type="button" id="trace-scale" title="Switch the latency axis"></button>
          </span>
        </h3>
        <p class="trace-readout" id="trace-readout">Loading&hellip;</p>
        <p class="trace-hover" id="trace-hover">&nbsp;</p>
        <div class="trace-plots">
          <div id="trace-latency"></div>
          <div id="trace-jitter"></div>
          <div id="trace-loss"></div>
        </div>
        <div id="trace-events" class="trace-events"></div>
        <p class="trace-note">Recorded continuously at one probe every
          ${state.config.trace.intervalSeconds}s, whether or not this page is open, so a spike
          that happened while you were busy is still here afterwards. Full detail for
          ${state.config.trace.fineHours}h; peaks are kept for ${state.config.trace.coarseDays} days.</p>
      </div>` : ''}
    ${state.config.detailRanges.map((r) => `
      <div class="card">
        <h3>${escapeHtml(r.label)}</h3>
        <div class="graph" data-range="${r.range}"></div>
      </div>`).join('')}`;

  if (document.getElementById('trace-latency')) {
    startTrace(id);
  }

  for (const container of content.querySelectorAll('.graph')) {
    loadGraph(container, id, container.dataset.range, {
      width: 700, height: 200, title: '', subtitle: '',
    });
  }
}

async function renderCharts() {
  const content = document.getElementById('content');
  content.innerHTML = '<p class="placeholder">Loading&hellip;</p>';

  const data = await getJson('/api/charts?range=10h&entries=5');
  const isLoss = (title) => title.includes('Loss');

  content.innerHTML = `
    <h1>Charts</h1>
    <p class="subtitle">The most interesting destinations over the last 10 hours.</p>
    <div class="grid-2">
      ${data.charts.map((chart) => `
        <div class="card">
          <h3>${escapeHtml(chart.title)}</h3>
          <table>
            <thead><tr><th>Target</th><th>Host</th><th class="num">Value</th></tr></thead>
            <tbody>
              ${chart.items.length ? chart.items.map((item) => `
                <tr>
                  <td><a href="#/target/${item.id}">${escapeHtml(item.title)}</a></td>
                  <td>${escapeHtml(item.host)}</td>
                  <td class="num">${isLoss(chart.title) ? `${item.value.toFixed(1)}%` : formatMs(item.value)}</td>
                </tr>`).join('') : '<tr><td colspan="3" class="placeholder">No data yet.</td></tr>'}
            </tbody>
          </table>
        </div>`).join('')}
    </div>`;
}

async function renderAlerts() {
  const content = document.getElementById('content');
  content.innerHTML = '<p class="placeholder">Loading&hellip;</p>';

  const data = await getJson('/api/alerts');

  const eventRows = (events) => events.length
    ? events.map((e) => `
        <tr>
          <td>${new Date(e.timestamp).toLocaleString()}</td>
          <td><span class="pill ${e.state === 'cleared' ? 'cleared' : 'raised'}">${escapeHtml(e.state)}</span></td>
          <td>${escapeHtml(e.alertName)}</td>
          <td><a href="#/target/${e.targetId}">${escapeHtml(e.targetTitle)}</a></td>
          <td>${escapeHtml(e.comment)}</td>
        </tr>`).join('')
    : '<tr><td colspan="5" class="placeholder">Nothing so far.</td></tr>';

  content.innerHTML = `
    <h1>Alerts</h1>
    <p class="subtitle">${data.active.length} active, ${data.rules.length} rule(s) configured.</p>

    <div class="card">
      <h3>Active</h3>
      <table>
        <thead><tr><th>Since</th><th>State</th><th>Alert</th><th>Target</th><th>Comment</th></tr></thead>
        <tbody>${eventRows(data.active)}</tbody>
      </table>
    </div>

    <div class="card">
      <h3>Recent notifications</h3>
      <table>
        <thead><tr><th>When</th><th>State</th><th>Alert</th><th>Target</th><th>Comment</th></tr></thead>
        <tbody>${eventRows(data.recent)}</tbody>
      </table>
    </div>

    <div class="card">
      <h3>Rules</h3>
      <table>
        <thead><tr><th>Name</th><th>Type</th><th>Pattern</th><th>Edge</th><th>Comment</th></tr></thead>
        <tbody>
          ${data.rules.map((rule) => `
            <tr>
              <td>${escapeHtml(rule.name)}</td>
              <td>${escapeHtml(rule.type)}</td>
              <td><code>${escapeHtml(rule.pattern)}</code></td>
              <td>${rule.edgeTrigger ? 'yes' : 'no'}</td>
              <td>${escapeHtml(rule.comment)}</td>
            </tr>`).join('')}
        </tbody>
      </table>
    </div>`;
}

async function updateAlertBadge() {
  try {
    const data = await getJson('/api/alerts');
    const badge = document.getElementById('alert-badge');
    badge.textContent = String(data.active.length);
    badge.hidden = data.active.length === 0;
  } catch {
    /* the badge is decoration; a failure here must not break the page */
  }
}

/* ------------------------------------------------------------------ trace */

/**
 * The recorded, second-by-second view.
 *
 * The daemon records continuously; this only reads. That is the difference that
 * matters: what is on screen is history, so the interesting moment does not have to be
 * happening right now, or have been watched when it did, to be found afterwards.
 */
function startTrace(id) {
  stopTrace();
  trace = {
    id,
    range: state.config.trace.ranges[0].range,
    from: null,
    to: null,
    scale: localStorage.getItem('smokeping-trace-scale') || 'auto',
    timer: null,
    charts: [],
    resize: null,
    data: null,
    drawnAsLog: null,
  };

  for (const button of document.querySelectorAll('.trace-ranges button[data-range]')) {
    button.addEventListener('click', () => {
      trace.range = button.dataset.range;
      trace.from = null;
      trace.to = null;
      loadTrace();
    });
  }

  const scale = document.getElementById('trace-scale');
  if (scale) {
    scale.addEventListener('click', () => {
      // Flipped from what is on screen, not from the stored preference: while that is
      // still "auto" the two disagree, and a first click that changes nothing visible
      // reads as a broken button.
      trace.scale = trace.data && traceUsesLog(trace.data) ? 'linear' : 'log';
      localStorage.setItem('smokeping-trace-scale', trace.scale);
      loadTrace();
    });
  }

  loadTrace();
}

function stopTrace() {
  if (!trace) return;
  clearTimeout(trace.timer);
  destroyTraceCharts();
  trace = null;
}

/** Zooms to one event, with a little air either side so its edges are visible. */
function zoomTrace(start, end) {
  const padding = Math.max(30, Math.round((end - start) * 0.5));
  trace.from = start - padding;
  trace.to = end + padding;
  loadTrace();
}

async function loadTrace() {
  if (!trace) return;
  const current = trace;
  clearTimeout(current.timer);

  const params = new URLSearchParams();
  if (current.from && current.to) {
    params.set('from', String(current.from));
    params.set('to', String(current.to));
  } else {
    params.set('range', current.range);
  }

  let data;
  try {
    data = await getJson(`/api/trace/${current.id}?${params}`);
  } catch (error) {
    if (trace !== current) return;
    document.getElementById('trace-readout').innerHTML =
      `<span class="error">${escapeHtml(error.message)}</span>`;
    return;
  }

  // The view may have moved on while the request was in flight.
  if (trace !== current) return;
  current.data = data;

  for (const button of document.querySelectorAll('.trace-ranges button[data-range]')) {
    button.classList.toggle('active', !current.from && button.dataset.range === current.range);
  }

  updateTraceCharts(data);
  renderTraceSummary(data);
  renderTraceEvents(data);

  // Only a recent window is worth refetching; anything older is not changing, and a
  // zoomed-in look at a past moment should stay where it was put.
  const span = data.to - data.from;
  if (!current.from && span <= 3600) {
    current.timer = setTimeout(loadTrace, TRACE_REFRESH_MS);
  }
}

/** Picks a linear or logarithmic latency axis, honouring an explicit choice. */
function traceUsesLog(data) {
  if (trace.scale === 'log') return true;
  if (trace.scale === 'linear') return false;
  // A window holding both a 15 ms norm and a 2 s stall has nothing useful to show on
  // a linear axis: the norm collapses onto the baseline and only the spike is legible.
  return data.summary.maxMs > 0 && data.summary.medianMs > 0 &&
    data.summary.maxMs > data.summary.medianMs * 8;
}

/** Turns the API's row-per-second into the column-per-series arrays uPlot wants. */
function traceColumns(data) {
  const t = [];
  const max = [];
  const min = [];
  const avg = [];
  const jitter = [];
  const jitterMax = [];
  const loss = [];

  for (const s of data.samples) {
    t.push(s.t);
    max.push(s.max);
    min.push(s.min);
    avg.push(s.avg);
    jitter.push(s.jitter);
    jitterMax.push(s.jitterMax);
    // Null rather than zero where nothing was measured: a flat zero would claim a
    // clean second, and a gap is not a clean second.
    loss.push(s.sent ? (s.lost / s.sent) * 100 : null);
  }

  return {
    latency: [t, max, min, avg],
    jitter: [t, jitterMax, jitter],
    loss: [t, loss],
  };
}

/** The palette, read from the stylesheet so the charts follow the theme. */
function traceTheme() {
  const style = getComputedStyle(document.documentElement);
  const read = (name, fallback) => (style.getPropertyValue(name) || fallback).trim();
  return {
    accent: read('--accent', '#4da3ff'),
    danger: read('--danger', '#ff5d5d'),
    muted: read('--muted', '#98a2b3'),
    border: read('--border', '#262d38'),
    jitter: '#c58af9',
  };
}

/**
 * Builds the three plots.
 *
 * Separate charts rather than one with three axes, sharing a cursor: latency, jitter
 * and loss are measured in different units and read at different scales, and stacking
 * them keeps each one's shape legible while the crosshair still lines the three up at
 * the same instant.
 */
function buildTraceCharts(data) {
  destroyTraceCharts();

  const columns = traceColumns(data);
  if (columns.latency[0].length < 2) return;

  const theme = traceTheme();
  const width = document.querySelector('.trace-plots').clientWidth || 900;
  const useLog = traceUsesLog(data);
  document.getElementById('trace-scale').textContent = useLog ? 'log' : 'linear';

  const msAxis = (label) => ({
    size: 58,
    label,
    labelSize: 16,
    values: (u, values) => values.map(formatAxisMs),
  });

  const axis = (extra = {}) => ({
    stroke: theme.muted,
    grid: { stroke: theme.border, width: 1 },
    ticks: { stroke: theme.border, width: 1 },
    font: '11px "Segoe UI", system-ui, sans-serif',
    labelFont: '11px "Segoe UI", system-ui, sans-serif',
    labelGap: 2,
    ...extra,
  });

  // Drag across any of the three to zoom. The window is refetched rather than merely
  // rescaled, so zooming into two minutes of a day-long view returns the seconds
  // themselves instead of magnifying the summary that replaced them.
  const zoomOnSelect = (u) => {
    if (u.select.width < 4) return;
    const from = Math.round(u.posToVal(u.select.left, 'x'));
    const to = Math.round(u.posToVal(u.select.left + u.select.width, 'x'));
    u.setSelect({ left: 0, width: 0, top: 0, height: 0 }, false);
    if (to > from) zoomTrace(from, to);
  };

  const common = (height, extra) => ({
    width,
    height,
    cursor: {
      // One key across the three, so the crosshair is at the same instant in all of
      // them; that is what makes "the latency spike and the loss are the same event"
      // readable rather than a guess.
      sync: { key: 'trace', scales: ['x', null] },
      drag: { x: true, y: false },
      // Vertical only. A horizontal crosshair here reads as a threshold line - which
      // is a thing these charts genuinely have - and means nothing on a loss axis.
      y: false,
    },
    legend: { show: false },
    hooks: { setSelect: [zoomOnSelect], setCursor: [showTraceCursor] },
    ...extra,
  });

  trace.charts = [
    new uPlot(
      common(210, {
        scales: { y: { distr: useLog ? 3 : 1 } },
        // The clock is drawn once, under the bottom plot: the three share an x range,
        // and repeating it between them would separate charts meant to be read together.
        axes: [axis({ show: false }), axis(msAxis('round trip ms'))],
        // Filled between the fastest and slowest reading each column covers, so an
        // aggregated column shows its whole spread instead of only its average.
        bands: [{ series: [1, 2], fill: withAlpha(theme.accent, 0.2) }],
        series: [
          {},
          { label: 'slowest', stroke: withAlpha(theme.accent, 0.55), width: 1 },
          { label: 'fastest', stroke: withAlpha(theme.accent, 0.55), width: 1 },
          { label: 'typical', stroke: theme.accent, width: 1.6 },
        ],
        plugins: [thresholdLine(data.thresholds.roundTripMs, theme.danger)],
      }),
      columns.latency,
      document.getElementById('trace-latency')),

    new uPlot(
      common(104, {
        axes: [axis({ show: false }), axis(msAxis('jitter ms'))],
        bands: [{ series: [1, 2], fill: withAlpha(theme.jitter, 0.18) }],
        series: [
          {},
          { label: 'worst jitter', stroke: withAlpha(theme.jitter, 0.6), width: 1 },
          { label: 'jitter', stroke: theme.jitter, width: 1.4 },
        ],
        plugins: [thresholdLine(data.thresholds.jitterMs, theme.danger)],
      }),
      columns.jitter,
      document.getElementById('trace-jitter')),

    new uPlot(
      common(98, {
        scales: { y: { range: [0, 100] } },
        axes: [
          axis(),
          axis({ size: 58, label: 'loss', labelSize: 16, values: (u, v) => v.map((x) => `${x}%`) }),
        ],
        series: [
          {},
          {
            label: 'loss',
            stroke: theme.danger,
            fill: withAlpha(theme.danger, 0.5),
            // Bars, not a line: loss is a count of things that did not happen at a
            // moment, and joining those moments with a slope invents the ones between.
            paths: uPlot.paths.bars({ size: [1, 8] }),
          },
        ],
      }),
      columns.loss,
      document.getElementById('trace-loss')),
  ];

  // uPlot draws to a canvas of a fixed pixel size, so the width has to be handed to
  // it again whenever the column it lives in changes.
  trace.resize = new ResizeObserver(() => {
    const w = document.querySelector('.trace-plots')?.clientWidth;
    if (w) trace.charts.forEach((u) => u.setSize({ width: w, height: u.height }));
  });
  trace.resize.observe(document.querySelector('.trace-plots'));
}

/** Redraws the plots in place, keeping the cursor and the zoom the user set. */
function updateTraceCharts(data) {
  const useLog = traceUsesLog(data);
  if (!trace.charts.length || useLog !== trace.drawnAsLog) {
    trace.drawnAsLog = useLog;
    buildTraceCharts(data);
    return;
  }

  const columns = traceColumns(data);
  trace.charts[0].setData(columns.latency);
  trace.charts[1].setData(columns.jitter);
  trace.charts[2].setData(columns.loss);
}

function destroyTraceCharts() {
  if (!trace) return;
  trace.resize?.disconnect();
  trace.resize = null;
  for (const chart of trace.charts || []) {
    chart.destroy();
  }
  trace.charts = [];
}

/**
 * Draws the line a reading has to cross to be reported as a peak, so the chart and
 * the list underneath it visibly agree about what counts.
 */
function thresholdLine(value, colour) {
  return {
    hooks: {
      draw: (u) => {
        if (!value) return;
        const y = u.valToPos(value, 'y', true);
        if (!Number.isFinite(y)) return;
        const { ctx } = u;
        ctx.save();
        ctx.strokeStyle = colour;
        ctx.globalAlpha = 0.65;
        ctx.setLineDash([4, 4]);
        ctx.beginPath();
        ctx.moveTo(u.bbox.left, y);
        ctx.lineTo(u.bbox.left + u.bbox.width, y);
        ctx.stroke();
        ctx.restore();
      },
    },
  };
}

/**
 * Reports what every series was doing at the instant under the cursor.
 *
 * One readout for all three charts rather than a legend under each: the question is
 * "what happened at this moment", and the answer is latency, jitter and loss together.
 */
function showTraceCursor(u) {
  const readout = document.getElementById('trace-hover');
  if (!readout || !trace?.data) return;

  const index = u.cursor.idx;
  if (index === null || index === undefined) {
    readout.innerHTML = '&nbsp;';
    return;
  }

  const sample = trace.data.samples[index];
  if (!sample) return;

  const when = `<strong>${new Date(sample.t * 1000).toLocaleTimeString()}</strong>`;

  // Nothing was measured here, which is not the same as a clean reading and should
  // not be dressed up as a row of dashes.
  if (!sample.sent) {
    readout.innerHTML = `${when} &middot; not recorded`;
    return;
  }

  readout.innerHTML = [
    when,
    `${formatMs(sample.avg)} typical`,
    sample.max !== null && sample.max !== sample.avg ? `${formatMs(sample.max)} slowest` : null,
    `${formatMs(sample.jitterMax)} jitter`,
    sample.lost ? `<span class="hot">${sample.lost}/${sample.sent} lost</span>` : `${sample.sent} sent`,
  ].filter(Boolean).join(' &middot; ');
}

function withAlpha(colour, alpha) {
  const hex = colour.replace('#', '');
  const full = hex.length === 3 ? [...hex].map((c) => c + c).join('') : hex;
  const n = parseInt(full, 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

/** Axis labels: short enough to fit, and in seconds once milliseconds get silly. */
function formatAxisMs(value) {
  if (value === null) return '';
  if (value >= 1000) return `${value / 1000}s`;
  if (value >= 10) return String(Math.round(value));
  if (value >= 1) return value.toFixed(1);
  return value.toFixed(2);
}

/** Describes the window on screen, since it is not always one of the range buttons. */
function describeWindow(data) {
  const span = data.to - data.from;
  if (trace && trace.from) {
    return `${new Date(data.from * 1000).toLocaleTimeString()} - ${new Date(data.to * 1000).toLocaleTimeString()}`;
  }
  if (span < 3600) return `${Math.round(span / 60)} min`;
  return `${Math.round(span / 3600)}h`;
}

/** The window summary, shown whenever the cursor is not over the plots. */
function renderTraceSummary(data) {
  const s = data.summary;
  document.getElementById('trace-readout').innerHTML = [
    `<strong>${s.eventCount}</strong> peak${s.eventCount === 1 ? '' : 's'} in ${describeWindow(data)}`,
    `median ${formatMs(s.medianMs)}`,
    `95th ${formatMs(s.p95Ms)}`,
    `worst ${formatMs(s.maxMs)}`,
    `jitter ${formatMs(s.medianJitterMs)} / ${formatMs(s.maxJitterMs)} worst`,
    `loss ${s.lossPercent === null ? '-' : s.lossPercent.toFixed(2)}% of ${s.sent}`,
    `above ${formatMs(data.thresholds.roundTripMs)} counts`,
    // Said out loud when it is not one second, because at summary resolution a
    // two-second stall is reported as the whole slot that contained it.
    ...(data.resolutionSeconds > 1 ? [`summarised ${data.resolutionSeconds}s at a time`] : []),
  ].join(' &middot; ');
}

/** The peak list: the moments worth looking at, with a way to look at them. */
function renderTraceEvents(data) {
  const container = document.getElementById('trace-events');
  if (!container) return;

  const back = trace && trace.from
    ? `<button type="button" class="trace-back">&larr; back to ${escapeHtml(trace.range)}</button>`
    : '';

  if (!data.events.length) {
    container.innerHTML = `${back}<p class="trace-empty">Nothing crossed the line in this window
      &mdash; no loss, and nothing slower than ${formatMs(data.thresholds.roundTripMs)}.</p>`;
    bindTraceBack(container);
    return;
  }

  container.innerHTML = `${back}
    <table class="trace-table">
      <thead><tr>
        <th>When</th><th>For</th><th>What</th>
        <th class="num">Peak</th><th class="num">Jitter</th><th class="num">Lost</th><th></th>
      </tr></thead>
      <tbody>
        ${data.events.map((e) => `
          <tr>
            <td>${new Date(e.start * 1000).toLocaleTimeString()}</td>
            <td>${data.resolutionSeconds > 1 ? '&le;' : ''}${e.durationSeconds}s</td>
            <td>${e.kinds.map((k) => `<span class="kind kind-${k}">${k}</span>`).join(' ')}</td>
            <td class="num">${formatMs(e.peakMs)}</td>
            <td class="num">${formatMs(e.peakJitterMs)}</td>
            <td class="num">${e.lost ? `${e.lost}/${e.sent}` : '-'}</td>
            <td><button type="button" class="trace-zoom" data-start="${e.start}" data-end="${e.end}">look</button></td>
          </tr>`).join('')}
      </tbody>
    </table>`;

  for (const button of container.querySelectorAll('.trace-zoom')) {
    button.addEventListener('click', () =>
      zoomTrace(Number(button.dataset.start), Number(button.dataset.end)));
  }
  bindTraceBack(container);
}

function bindTraceBack(container) {
  const back = container.querySelector('.trace-back');
  if (!back) return;
  back.addEventListener('click', () => {
    trace.from = null;
    trace.to = null;
    loadTrace();
  });
}

/* ------------------------------------------------------------------ shell */

async function render() {
  stopTrace();
  state.route = parseRoute();
  highlightNav();
  renderMenu();

  try {
    switch (state.route.view) {
      case 'target':
        await renderTarget(state.route.id);
        break;
      case 'folder': {
        const node = findNode(state.config.menu, state.route.id);
        if (!node) {
          document.getElementById('content').innerHTML = '<p class="error">Unknown section.</p>';
          break;
        }
        await renderOverview(node.children || [], node.title, node.description);
        break;
      }
      case 'charts':
        await renderCharts();
        break;
      case 'alerts':
        await renderAlerts();
        break;
      default:
        await renderOverview(state.config.menu, state.config.siteName, `${state.config.targetCount} target(s), last ${OVERVIEW_RANGE}`);
    }
  } catch (error) {
    document.getElementById('content').innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`;
  }

  updateAlertBadge();
}

function applyTheme() {
  document.documentElement.dataset.theme = state.theme;
  document.getElementById('theme-toggle').textContent = state.theme === 'dark' ? 'Light' : 'Dark';
}

function scheduleRefresh() {
  clearInterval(state.timer);
  if (document.getElementById('auto-refresh').checked) {
    state.timer = setInterval(render, REFRESH_MS);
  }
}

async function start() {
  applyTheme();

  try {
    state.config = await getJson('/api/config');
  } catch (error) {
    document.getElementById('content').innerHTML =
      `<p class="error">Could not load the configuration: ${escapeHtml(error.message)}</p>`;
    return;
  }

  document.getElementById('site-name').textContent = state.config.siteName;
  document.getElementById('site-owner').textContent = state.config.owner || '';
  document.title = state.config.siteName;

  document.getElementById('theme-toggle').addEventListener('click', () => {
    state.theme = state.theme === 'dark' ? 'light' : 'dark';
    localStorage.setItem('smokeping-theme', state.theme);
    applyTheme();
    render();
  });

  document.getElementById('auto-refresh').addEventListener('change', scheduleRefresh);
  window.addEventListener('hashchange', render);

  await render();
  scheduleRefresh();
}

start();
