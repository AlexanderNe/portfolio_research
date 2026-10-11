"use strict";
const {
  esc, fmtPrice, fmtPct, fmtMoney, fmtDay, mskDayKey, setPosCls, signalTypeBadge
} = PTS;
const fmtTime = PTS.fmtTimeShort;
const fmtMoneyAxis = PTS.fmtMoneyPlain;
const {
  fitCanvas, touchDist, hideTip, showTip, maxZoomWidth,
  zoomWindow, panWindow, leftPadFor, valueGrid, dashedHLine, clipPlot,
} = Charting;

const cv = document.getElementById("cv");
const ctx = cv.getContext("2d");
const tip = document.getElementById("tip");
let data = null;
let scale = null;
let W = 0, H = 560;
const PAD = { l: 64, r: 16, t: 16, b: 24 };
let view = { from: 0, to: 1 };
let priceView = null;
let _autoMin = 0, _autoMax = 1;
let panning = null;
let initialized = false;
let mode = "price";
let eqData = null;
let eqScale = null;
let eqView = null;
let eqError = null;

document.getElementById("tabPrice").onclick = () => switchMode("price");
document.getElementById("tabEquity").onclick = () => switchMode("equity");

async function load() {
  const id = new URLSearchParams(location.search).get("id");
  if (!id) { fail("Missing id parameter. Open the page from the admin panel."); return; }
  try {
    const res = await fetch("/api/instruments/" + id + "/details", { credentials: "same-origin" });
    if (!res.ok) {
      if (res.status === 401) { fail("Authentication required."); return; }
      if (data) return;
      fail("HTTP " + res.status + ": " + (await res.text()));
      return;
    }
    data = await res.json();
    // The user's zoom/price range survive background refreshes; only the very
    // first load picks the default 3-month window.
    if (!initialized) {
      const dv = defaultView(data.candles);
      view = dv.view;
      priceView = dv.range;
      initialized = true;
    }
    document.getElementById("title").textContent =
      (data.name ? data.name + " " : "") + data.ticker + " — instrument details";
    render();
    renderDetails();
    if (mode === "equity") loadEquity();
  } catch (err) {
    if (!data) fail("Failed to load instrument details: " + err.message);
  }
}

function fail(msg) {
  document.getElementById("title").textContent = "Instrument details unavailable";
  const existing = document.querySelector("main .errorbox");
  if (existing) { existing.textContent = msg; existing.style.display = "block"; return; }
  const box = document.createElement("div");
  box.className = "errorbox";
  box.style.display = "block";
  box.textContent = msg;
  document.querySelector("main").appendChild(box);
}

function signalRowHtml(s) {
  const dir = s.direction === "Long" ? '<span class="badge green">LONG</span>'
    : s.direction === "Short" ? '<span class="badge red">SHORT</span>' : "—";
  return '<tr>' +
    '<td data-label="Time" class="mono">' + fmtTime(s.timestamp) + '</td>' +
    '<td data-label="Type">' + signalTypeBadge(s.type) + '</td>' +
    '<td data-label="Dir">' + dir + '</td>' +
    '<td data-label="Price" class="mono num">' + fmtPrice(s.price) + '</td>' +
    '<td data-label="SL" class="mono num">' + fmtPrice(s.stopLoss) + '</td>' +
    '<td data-label="TP" class="mono num">' + fmtPrice(s.takeProfit) + '</td>' +
    '<td data-label="Exit" class="mono num">' + fmtPrice(s.exitPrice) + '</td>' +
    '<td data-label="Exit reason">' + (s.exitReason ? esc(s.exitReason) : "—") + '</td>' +
    '<td data-label="PnL" class="mono num">' + fmtPct(s.pnlPercent) + '</td>' +
    '</tr>';
}

function renderDetails() {
  const d = data;
  if (!d) return;

  const shouldAddSign = v => v !== null && v !== undefined && !isNaN(v);

  const totalEl = document.getElementById("pnlTotal");
  totalEl.textContent = fmtMoney(d.totalPnlRub) + " RUB";
  document.getElementById("pnlTotalPct").textContent = fmtPct(d.totalPnlPercent);
  setPosCls(document.getElementById("pnlCardTotal"), Number(d.totalPnlRub));

  const uEl = document.getElementById("pnlUnrl");
  const uPEl = document.getElementById("pnlUnrlPct");
  const uCard = document.getElementById("pnlCardUnrl");
  if (shouldAddSign(d.unrealizedPnlRub)) {
    uEl.textContent = fmtMoney(d.unrealizedPnlRub) + " RUB";
    uPEl.textContent = fmtPct(d.unrealizedPnlPercent);
    setPosCls(uCard, Number(d.unrealizedPnlRub));
  } else {
    uEl.textContent = "—";
    uPEl.textContent = "—";
    setPosCls(uCard, null);
  }

  const pendEl = document.getElementById("pendingSignal");
  const pendCard = document.getElementById("pendingCard");
  pendCard.classList.remove("up", "down");
  if (d.pendingSignal === null || d.pendingSignal === undefined) {
    pendEl.textContent = "—";
  } else if (d.pendingSignal > 0) {
    pendEl.textContent = "LONG";
    pendCard.classList.add("up");
  } else if (d.pendingSignal < 0) {
    pendEl.textContent = "SHORT";
    pendCard.classList.add("down");
  } else {
    pendEl.textContent = "none";
  }

  const rows = (d.signals || []).slice().reverse();
  const tbody = document.getElementById("sigRows");
  tbody.innerHTML = rows.length
    ? rows.map(signalRowHtml).join("")
    : '<tr><td colspan="9" class="empty">No signals yet.</td></tr>';
  const upd = document.getElementById("sigUpdated");
  if (upd) upd.textContent = "updated " + new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function resize() {
  const d = fitCanvas(cv, ctx);
  W = d.w; H = d.h;
}

function computeScale() {
  const cs = data.candles || [];
  PAD.l = 64;
  _autoMin = Infinity; _autoMax = -Infinity;
  cs.forEach(c => {
    if (c.isPlaceholder) return;
    if (c.low < _autoMin) _autoMin = c.low;
    if (c.high > _autoMax) _autoMax = c.high;
  });
  if (data.openPosition) {
    [data.openPosition.stopLoss, data.openPosition.takeProfit, data.openPosition.entryPrice].forEach(p => {
      _autoMin = Math.min(_autoMin, p);
      _autoMax = Math.max(_autoMax, p);
    });
  }
  if (!isFinite(_autoMin)) { _autoMin = 0; _autoMax = 1; }
  const padPct = 0.06 * (_autoMax - _autoMin) || 1;
  _autoMin -= padPct; _autoMax += padPct;

  const plotW = W - PAD.l - PAD.r, plotH = H - PAD.t - PAD.b;
  const n = cs.length;
  const windowFrac = Math.max(1e-6, view.to - view.from);
  const priceMin = priceView ? priceView.min : _autoMin;
  const priceMax = priceView ? priceView.max : _autoMax;
  const priceSpan = Math.max(1e-9, priceMax - priceMin);
  scale = {
    cs, n, plotW, plotH, windowFrac,
    candleW: Math.max(1, Math.min(20, n > 0 ? plotW / windowFrac / n * 0.7 : 20)),
    xFor: i => n === 0 ? PAD.l : PAD.l + ((i / n - view.from) / windowFrac) * plotW,
    yFor: p => PAD.t + plotH - (p - priceMin) / priceSpan * plotH,
    candleDays: cs.map(c => mskDayKey(c.time)),
    minPrice: priceMin,
    maxPrice: priceMax,
  };
}

function candleIndexFor(time) {
  if (!time || scale.n === 0) return 0;
  const day = mskDayKey(time);
  const keys = scale.candleDays;
  if (day === null || !keys || keys.length === 0) return 0;
  if (day <= keys[0]) return 0;
  if (day >= keys[keys.length - 1]) return keys.length - 1;
  let lo = 0, hi = keys.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (keys[mid] <= day) lo = mid; else hi = mid - 1;
  }
  return lo;
}

function drawGrid() {
  valueGrid(ctx, {
    yFor: scale.yFor, yMin: scale.minPrice, yMax: scale.maxPrice,
    left: PAD.l, right: W - PAD.r, label: fmtPrice,
  });
  if (scale.n === 0) return;
  const labelCount = Math.min(scale.n, 8);
  ctx.textAlign = "center"; ctx.textBaseline = "top";
  let prevX = -100;
  for (let i = 0; i < labelCount; i++) {
    const f = view.from + (i / Math.max(1, labelCount - 1)) * (view.to - view.from);
    if (f < 0 || f > 1) continue;
    const idx = Math.min(scale.n - 1, Math.max(0, Math.round(f * scale.n)));
    const x = scale.xFor(idx);
    if (x - prevX < 46) continue;
    prevX = x;
    ctx.fillText(fmtDay(scale.cs[idx].time), x, H - PAD.b + 6);
  }
}

function applyHorizontal(mx, factor) {
  const s = mode === "equity" ? eqScale : scale;
  if (!s || s.n === 0) return;
  const minWidth = mode === "equity"
    ? Math.min(1, (6 * 3600 * 1000) / Math.max(1, s.maxTime - s.minTime))
    : Math.min(1, 2 / s.n);
  zoomWindow(view, mx, PAD.l, s.plotW, factor, minWidth);
}

function zoomHorizontal(mx, factor) {
  applyHorizontal(mx, factor);
  render();
}

function applyVertical(my, factor) {
  const s = mode === "equity" ? eqScale : scale;
  if (!s || s.n === 0) return;
  const minSpan = Math.max(1e-9, mode === "equity"
    ? (s.autoMax - s.autoMin) * 0.002
    : (_autoMax - _autoMin) * 0.002);
  const fy = Math.min(1, Math.max(0, (my - PAD.t) / Math.max(1, s.plotH)));
  const curMin = s.minPrice, curMax = s.maxPrice;
  const curSpan = curMax - curMin;
  const newSpan = Math.max(minSpan, curSpan * factor);
  const anchor = curMax - fy * curSpan;
  const newMax = anchor + fy * newSpan;
  const range = { min: newMax - newSpan, max: newMax };
  if (mode === "equity") eqView = range; else priceView = range;
}

function zoomVertical(my, factor) {
  applyVertical(my, factor);
  render();
}

function panBy(dx, dy) {
  const s = mode === "equity" ? eqScale : scale;
  if (!s || s.n === 0) return;
  if (dx !== 0) panWindow(view, dx, s.plotW);
  if (dy !== 0) {
    const span = s.maxPrice - s.minPrice;
    const shift = (dy / Math.max(1, s.plotH)) * span;
    const range = { min: s.minPrice + shift, max: s.maxPrice + shift };
    if (mode === "equity") eqView = range; else priceView = range;
  }
}

function drawCandles() {
  const up = "#2e9e5b", dn = "#d64545";
  scale.cs.forEach((c, i) => {
    const x = scale.xFor(i);
    if (c.isPlaceholder) {
      const y = scale.yFor(c.close);
      const bw = Math.max(1, scale.candleW);
      ctx.strokeStyle = "#42536e"; ctx.lineWidth = 1;
      ctx.setLineDash([3, 3]);
      ctx.beginPath(); ctx.moveTo(x - bw * 0.5, y); ctx.lineTo(x + bw * 0.5, y); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(x, y - 5); ctx.lineTo(x, y + 5); ctx.stroke();
      ctx.setLineDash([]);
      return;
    }
    const o = scale.yFor(c.open), h = scale.yFor(c.high), l = scale.yFor(c.low), cl = scale.yFor(c.close);
    const color = c.close >= c.open ? up : dn;
    ctx.strokeStyle = color; ctx.fillStyle = color; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(x, h); ctx.lineTo(x, l); ctx.stroke();
    const top = Math.min(o, cl), bot = Math.max(o, cl);
    const bw = Math.max(1, scale.candleW);
    ctx.fillRect(x - bw / 2, top, bw, Math.max(1, bot - top));
  });
}

function drawTradeLines() {
  (data.trades || []).forEach(t => {
    const x1 = scale.xFor(candleIndexFor(t.entryTime));
    const x2 = scale.xFor(candleIndexFor(t.exitTime));
    const y1 = scale.yFor(t.entryPrice);
    const y2 = scale.yFor(t.exitPrice);
    ctx.strokeStyle = t.direction === "Long" ? "rgba(46,158,91,.45)" : "rgba(214,69,69,.45)";
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke();
  });
}

function drawMarkers() {
  (data.trades || []).forEach(t => {
    drawEntry(t.direction, t.entryTime, t.entryPrice);
    drawExit(t.exitTime, t.exitPrice, t.pnlPercent);
  });
  if (data.openPosition) {
    drawEntry(data.openPosition.direction, data.openPosition.entryTime, data.openPosition.entryPrice);
  }
}

function drawEntry(direction, time, price) {
  const x = scale.xFor(candleIndexFor(time));
  const y = scale.yFor(price);
  const isLong = direction === "Long";
  ctx.fillStyle = isLong ? "#2e9e5b" : "#d64545";
  ctx.strokeStyle = "#fff"; ctx.lineWidth = 1;
  const s = 11;
  ctx.beginPath();
  if (isLong) { ctx.moveTo(x - s / 2, y + s * 0.4); ctx.lineTo(x + s / 2, y + s * 0.4); ctx.lineTo(x, y - s * 0.5); }
  else { ctx.moveTo(x - s / 2, y - s * 0.4); ctx.lineTo(x + s / 2, y - s * 0.4); ctx.lineTo(x, y + s * 0.5); }
  ctx.closePath(); ctx.fill(); ctx.stroke();
}

function drawExit(time, price, pnl) {
  const x = scale.xFor(candleIndexFor(time));
  const y = scale.yFor(price);
  const color = (pnl === null || pnl === undefined || pnl === 0) ? "#9aa7b8"
              : pnl > 0 ? "#2e9e5b" : "#d64545";
  ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.fillStyle = "#161d29";
  ctx.beginPath(); ctx.arc(x, y, 5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  ctx.beginPath();
  ctx.moveTo(x - 3, y - 3); ctx.lineTo(x + 3, y + 3);
  ctx.moveTo(x + 3, y - 3); ctx.lineTo(x - 3, y + 3);
  ctx.stroke();
}

function drawLevels() {
  if (!data.openPosition) return;
  const op = data.openPosition;
  const draw = (price, label, color) => {
    const y = scale.yFor(price);
    dashedHLine(ctx, PAD.l, W - PAD.r, y, color, [6, 4], 1.5);
    ctx.fillStyle = color; ctx.font = "12px Segoe UI, sans-serif";
    ctx.textAlign = "right"; ctx.textBaseline = "middle";
    ctx.fillText(label + " " + fmtPrice(price), W - PAD.r - 6, y);
  };
  draw(op.takeProfit, "TP", "#2e9e5b");
  draw(op.entryPrice, "ENTRY", "#e8b36f");
  draw(op.stopLoss, "SL", "#d64545");
}

function buildLegend() {
  const parts = ["candles: " + (scale ? scale.n : 0)];
  parts.push("trades: " + (data.trades || []).length);
  if (data.openPosition) {
    const op = data.openPosition;
    const dir = op.direction === "Long" ? "LONG" : "SHORT";
    parts.push(dir + " " + op.units + " @ " + fmtPrice(op.entryPrice) +
      "  SL " + fmtPrice(op.stopLoss) + "  TP " + fmtPrice(op.takeProfit));
  } else {
    parts.push("position: flat");
  }
  document.getElementById("legend").textContent = parts.join("   ·   ");
}

async function loadEquity() {
  const id = new URLSearchParams(location.search).get("id");
  if (!id) return;
  try {
    const res = await fetch("/api/instruments/" + id + "/equity-data", { credentials: "same-origin" });
    if (!res.ok) throw new Error("HTTP " + res.status + ": " + (await res.text()));
    eqData = await res.json();
    eqError = null;
    render();
  } catch (err) {
    eqError = "equity unavailable: " + err.message;
    render();
  }
}

function switchMode(m) {
  if (m === mode) return;
  mode = m;
  document.getElementById("tabPrice").classList.toggle("active", m === "price");
  document.getElementById("tabEquity").classList.toggle("active", m === "equity");
  view = { from: 0, to: 1 };
  priceView = null;
  eqView = null;
  if (m === "equity" && !eqData) loadEquity();
  render();
}

function computeEquityScale() {
  const pts = (eqData.points || []).filter(p => {
    const t = new Date(p.time).getTime();
    return !isNaN(t) && isFinite(Number(p.equity));
  });
  const times = pts.map(p => new Date(p.time).getTime());
  const values = pts.map(p => Number(p.equity));
  let tMin = Infinity, tMax = -Infinity, eMin = Infinity, eMax = -Infinity;
  times.forEach(t => { if (t < tMin) tMin = t; if (t > tMax) tMax = t; });
  values.forEach(e => { if (e < eMin) eMin = e; if (e > eMax) eMax = e; });
  if (tMax === tMin) { tMin -= 86400000; tMax += 86400000; }
  const tPad = (tMax - tMin) * 0.03;
  tMin -= tPad; tMax += tPad;
  eMin = isFinite(eMin) ? eMin : Number(eqData.initialPool);
  eMax = isFinite(eMax) ? eMax : Number(eqData.equity);
  const ePad = Math.max(1, (eMax - eMin) * 0.06);
  const autoMin = eMin - ePad, autoMax = eMax + ePad;
  const eVMin = eqView ? eqView.min : autoMin;
  const eVMax = eqView ? eqView.max : autoMax;
  const eSpan = Math.max(1e-9, eVMax - eVMin);
  const labels = [];
  for (let i = 0; i <= 6; i++) labels.push(fmtMoneyAxis(eVMin + eSpan * i / 6));
  PAD.l = leftPadFor(ctx, labels, W);
  const plotW = W - PAD.l - PAD.r, plotH = H - PAD.t - PAD.b;
  const windowFrac = Math.max(1e-6, view.to - view.from);
  eqScale = {
    n: pts.length, plotW, plotH, windowFrac, tMin, tMax,
    eMin: eVMin, eMax: eVMax, eSpan, autoMin, autoMax,
    minPrice: eVMin, maxPrice: eVMax,
    times, values,
    xFor: t => PAD.l + ((t - tMin) / (tMax - tMin) - view.from) / windowFrac * plotW,
    yFor: e => PAD.t + plotH - (e - eVMin) / eSpan * plotH,
    minTime: tMin,
    maxTime: tMax
  };
}

function drawEquityGrid() {
  valueGrid(ctx, {
    yFor: eqScale.yFor, yMin: eqScale.eMin, yMax: eqScale.eMax,
    left: PAD.l, right: W - PAD.r, label: fmtMoneyAxis,
  });
  if (eqScale.n === 0) return;
  const labelCount = 6;
  ctx.textAlign = "center"; ctx.textBaseline = "top";
  let prevX = -100;
  for (let i = 0; i < labelCount; i++) {
    const f = view.from + (i / (labelCount - 1)) * (view.to - view.from);
    if (f < 0 || f > 1) continue;
    const t = eqScale.tMin + f * (eqScale.tMax - eqScale.tMin);
    const x = eqScale.xFor(t);
    if (x - prevX < 62) continue;
    prevX = x;
    ctx.fillText(fmtDay(t), x, H - PAD.b + 6);
  }
}

function drawEquityBaseline() {
  const y = eqScale.yFor(Number(eqData.initialPool));
  dashedHLine(ctx, PAD.l, W - PAD.r, y, "rgba(255,255,255,.22)", [5, 5], 1);
  ctx.fillStyle = "rgba(255,255,255,.5)"; ctx.font = "11px Consolas, monospace";
  ctx.textAlign = "right"; ctx.textBaseline = "top";
  ctx.fillText("initial " + fmtMoneyAxis(eqData.initialPool), W - PAD.r - 8, y - 14);
}

function drawEquityCurve() {
  if (eqScale.n === 0) return;
  const current = Number(eqData.equity);
  const up = current >= Number(eqData.initialPool);
  const color = up ? "#4da3ff" : "#e88f8f";
  ctx.strokeStyle = color; ctx.lineWidth = 2;
  ctx.fillStyle = up ? "rgba(77,163,255,.13)" : "rgba(232,143,143,.13)";
  ctx.beginPath();
  ctx.moveTo(PAD.l, eqScale.yFor(eqScale.eMin));
  for (let i = 0; i < eqScale.n; i++) {
    ctx.lineTo(eqScale.xFor(eqScale.times[i]), eqScale.yFor(eqScale.values[i]));
  }
  ctx.lineTo(W - PAD.r, eqScale.yFor(eqScale.eMin));
  ctx.closePath();
  ctx.save();
  ctx.fill();
  ctx.restore();
  ctx.beginPath();
  for (let i = 0; i < eqScale.n; i++) {
    const x = eqScale.xFor(eqScale.times[i]);
    if (i === 0) ctx.moveTo(x, eqScale.yFor(eqScale.values[i]));
    else ctx.lineTo(x, eqScale.yFor(eqScale.values[i]));
  }
  ctx.stroke();
  ctx.beginPath();
  ctx.arc(eqScale.xFor(eqScale.times[eqScale.n - 1]), eqScale.yFor(eqScale.values[eqScale.n - 1]), 3.5, 0, Math.PI * 2);
  ctx.fillStyle = color; ctx.fill();
}

function buildEquityLegend() {
  const pix = "·";
  const ep = Number(eqData.initialPool) || 0;
  const cur = Number(eqData.equity) || 0;
  const pct = ep !== 0 ? (cur - ep) / ep * 100 : 0;
  const parts = [
    "points: " + (eqData.points || []).length,
    "realized: " + fmtMoney(eqData.realized),
    "open: " + fmtMoney(eqData.unrealized),
    "equity: " + fmtMoneyAxis(eqData.equity) + " (" + fmtPct(pct) + ")"
  ];
  if (eqError) parts.push(eqError);
  document.getElementById("legend").textContent = parts.join("   " + pix + "   ");
}

function showEquityTipAt(mx, my) {
  hideTip(tip);
  if (!eqScale || eqScale.n === 0) return;
  let best = -1, bestDist = Infinity;
  for (let i = 0; i < eqScale.n; i++) {
    const d = Math.abs(eqScale.xFor(eqScale.times[i]) - mx);
    if (d < bestDist) { bestDist = d; best = i; }
  }
  if (best < 0 || bestDist > 36) return;
  const base = Number(eqData.initialPool) || 0;
  const e = eqScale.values[best];
  const delta = e - base;
  const cls = delta >= 0 ? "up" : "down";
  const pct = base !== 0 ? delta / base * 100 : 0;
  showTip(tip, mx, my, "<b>" + fmtDay(eqScale.times[best]) + "</b><br>equity: " + fmtMoneyAxis(e) +
    "<br><span class=\"" + cls + "\">" + (delta >= 0 ? "+" : "") + fmtMoneyAxis(delta) + " RUB (" + fmtPct(pct) + ")</span>");
}

function defaultView(cs) {
  const arr = cs || [];
  const n = arr.length;
  if (n === 0) return { view: { from: 0, to: 1 }, range: null };
  const last = new Date(arr[n - 1].time);
  if (isNaN(last.getTime())) return { view: { from: 0, to: 1 }, range: null };
  const cutoff = new Date(last.getFullYear(), last.getMonth() - 3, last.getDate());
  let i = 0;
  while (i < n - 1 && new Date(arr[i].time).getTime() < cutoff.getTime()) i++;
  const from = i / n;
  const width = 1 - from;
  let lo = Infinity, hi = -Infinity;
  for (let k = i; k < n; k++) {
    if (arr[k].low < lo) lo = arr[k].low;
    if (arr[k].high > hi) hi = arr[k].high;
  }
  if (!isFinite(lo)) lo = 0;
  if (!isFinite(hi)) hi = 1;
  const pad = 0.06 * (hi - lo) || 1;
  return { view: { from, to: from + width * 1.06 }, range: { min: lo - pad, max: hi + pad } };
}

function render() {
  resize();
  ctx.clearRect(0, 0, W, H);
  if (mode === "equity") {
    if (!eqData) {
      document.getElementById("legend").textContent = eqError || "equity: loading…";
      return;
    }
    computeEquityScale();
    drawEquityGrid();
    clipPlot(ctx, PAD.l, PAD.t, eqScale.plotW, eqScale.plotH);
    drawEquityBaseline();
    drawEquityCurve();
    ctx.restore();
    buildEquityLegend();
    return;
  }
  computeScale();
  drawGrid();
  clipPlot(ctx, PAD.l, PAD.t, scale.plotW, scale.plotH);
  drawCandles();
  drawTradeLines();
  drawMarkers();
  drawLevels();
  ctx.restore();
  buildLegend();
}

function showTipAt(mx, my) {
  if (mode !== "price") { showEquityTipAt(mx, my); return; }
  if (!scale || scale.n === 0) { hideTip(tip); return; }
  const fx = (mx - PAD.l) / Math.max(1, scale.plotW);
  const idx = Math.round((view.from + fx * (view.to - view.from)) * scale.n);
  if (idx < 0 || idx >= scale.n) { hideTip(tip); return; }
  const c = scale.cs[idx];
  const lines = ["<b>" + fmtDay(c.time) + "</b>"];
  if (c.isPlaceholder) {
    lines.push("no price data");
  } else {
    lines.push("O " + fmtPrice(c.open) + "   H " + fmtPrice(c.high));
    lines.push("L " + fmtPrice(c.low) + "   C " + fmtPrice(c.close));
  }
  (data.trades || []).forEach(t => {
    if (candleIndexFor(t.entryTime) === idx) {
      lines.push("<span class=\"mk-entry\">▲ " + (t.direction === "Long" ? "LONG" : "SHORT") + " entry @ " + fmtPrice(t.entryPrice) + " " + fmtTime(t.entryTime) + "</span>");
    }
    if (candleIndexFor(t.exitTime) === idx) {
      lines.push("<span class=\"mk-exit\">✕ " + t.direction + " exit @ " + fmtPrice(t.exitPrice) + " (" + esc(t.reason || "") + ", " + fmtPct(t.pnlPercent) + ") " + fmtTime(t.exitTime) + "</span>");
    }
  });
  if (data.openPosition && candleIndexFor(data.openPosition.entryTime) === idx) {
    lines.push("<span class=\"mk-entry\">◉ " + (data.openPosition.direction === "Long" ? "LONG" : "SHORT") + " open @ " + fmtPrice(data.openPosition.entryPrice) + " " + fmtTime(data.openPosition.entryTime) + "</span>");
  }
  const html = lines.join("<br>");
  showTip(tip, mx, my, html);
}

cv.addEventListener("mousemove", e => {
  const rect = cv.getBoundingClientRect();
  const mx = e.clientX - rect.left;
  if (!panning) cv.style.cursor = mx < PAD.l ? "ns-resize" : "";
  if (panning) { hideTip(tip); return; }
  showTipAt(mx, e.clientY - rect.top);
});

cv.addEventListener("mouseleave", () => { hideTip(tip); });

cv.addEventListener("wheel", e => {
  e.preventDefault();
  const rect = cv.getBoundingClientRect();
  const mx = e.clientX - rect.left;
  const my = e.clientY - rect.top;
  const factor = e.deltaY > 0 ? 1.15 : 1 / 1.15;
  hideTip(tip);
  // Scrolling on the value axis (left gutter) or with Shift scales the value
  // range; anywhere else it scales time. Both price and equity behave the same.
  if (mx < PAD.l || e.shiftKey) zoomVertical(my, factor); else zoomHorizontal(mx, factor);
}, { passive: false });

document.getElementById("btnReset").onclick = () => {
  if (!data && !eqData) return;
  view = { from: 0, to: 1 };
  eqView = null;
  if (mode === "equity") { render(); return; }
  const dv = defaultView(data.candles);
  view = dv.view;
  priceView = dv.range;
  render();
};

cv.addEventListener("mousedown", e => {
  if (e.button !== 1) return;
  e.preventDefault();
  panning = { lastX: e.clientX, lastY: e.clientY };
  cv.style.cursor = "grabbing";
  hideTip(tip);
});

window.addEventListener("mousemove", e => {
  if (!panning || !scale) return;
  e.preventDefault();
  const dx = e.clientX - panning.lastX;
  const dy = e.clientY - panning.lastY;
  panning.lastX = e.clientX;
  panning.lastY = e.clientY;
  panBy(dx, dy);
  render();
});

window.addEventListener("mouseup", e => {
  if (e.button === 1 && panning) {
    panning = null;
    cv.style.cursor = "";
  }
});
window.addEventListener("blur", () => {
  if (panning) {
    panning = null;
    cv.style.cursor = "";
  }
});

let gesture = null;
let tap = null;

cv.addEventListener("touchstart", e => {
  if (e.touches.length >= 2) {
    e.preventDefault();
    const clientX = (e.touches[0].clientX + e.touches[1].clientX) / 2;
    const clientY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
    const rect = cv.getBoundingClientRect();
    // Zone and zoom baseline are fixed when the pinch starts: the value axis
    // (left gutter) drives the vertical range, the plot drives time/horizontal.
    // Snapshotting the view lets every move recompute the zoom from the START
    // only - deriving it from each frame's already-zoomed width compounds per
    // frame and feels like acceleration. Both price and equity share this.
    const s = mode === "equity" ? eqScale : scale;
    const zone = clientX - rect.left < PAD.l ? "axis" : "chart";
    gesture = {
      dist: touchDist(e.touches[0], e.touches[1]),
      startMidX: clientX,
      startMidY: clientY,
      zone,
      frac: Math.min(1, Math.max(0, zone === "axis"
        ? (clientY - rect.top - PAD.t) / Math.max(1, s ? s.plotH : 1)
        : (clientX - rect.left - PAD.l) / Math.max(1, s ? s.plotW : 1))),
      min: s ? s.minPrice : 0,
      span: s ? s.maxPrice - s.minPrice : 0,
      autoMin: mode === "equity" ? (s ? s.autoMin : 0) : _autoMin,
      autoMax: mode === "equity" ? (s ? s.autoMax : 1) : _autoMax,
      viewFrom: view.from,
      viewW: view.to - view.from
    };
    tap = null;
    hideTip(tip);
  } else if (e.touches.length === 1 && !gesture) {
    const rect = cv.getBoundingClientRect();
    tap = {
      x: e.touches[0].clientX,
      y: e.touches[0].clientY,
      lastX: e.touches[0].clientX,
      lastY: e.touches[0].clientY,
      zone: e.touches[0].clientX - rect.left < PAD.l ? "axis" : "chart",
      moved: false
    };
  }
}, { passive: false });

cv.addEventListener("touchmove", e => {
  const s = mode === "equity" ? eqScale : scale;
  if (!s || s.n === 0) return;
  if (e.touches.length >= 2 && gesture) {
    e.preventDefault();
    hideTip(tip);
    tap = null;
    const t = e.touches;
    const dist = touchDist(t[0], t[1]);
    // Zoom recomputed from the START snapshot only (frame-count independent ->
    // no acceleration), layered with a two-finger pan that follows the fingers'
    // midpoint on BOTH axes. Everything is absolute-from-start, so nothing can
    // compound frame-to-frame. 0.7 damping = a bit under raw speed; 1 = raw.
    const rel = gesture.dist > 0 ? Math.pow(gesture.dist / dist, 0.7) : 1;
    const midX = (t[0].clientX + t[1].clientX) / 2;
    const midY = (t[0].clientY + t[1].clientY) / 2;
    const panX = midX - gesture.startMidX;
    const panY = midY - gesture.startMidY;
    if (gesture.zone === "axis") {
      const newSpan = Math.max(
        Math.max(1e-9, (gesture.autoMax - gesture.autoMin) * 0.002),
        gesture.span * rel);
      const anchor = gesture.min + gesture.span - gesture.frac * gesture.span;
      const newMax = anchor + gesture.frac * newSpan;
      const shift = (panY / Math.max(1, s.plotH)) * newSpan;
      const range = { min: newMax - newSpan + shift, max: newMax + shift };
      if (mode === "equity") eqView = range; else priceView = range;
    } else {
      const minWidth = mode === "equity"
        ? Math.min(1, (6 * 3600 * 1000) / Math.max(1, s.maxTime - s.minTime))
        : Math.min(1, 2 / s.n);
      const newWidth = Math.min(maxZoomWidth, Math.max(minWidth, gesture.viewW * rel));
      const anchor = gesture.viewFrom + gesture.frac * gesture.viewW;
      const baseFrom = anchor - gesture.frac * newWidth;
      const shift = (panX / Math.max(1, s.plotW)) * newWidth;
      view.from = baseFrom - shift;
      view.to = view.from + newWidth;
      // The two-finger DRAG also pans the value axis in the plot zone (value is
      // not zoomed here, so the span stays the gesture's start span).
      const pShift = (panY / Math.max(1, s.plotH)) * gesture.span;
      const range = { min: gesture.min + pShift, max: gesture.min + gesture.span + pShift };
      if (mode === "equity") eqView = range; else priceView = range;
    }
    render();
  } else if (e.touches.length === 1 && tap && !gesture) {
    const t = e.touches[0];
    if (Math.hypot(t.clientX - tap.x, t.clientY - tap.y) > 8) tap.moved = true;
    if (tap.moved) {
      hideTip(tip);
      // panBy is incremental: feed it the delta since the last move, or the
      // chart accelerates every frame.
      if (tap.zone === "axis") {
        e.preventDefault();
        panBy(0, t.clientY - tap.lastY);
        tap.lastY = t.clientY;
        render();
      } else if (Math.abs(t.clientX - tap.x) > Math.abs(t.clientY - tap.y)) {
        e.preventDefault();
        panBy(t.clientX - tap.lastX, 0);
        tap.lastX = t.clientX;
        render();
      }
    }
  }
}, { passive: false });

cv.addEventListener("touchend", e => {
  const remaining = e.touches.length;
  if (remaining < 2) gesture = null;
  if (remaining === 0) {
    const s = mode === "equity" ? eqScale : scale;
    if (tap && !tap.moved && s && s.n > 0) {
      const rect = cv.getBoundingClientRect();
      showTipAt(tap.x - rect.left, tap.y - rect.top);
    }
    tap = null;
  }
});
cv.addEventListener("touchcancel", () => { gesture = null; tap = null; });

window.addEventListener("resize", () => { if (data) render(); });

load();
setInterval(load, 5000);
