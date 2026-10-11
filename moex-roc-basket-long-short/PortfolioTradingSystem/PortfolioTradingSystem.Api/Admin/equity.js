"use strict";
const { fmtPct, fmtDay, fmtMoneyPlain: fmtMoney } = PTS;
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
let yView = null;
let panning = null;

async function load() {
  try {
    const res = await fetch("/api/equity-data", { credentials: "same-origin" });
    if (res.status === 401) { fail("Authentication required."); return; }
    if (!res.ok) { fail("HTTP " + res.status + ": " + (await res.text())); return; }
    data = await res.json();
    document.getElementById("title").textContent = "Total equity";
    render();
  } catch (err) {
    fail("Failed to load equity data: " + err.message);
  }
}

function fail(msg) {
  document.getElementById("title").textContent = "Total equity — unavailable";
  const box = document.getElementById("errorBox");
  box.textContent = msg;
  box.style.display = "block";
}

function resize() {
  const d = fitCanvas(cv, ctx);
  W = d.w; H = d.h;
}

function computeScale() {
  const pts = data.points || [];
  const n = pts.length;
  if (n === 0) { scale = null; return; }
  let tMin = Infinity, tMax = -Infinity, eMin = Infinity, eMax = -Infinity;
  pts.forEach(p => {
    const t = new Date(p.time).getTime();
    if (isNaN(t)) return;
    if (t < tMin) tMin = t;
    if (t > tMax) tMax = t;
    const e = Number(p.equity);
    if (e < eMin) eMin = e;
    if (e > eMax) eMax = e;
  });
  if (tMax === tMin) { tMin -= 86400000; tMax += 86400000; }
  const tPad = (tMax - tMin) * 0.03;
  tMin -= tPad; tMax += tPad;
  eMin = isFinite(eMin) ? eMin : Number(data.initialPool);
  eMax = isFinite(eMax) ? eMax : Number(data.equity);
  const ePad = (eMax - eMin) * 0.06 || 1;
  const autoMin = eMin - ePad, autoMax = eMax + ePad;
  const yMin = yView ? yView.min : autoMin;
  const yMax = yView ? yView.max : autoMax;
  const eSpan = Math.max(1e-9, yMax - yMin);
  const labels = [];
  for (let i = 0; i <= 6; i++) labels.push(fmtMoney(yMin + eSpan * i / 6));
  PAD.l = leftPadFor(ctx, labels, W);
  const plotW = W - PAD.l - PAD.r, plotH = H - PAD.t - PAD.b;
  const windowFrac = Math.max(1e-6, view.to - view.from);
  scale = {
    n, plotW, plotH, windowFrac, tMin, tMax, yMin, yMax, eSpan, autoMin, autoMax,
    minPrice: yMin, maxPrice: yMax, minTime: tMin, maxTime: tMax,
    xFor: t => PAD.l + ((t - tMin) / (tMax - tMin) - view.from) / windowFrac * plotW,
    yFor: e => PAD.t + plotH - (e - yMin) / eSpan * plotH,
  };
}

function drawGrid() {
  valueGrid(ctx, {
    yFor: scale.yFor, yMin: scale.yMin, yMax: scale.yMax,
    left: PAD.l, right: W - PAD.r, label: fmtMoney,
  });
  const labelCount = 6;
  ctx.textAlign = "center"; ctx.textBaseline = "top";
  let prevX = -100;
  for (let i = 0; i < labelCount; i++) {
    const f = view.from + (i / (labelCount - 1)) * (view.to - view.from);
    if (f < 0 || f > 1) continue;
    const t = scale.tMin + f * (scale.tMax - scale.tMin);
    const x = scale.xFor(t);
    if (x - prevX < 70) continue;
    prevX = x;
    ctx.fillText(fmtDay(t), x, H - PAD.b + 6);
  }
}

function drawBaseline() {
  dashedHLine(ctx, PAD.l, W - PAD.r, scale.yFor(Number(data.initialPool)),
    "rgba(255,255,255,.22)", [5, 5], 1);
}

function drawCurve() {
  const pts = (data.points || []).filter(p => {
    const t = new Date(p.time).getTime();
    return !isNaN(t) && isFinite(Number(p.equity));
  });
  if (pts.length === 0) return;
  const path = [];
  pts.forEach(p => {
    const x = scale.xFor(new Date(p.time).getTime());
    const y = scale.yFor(Number(p.equity));
    path.push([x, y]);
  });
  const current = Number(data.equity);
  const up = current >= Number(data.initialPool);
  const color = up ? "#4da3ff" : "#e88f8f";
  ctx.beginPath();
  ctx.moveTo(path[0][0], path[0][1]);
  for (let i = 1; i < path.length; i++) ctx.lineTo(path[i][0], path[i][1]);
  ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.stroke();
  ctx.save();
  ctx.beginPath();
  ctx.moveTo(path[0][0], scale.yFor(scale.yMin));
  for (let i = 0; i < path.length; i++) ctx.lineTo(path[i][0], path[i][1]);
  ctx.lineTo(path[path.length - 1][0], scale.yFor(scale.yMin));
  ctx.closePath();
  ctx.fillStyle = up ? "rgba(77,163,255,.14)" : "rgba(232,143,143,.14)";
  ctx.fill();
  ctx.restore();
  ctx.beginPath();
  ctx.arc(path[path.length - 1][0], path[path.length - 1][1], 3.5, 0, Math.PI * 2);
  ctx.fillStyle = color; ctx.fill();
}

function buildLegend() {
  const pix = "·";
  const parts = [
    "points: " + (data.points || []).length,
    "realized: " + fmtMoney(data.realized),
    "open: " + fmtMoney(data.unrealized),
    "equity: " + fmtMoney(data.equity)
  ];
  document.getElementById("legend").textContent = parts.join("   " + pix + "   ");
}

function render() {
  resize();
  ctx.clearRect(0, 0, W, H);
  computeScale();
  if (!scale) { buildLegend(); return; }
  drawGrid();
  clipPlot(ctx, PAD.l, PAD.t, scale.plotW, scale.plotH);
  drawBaseline();
  drawCurve();
  ctx.restore();
  buildLegend();
}

function showTipAt(mx, my) {
  if (!scale) { hideTip(tip); return; }
  let best = null, bestDist = Infinity;
  (data.points || []).forEach(p => {
    const t = new Date(p.time).getTime();
    if (isNaN(t) || !isFinite(Number(p.equity))) return;
    const d = Math.abs(scale.xFor(t) - mx);
    if (d < bestDist) { bestDist = d; best = p; }
  });
  if (!best || bestDist > 36) { hideTip(tip); return; }
  const t = new Date(best.time).getTime();
  const e = Number(best.equity);
  const base = Number(data.initialPool);
  const delta = e - base;
  const cls = delta >= 0 ? "up" : "down";
  const pct = base !== 0 ? delta / base * 100 : 0;
  showTip(tip, mx, my, "<b>" + fmtDay(t) + "</b><br>equity: " + fmtMoney(e) +
    "<br><span class=\"" + cls + "\">" + (delta >= 0 ? "+" : "") + fmtMoney(delta) + " RUB (" + fmtPct(pct) + ")</span>");
}

function applyHorizontal(mx, factor) {
  if (!scale) return;
  const minWidth = Math.min(1, (6 * 3600 * 1000) / Math.max(1, scale.tMax - scale.tMin));
  zoomWindow(view, mx, PAD.l, scale.plotW, factor, minWidth);
}

function zoomHorizontal(mx, factor) {
  applyHorizontal(mx, factor);
  render();
}

function applyVertical(my, factor) {
  if (!scale) return;
  const minSpan = Math.max(1e-9, (scale.autoMax - scale.autoMin) * 0.002);
  const fy = Math.min(1, Math.max(0, (my - PAD.t) / Math.max(1, scale.plotH)));
  const curSpan = scale.maxPrice - scale.minPrice;
  const newSpan = Math.max(minSpan, curSpan * factor);
  const anchor = scale.maxPrice - fy * curSpan;
  const newMax = anchor + fy * newSpan;
  yView = { min: newMax - newSpan, max: newMax };
}

function zoomVertical(my, factor) {
  applyVertical(my, factor);
  render();
}

function panBy(dx, dy) {
  if (!scale) return;
  if (dx !== 0) panWindow(view, dx, scale.plotW);
  if (dy !== 0) {
    const span = scale.maxPrice - scale.minPrice;
    const shift = (dy / Math.max(1, scale.plotH)) * span;
    yView = { min: scale.minPrice + shift, max: scale.maxPrice + shift };
  }
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
  // range; anywhere else it scales time - same as the price chart.
  if (mx < PAD.l || e.shiftKey) zoomVertical(my, factor); else zoomHorizontal(mx, factor);
}, { passive: false });

document.getElementById("btnReset").onclick = () => {
  view = { from: 0, to: 1 };
  yView = null;
  if (data) render();
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
    // frame and feels like acceleration. Same model as the price chart.
    const s = scale;
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
      autoMin: s ? s.autoMin : 0,
      autoMax: s ? s.autoMax : 1,
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
  const s = scale;
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
      yView = { min: newMax - newSpan + shift, max: newMax + shift };
    } else {
      const minWidth = Math.min(1, (6 * 3600 * 1000) / Math.max(1, s.maxTime - s.minTime));
      const newWidth = Math.min(maxZoomWidth, Math.max(minWidth, gesture.viewW * rel));
      const anchor = gesture.viewFrom + gesture.frac * gesture.viewW;
      const baseFrom = anchor - gesture.frac * newWidth;
      const shift = (panX / Math.max(1, s.plotW)) * newWidth;
      view.from = baseFrom - shift;
      view.to = view.from + newWidth;
      // The two-finger DRAG also pans the value axis in the plot zone (value is
      // not zoomed here, so the span stays the gesture's start span).
      const pShift = (panY / Math.max(1, s.plotH)) * gesture.span;
      yView = { min: gesture.min + pShift, max: gesture.min + gesture.span + pShift };
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
    if (tap && !tap.moved && scale) {
      const rect = cv.getBoundingClientRect();
      showTipAt(tap.x - rect.left, tap.y - rect.top);
    }
    tap = null;
  }
});
cv.addEventListener("touchcancel", () => { gesture = null; tap = null; });

window.addEventListener("resize", () => { if (data) render(); });

load();
