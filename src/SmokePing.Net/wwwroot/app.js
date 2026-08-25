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
        <svg id="trace-chart" class="trace-chart" viewBox="0 0 900 268"></svg>
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

  if (document.getElementById('trace-chart')) {
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

  drawTrace(data);
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

function drawTrace(data) {
  const chart = document.getElementById('trace-chart');
  if (!chart) return;

  const left = 52;
  const right = 894;
  const width = right - left;
  const latencyTop = 10;
  const latencyBottom = 150;
  const jitterTop = 174;
  const jitterBottom = 216;
  const lossTop = 222;
  const lossBottom = 240;

  const samples = data.samples;
  const useLog = traceUsesLog(data);
  document.getElementById('trace-scale').textContent = useLog ? 'log' : 'linear';

  const span = Math.max(1, data.to - data.from);
  const x = (t) => left + ((t - data.from) / span) * width;
  const columnWidth = Math.max(width / Math.max(samples.length, 1), 0.8);

  const peak = Math.max(data.summary.maxMs || 0, data.thresholds.roundTripMs || 0, 1);
  const floor = useLog ? Math.max(0.1, Math.min(...samples.filter((s) => s.min !== null).map((s) => s.min), peak) * 0.8) : 0;
  const top = peak * 1.1;

  const scaleY = (v) => {
    if (v === null || v === undefined) return null;
    const height = latencyBottom - latencyTop;
    if (!useLog) return latencyBottom - Math.min(v / top, 1) * height;
    const lo = Math.log10(floor);
    const fraction = (Math.log10(Math.max(v, floor)) - lo) / (Math.log10(top) - lo);
    return latencyBottom - Math.min(Math.max(fraction, 0), 1) * height;
  };

  const jitterPeak = Math.max(data.summary.maxJitterMs || 0, data.thresholds.jitterMs || 0, 1) * 1.1;
  const jitterY = (v) => jitterBottom - Math.min((v || 0) / jitterPeak, 1) * (jitterBottom - jitterTop);

  const parts = [];

  // Gridlines first, so nothing measured is drawn underneath them.
  for (const tick of axisTicks(floor, top, useLog)) {
    const ty = scaleY(tick);
    parts.push(`<line x1="${left}" y1="${ty.toFixed(1)}" x2="${right}" y2="${ty.toFixed(1)}" class="trace-grid"/>`);
    parts.push(`<text x="${left - 6}" y="${(ty + 3.5).toFixed(1)}" class="trace-label" text-anchor="end">${formatTick(tick)}</text>`);
  }

  parts.push(`<text x="${left - 6}" y="${latencyTop}" class="trace-label" text-anchor="end">ms</text>`);

  // The threshold: everything above this line is what the peak list is reporting.
  if (data.thresholds.roundTripMs) {
    const ty = scaleY(data.thresholds.roundTripMs);
    parts.push(`<line x1="${left}" y1="${ty.toFixed(1)}" x2="${right}" y2="${ty.toFixed(1)}" class="trace-threshold"/>`);
  }

  // The min-max envelope, so a column covering many probes still shows its worst one.
  const upper = [];
  const lower = [];
  const flushEnvelope = () => {
    if (upper.length > 1) {
      parts.push(`<path d="M${upper.join('L')}L${lower.reverse().join('L')}Z" class="trace-envelope"/>`);
    }
    upper.length = 0;
    lower.length = 0;
  };

  for (const s of samples) {
    if (s.max === null || s.min === null) {
      flushEnvelope();
      continue;
    }
    upper.push(`${x(s.t).toFixed(1)},${scaleY(s.max).toFixed(1)}`);
    lower.push(`${x(s.t).toFixed(1)},${scaleY(s.min).toFixed(1)}`);
  }
  flushEnvelope();

  parts.push(...brokenLine(samples, (s) => s.avg, x, scaleY, 'trace-line'));
  parts.push(...brokenLine(samples, (s) => s.jitterMax, x, jitterY, 'trace-jitter'));

  // Loss last and on its own strip: it is the one thing that should never be missed.
  for (const s of samples) {
    if (!s.lost) continue;
    const height = (lossBottom - lossTop) * Math.min(s.lost / Math.max(s.sent, 1), 1);
    parts.push(`<rect x="${x(s.t).toFixed(1)}" y="${(lossBottom - height).toFixed(1)}" width="${columnWidth.toFixed(1)}" height="${height.toFixed(1)}" class="trace-loss"/>`);
  }

  parts.push(`<line x1="${left}" y1="${lossBottom}" x2="${right}" y2="${lossBottom}" class="trace-grid"/>`);
  parts.push(`<text x="${left - 6}" y="${jitterBottom}" class="trace-label" text-anchor="end">jitter</text>`);
  parts.push(`<text x="${left - 6}" y="${lossBottom}" class="trace-label" text-anchor="end">loss</text>`);

  const times = timeTicks(data.from, data.to);
  times.forEach((t, i) => {
    // The end labels sit on the plot's edges, so centring them would push half of
    // each outside the drawing and the clock would read "07:52:4".
    const anchor = i === 0 ? 'start' : (i === times.length - 1 ? 'end' : 'middle');
    parts.push(`<text x="${x(t).toFixed(1)}" y="258" class="trace-label" text-anchor="${anchor}">${formatClock(t, span)}</text>`);
  });

  chart.innerHTML = parts.join('');

  const s = data.summary;
  const label = describeWindow(data);
  document.getElementById('trace-readout').innerHTML = [
    `<strong>${s.eventCount}</strong> peak${s.eventCount === 1 ? '' : 's'} in ${label}`,
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

/** Describes the window on screen, since it is not always one of the range buttons. */
function describeWindow(data) {
  const span = data.to - data.from;
  if (trace && trace.from) {
    return `${new Date(data.from * 1000).toLocaleTimeString()} - ${new Date(data.to * 1000).toLocaleTimeString()}`;
  }
  if (span < 3600) return `${Math.round(span / 60)} min`;
  return `${Math.round(span / 3600)}h`;
}

/**
 * Draws a series as a line that stops at gaps rather than running through them: a
 * straight line across an outage would claim measurements that were never taken.
 */
function brokenLine(samples, pick, x, y, className) {
  const parts = [];
  let run = [];
  const flush = () => {
    if (run.length > 1) {
      parts.push(`<polyline points="${run.join(' ')}" class="${className}"/>`);
    } else if (run.length === 1) {
      const [px, py] = run[0].split(',');
      parts.push(`<circle cx="${px}" cy="${py}" r="1.1" class="${className}-dot"/>`);
    }
    run = [];
  };

  for (const s of samples) {
    const value = pick(s);
    if (value === null || value === undefined) {
      flush();
      continue;
    }
    run.push(`${x(s.t).toFixed(1)},${y(value).toFixed(1)}`);
  }
  flush();
  return parts;
}

function axisTicks(floor, top, useLog) {
  if (useLog) {
    const ticks = [];
    for (let power = Math.floor(Math.log10(Math.max(floor, 0.1))); Math.pow(10, power) <= top; power++) {
      const value = Math.pow(10, power);
      if (value >= floor) ticks.push(value);
    }
    return ticks.length > 1 ? ticks : [floor, top];
  }
  return [0.25, 0.5, 0.75, 1].map((f) => top * f);
}

function formatTick(value) {
  if (value >= 1000) return `${(value / 1000).toFixed(0)}s`;
  if (value >= 10) return `${value.toFixed(0)}`;
  return `${value.toFixed(1)}`;
}

function timeTicks(from, to) {
  const count = 6;
  const ticks = [];
  for (let i = 0; i <= count; i++) {
    ticks.push(from + Math.round(((to - from) * i) / count));
  }
  return ticks;
}

function formatClock(seconds, span) {
  const date = new Date(seconds * 1000);
  const options = span <= 1800
    ? { hour: '2-digit', minute: '2-digit', second: '2-digit' }
    : { hour: '2-digit', minute: '2-digit' };
  return date.toLocaleTimeString([], options);
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
