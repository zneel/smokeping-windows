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

/** How many seconds of live trace stay on screen. */
const LIVE_WINDOW = 180;

/** The open live stream, if any. Only one runs at a time. */
let live = null;

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
    ${state.config.live && state.config.live.enabled ? `
      <div class="card">
        <h3>Live
          <button id="live-toggle" type="button">Start</button>
          <span id="live-readout" class="live-readout"></span>
        </h3>
        <svg id="live-chart" class="live-chart" viewBox="0 0 900 160" preserveAspectRatio="none"></svg>
        <p class="live-note">One probe a second, shown as it happens. Not recorded &mdash; the
          stored graphs above keep their own schedule.</p>
      </div>` : ''}
    ${state.config.detailRanges.map((r) => `
      <div class="card">
        <h3>${escapeHtml(r.label)}</h3>
        <div class="graph" data-range="${r.range}"></div>
      </div>`).join('')}`;

  const toggle = document.getElementById('live-toggle');
  if (toggle) {
    toggle.addEventListener('click', () => (live ? stopLive() : startLive(id)));
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

/* ------------------------------------------------------------------- live */

/**
 * Opens the live stream for a target and draws it as it arrives.
 *
 * The server sends one probe per second over server-sent events; this keeps the last
 * few minutes and redraws a strip chart on each sample. Nothing here is stored, and
 * closing the view stops the probing.
 */
function startLive(id) {
  stopLive();

  const samples = [];
  const source = new EventSource(`/api/live/${id}`);
  live = { source, samples };

  const toggle = document.getElementById('live-toggle');
  if (toggle) {
    toggle.textContent = 'Stop';
    toggle.classList.add('running');
  }

  source.addEventListener('error', (event) => {
    // A stream the server refused carries a message; a dropped connection does not,
    // and EventSource retries that on its own.
    if (event.data) {
      setLiveReadout(`stopped: ${event.data}`);
      stopLive();
    }
  });

  source.onmessage = (event) => {
    samples.push(JSON.parse(event.data));
    while (samples.length > LIVE_WINDOW) {
      samples.shift();
    }

    drawLive(samples);
  };
}

function stopLive() {
  if (!live) return;
  live.source.close();
  live = null;

  const toggle = document.getElementById('live-toggle');
  if (toggle) {
    toggle.textContent = 'Start';
    toggle.classList.remove('running');
  }
}

function setLiveReadout(text) {
  const readout = document.getElementById('live-readout');
  if (readout) readout.textContent = text;
}

function isLost(sample) {
  return sample.rtt === null || sample.rtt === undefined;
}

/** Draws the live trace: a line for the round trip time, marks where a probe was lost. */
function drawLive(samples) {
  const chart = document.getElementById('live-chart');
  if (!chart) return;

  const width = 900;
  const height = 160;
  const answered = samples.filter((s) => !isLost(s));
  const lost = samples.length - answered.length;

  // Scale to the slowest answer with headroom, so the trace fills the strip.
  const peak = answered.length ? Math.max(...answered.map((s) => s.rtt)) : 1;
  const top = (peak * 1.2) || 1;
  // Newest at the right edge, so the trace scrolls in the way a live monitor does
  // rather than filling from the left while the window is still short.
  const step = width / LIVE_WINDOW;
  const x = (i) => width - ((samples.length - 1 - i) * step);
  const y = (v) => height - (Math.min(v / top, 1) * (height - 12));

  const parts = [];

  samples.forEach((s, i) => {
    if (isLost(s)) {
      parts.push(`<rect x="${x(i).toFixed(1)}" y="0" width="${Math.max(step, 1).toFixed(1)}" height="${height}" fill="#ff5d5d" fill-opacity="0.28"/>`);
    }
  });

  // Break the line at losses rather than drawing straight through them.
  let run = [];
  const flush = () => {
    if (run.length > 1) {
      parts.push(`<polyline points="${run.join(' ')}" fill="none" stroke="#26ff00" stroke-width="1.6"/>`);
    }
    run = [];
  };

  samples.forEach((s, i) => {
    if (isLost(s)) {
      flush();
      return;
    }
    run.push(`${x(i).toFixed(1)},${y(s.rtt).toFixed(1)}`);
  });
  flush();

  chart.innerHTML = parts.join('');

  const latest = samples[samples.length - 1];
  const current = latest && !isLost(latest) ? formatMs(latest.rtt) : 'lost';
  const sorted = answered.map((s) => s.rtt).sort((a, b) => a - b);
  const median = sorted.length ? formatMs(sorted[Math.floor(sorted.length / 2)]) : '-';

  setLiveReadout(`${current} now  \u00b7  ${median} median  \u00b7  ${formatMs(peak)} peak  \u00b7  ${lost}/${samples.length} lost`);
}

/* ------------------------------------------------------------------ shell */

async function render() {
  stopLive();
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
