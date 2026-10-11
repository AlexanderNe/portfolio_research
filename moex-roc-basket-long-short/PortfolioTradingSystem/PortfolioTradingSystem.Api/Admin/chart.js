"use strict";
// Low-level canvas primitives shared by details.js and equity.js.
// No layout decisions live here: callers pass their own scales, styles and
// labels, so each page keeps its exact rendering behaviour.
(function () {
  // Maximum horizontal window width: zoom-out is capped past the padded data
  // range so the curve still occupies a reasonable share of the plot.
  const MAX_ZOOM_WIDTH = 1.6;

  // Fit the canvas to its parent and the viewport, applying devicePixelRatio.
  // Returns the CSS-pixel size the caller should use for layout math.
  function fitCanvas(cv, ctx) {
    const rect = cv.parentElement.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const w = Math.max(300, Math.floor(rect.width));
    const h = Math.max(340, Math.min(560, Math.floor((window.innerHeight || 900) * 0.65)));
    cv.style.width = w + "px";
    cv.style.height = h + "px";
    cv.width = Math.floor(w * dpr);
    cv.height = Math.floor(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { w, h };
  }

  function touchDist(a, b) {
    return Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
  }

  function hideTip(tip) {
    tip.style.display = "none";
  }

  function showTip(tip, mx, my, html) {
    tip.innerHTML = html;
    tip.style.display = "block";
    tip.style.left = (mx + 14) + "px";
    tip.style.top = (my + 14) + "px";
  }

  // Zoom a normalized [from, to] window horizontally around mx. Mutates + returns.
  function zoomWindow(view, mx, left, plotW, factor, minWidth) {
    const w = Math.max(1, plotW);
    const fx = Math.min(1, Math.max(0, (mx - left) / w));
    const curWidth = Math.max(1e-6, view.to - view.from);
    const newWidth = Math.min(MAX_ZOOM_WIDTH, Math.max(minWidth, curWidth * factor));
    const anchor = view.from + fx * curWidth;
    view.from = anchor - fx * newWidth;
    view.to = view.from + newWidth;
    return view;
  }

  // Pan a normalized [from, to] window by dx pixels. Mutates + returns.
  function panWindow(view, dx, plotW) {
    if (dx === 0) return view;
    const windowFrac = Math.max(1e-6, view.to - view.from);
    const shift = (dx / Math.max(1, plotW)) * windowFrac;
    view.from -= shift;
    view.to -= shift;
    return view;
  }

  // Left gutter width sized to the widest y-axis label (min 64px, max 40% of W).
  function leftPadFor(ctx, labels, W) {
    ctx.font = "11px Consolas, monospace";
    let labelW = 64;
    for (let i = 0; i < labels.length; i++) {
      labelW = Math.max(labelW, Math.ceil(ctx.measureText(labels[i]).width) + 12);
    }
    return Math.min(labelW, Math.floor(W * 0.4));
  }

  // Seven horizontal grid lines with right-aligned value labels.
  function valueGrid(ctx, opts) {
    const { yFor, yMin, yMax, left, right, label } = opts;
    ctx.strokeStyle = "#232c3b"; ctx.lineWidth = 1;
    ctx.fillStyle = "#8fa3bf"; ctx.font = "11px Consolas, monospace";
    ctx.textAlign = "right"; ctx.textBaseline = "middle";
    for (let i = 0; i <= 6; i++) {
      const v = yMin + (yMax - yMin) * i / 6;
      const y = yFor(v);
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
      ctx.fillText(label(v), left - 6, y);
    }
  }

  function dashedHLine(ctx, left, right, y, color, dash, lineWidth) {
    ctx.strokeStyle = color; ctx.lineWidth = lineWidth || 1;
    ctx.setLineDash(dash || [5, 5]);
    ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(right, y); ctx.stroke();
    ctx.setLineDash([]);
  }

  // Clip subsequent drawing to the plot rectangle. Caller must ctx.restore().
  function clipPlot(ctx, left, top, width, height) {
    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top, width, height);
    ctx.clip();
  }

  window.Charting = {
    maxZoomWidth: MAX_ZOOM_WIDTH,
    fitCanvas, touchDist, hideTip, showTip,
    zoomWindow, panWindow, leftPadFor, valueGrid, dashedHLine, clipPlot,
  };
})();
