"use strict";
const { esc, fmtNum, fmtPct, fmtMoney, fmtTime, badge, signalTypeBadge, humanizeEnum } = PTS;

let instruments = [];
let metricByTicker = {};
let expandedTicker = null;
let instFilter = "";
let simpleView = (function () { try { return localStorage.getItem("ptsInstSimpleView") === "1"; } catch (e) { return false; } })();
let editState = null;
let currentView = "instruments";
let sigState = { ticker: "", page: 1, pageSize: 50, total: 0, totalPages: 1, hasRows: false };
const signalsHtml = {};

async function api(path, opts) {
  try {
    return await PTS.api(path, opts);
  } catch (e) {
    if (e.status === 401) {
      showError("Authentication required. Please reload the page and log in with the admin credentials.");
    }
    throw e;
  }
}

function pnlHtml(v, pct) {
  if (v === null || v === undefined || isNaN(v)) return "";
  const color = v >= 0 ? "#7fd18a" : "#e88f8f";
  return '<span style="color:' + color + '">' + esc(fmtMoney(v)) + " RUB (" + esc(fmtPct(pct)) + ")</span>";
}
function stateBadges(instr) {
  const procCls = instr.processingStatus === "Running" ? "green" : "orange";
  return badge(instr.processingStatus, procCls);
}
function engineBadge(m) {
  if (!m) return badge("no engine", "gray");
  if (m.status === "error") return badge("error", "red");
  if (m.status === "warming up") return badge("warming up", "orange");
  if (m.status === "running") return badge("running", "green");
  return badge(m.status || "stopped", "gray");
}
function showError(msg) {
  const box = document.getElementById("errorBox");
  box.textContent = msg;
  box.style.display = "block";
  setTimeout(() => { box.style.display = "none"; }, 8000);
}
function toast(msg) {
  const t = document.getElementById("toast");
  t.textContent = msg;
  t.style.opacity = 1;
  setTimeout(() => { t.style.opacity = 0; }, 3000);
}

async function refresh() {
  try {
    const [summary, views] = await Promise.all([api("/api/summary"), api("/api/instruments")]);
    document.getElementById("globalState").textContent = "state: " + summary.globalState;
    document.getElementById("engineCount").textContent = "engines: " + summary.engines;
    document.getElementById("counts").textContent = "instruments: " + summary.instruments + " (running: " + summary.running + ")";

    function setPnlCard(id, v) {
      const el = document.getElementById(id);
      if (!el) return;
      el.classList.remove("up", "down");
      if (v !== null && v !== undefined && !isNaN(v)) el.classList.add(v >= 0 ? "up" : "down");
    }
    function setPnlSub(v) {
      [totalUnrlEl, totalUnrlPctEl].forEach(el => {
        el.classList.remove("up", "down");
        if (v !== null && v !== undefined && !isNaN(v)) el.classList.add(v >= 0 ? "up" : "down");
      });
    }

    const totalEl = document.getElementById("pnlTotal");
    const pctEl = document.getElementById("pnlTotalPct");
    const totalUnrlEl = document.getElementById("pnlTotalUnrl");
    const totalUnrlPctEl = document.getElementById("pnlTotalUnrlPct");
    const longsEl = document.getElementById("pnlLongs");
    const shortsEl = document.getElementById("pnlShorts");
    const longsPctEl = document.getElementById("pnlLongsPct");
    const shortsPctEl = document.getElementById("pnlShortsPct");
    totalEl.textContent = fmtMoney(summary.totalPnl) + " RUB";
    pctEl.textContent = fmtPct(summary.totalPnlPct);
    totalUnrlEl.textContent = fmtMoney(summary.unrealizedPnl) + " RUB";
    totalUnrlPctEl.textContent = fmtPct(summary.unrealizedPnlPct);
    setPnlCard("pnlCardTotal", summary.totalPnl);
    setPnlSub(summary.unrealizedPnl);
    longsEl.textContent = fmtMoney(summary.longsPnl) + " RUB";
    shortsEl.textContent = fmtMoney(summary.shortsPnl) + " RUB";
    longsPctEl.textContent = fmtPct(summary.longsPnlPct);
    shortsPctEl.textContent = fmtPct(summary.shortsPnlPct);
    setPnlCard("pnlCardLongs", summary.longsPnl);
    setPnlCard("pnlCardShorts", summary.shortsPnl);

    instruments = views.map(v => Object.assign({}, v.instrument, { metrics: v.metrics || null }));
    metricByTicker = {};
    instruments.forEach(i => { if (i.metrics) metricByTicker[i.ticker] = i.metrics; });
    document.getElementById("instTickerOptions").innerHTML =
      [...new Set(instruments.map(i => i.ticker))].map(t => '<option value="' + esc(t) + '"></option>').join("");
    if (!editState) renderRows();
  } catch (e) {
    if (e.message !== "401") showError("Refresh failed: " + e.message);
  }
}

function renderSimpleRows(list) {
  return list.map(instr => {
    const m = instr.metrics;
    const pos = m && m.positionState === "long" ? badge("LONG", "green")
      : m && m.positionState === "short" ? badge("SHORT", "red")
      : badge(m ? "flat" : "—", "gray");
    const pnlPct = m && m.positionPnlPercent !== null && m.positionPnlPercent !== undefined
      ? '<span style="color:' + (m.positionPnlPercent >= 0 ? "#7fd18a" : "#e88f8f") + '">' + esc(fmtPct(m.positionPnlPercent)) + '</span>'
      : "—";
    return '<tr onclick="window.open(\'/details?id=' + instr.id + '\', \'_blank\')">' +
      '<td data-label="Ticker"><b>' + esc(instr.ticker) + '</b></td>' +
      '<td data-label="Position">' + pos + '</td>' +
      '<td data-label="PnL" class="mono">' + pnlPct + '</td>' +
      '</tr>';
  }).join("");
}

function renderRows() {
  const tbody = document.getElementById("rows");
  if (!instruments.length) {
    tbody.innerHTML = '<tr><td colspan="9" class="empty">No instruments.</td></tr>';
    return;
  }
  const list = instFilter
    ? instruments.filter(i => (i.ticker || "").toUpperCase().includes(instFilter))
    : instruments;
  if (!list.length) {
    tbody.innerHTML = '<tr><td colspan="9" class="empty">No instruments matching "' + esc(instFilter) + '".</td></tr>';
    return;
  }
  const cntEl = document.getElementById("instCount");
  if (cntEl) cntEl.textContent = list.length === instruments.length ? "" : list.length + " of " + instruments.length;
  const head = document.getElementById("rowsHead");
  if (head) head.innerHTML = simpleView
    ? '<tr><th>Ticker</th><th>Position</th><th>PnL</th></tr>'
    : '<tr><th>Ticker</th><th>Name</th><th>Status</th><th>FIGI / ISIN / Lot / Future</th>' +
      '<th>Engine</th><th>Last candle</th><th>ROC / ATR</th><th>Position</th><th>Actions</th></tr>';
  const tableEl = document.getElementById("instTable");
  if (tableEl) tableEl.className = simpleView ? "simple" : "";
  if (simpleView) {
    tbody.innerHTML = renderSimpleRows(list);
    return;
  }
  tbody.innerHTML = list.map(instr => {
    const m = instr.metrics;
    const mBadge = engineBadge(m);
    const lastCandle = m ? fmtTime(m.lastCandleTime) + " @ " + fmtNum(m.lastCandleClose, 4) : "—";
    const rocAtr = m ? fmtNum(m.roc, 4) + " / " + fmtNum(m.atr, 4) : "—";
    const posState = m ? (m.positionState === "long" ? badge("LONG", "green")
                       : m.positionState === "short" ? badge("SHORT", "red")
                       : badge("flat", "gray")) : badge("—", "gray");
    const posSince = m && m.positionSince ? " since " + fmtTime(m.positionSince) : "";
    const posPnl = m ? pnlHtml(m.positionPnl, m.positionPnlPercent) : "";
    const hasErr = m && m.lastError ? ' title="' + esc(m.lastError) + '"' : "";
    const future = instr.futureTicker ? instr.futureTicker + (instr.futureFigi ? " (" + instr.futureFigi + ")" : "") : "";
    const isOpen = expandedTicker === instr.ticker;
    return '<tr onclick="actions.expand(\'' + instr.id + '\')">' +
      '<td data-label="Ticker"><b>' + esc(instr.ticker) + '</b></td>' +
      '<td data-label="Name">' + esc(instr.name || "") + '</td>' +
      '<td data-label="Status">' + stateBadges(instr) + '</td>' +
      '<td data-label="FIGI / ISIN / Lot / Future" class="mono">' + esc(instr.figi || "") + ' / ' + esc(instr.isin || "") + ' / ' + instr.lotSize +
        (future ? ' / ' + esc(future) : '') + '</td>' +
      '<td data-label="Engine"' + hasErr + '>' + mBadge + (m && m.warmupDone ? ' · warm' : '') + '</td>' +
      '<td data-label="Last candle" class="mono">' + lastCandle + '</td>' +
      '<td data-label="ROC / ATR" class="mono">' + rocAtr + '</td>' +
      '<td data-label="Position">' + posState + posPnl + posSince + (m && m.pendingSignal !== 0 && m.pendingSignal ? " · signal " + (m.pendingSignal > 0 ? "L" : "S") : "") + '</td>' +
      '<td data-label="Actions" class="row-actions">' +
        '<button class="btn" title="Open instrument details (chart + PnL + signals) in a new tab" onclick="event.stopPropagation(); window.open(\'/details?id=' + instr.id + '\', \'_blank\')">Chart</button>' +
      '</td></tr>' +
      (isOpen ? '<tr id="detail-' + instr.ticker + '"><td colspan="9"><div class="detail open" id="detailBox-' + instr.ticker + '">' +
        '<div class="row-actions" style="margin-bottom:10px">' +
        '<button class="btn" onclick="actions.pause(\'' + instr.id + '\', this)">' + (instr.processingStatus === "Running" ? "Pause" : "Resume") + '</button>' +
        '<button class="btn" onclick="actions.resolve(\'' + instr.id + '\')">Resolve</button>' +
        '<button class="btn" onclick="actions.edit(\'' + instr.id + '\')">' + (editState && editState.ticker === instr.ticker ? "Cancel edit" : "Edit") + '</button>' +
        '<button class="btn danger" onclick="actions.del(\'' + instr.id + '\', \'' + esc(instr.ticker) + '\')">Delete</button>' +
        '<button class="btn" onclick="actions.expand(\'' + instr.id + '\')">Collapse</button>' +
        '</div>' +
        (editState && editState.ticker === instr.ticker ? editFormHtml(instr) : "") +
        '<div class="grid2">' +
        kv("Engine status", m ? m.status : "no engine") +
        kv("Last run time", fmtTime(m && m.lastRunTime)) +
        kv("Last error", m && m.lastError || "") +
        kv("Warm-up", m ? (m.warmupDone ? "done (" + m.warmupBars + " bars)" : "in progress / not done") : "—") +
        kv("Last processed bar", fmtTime(m && m.lastProcessedBarTime)) +
        kv("Bars processed", m ? m.barsProcessed : "—") +
        kv("Latest close", fmtNum(m && m.latestClose, 4)) +
        kv("ROC (" + "n=5)", fmtNum(m && m.roc, 4)) +
        kv("ATR (" + "14)", fmtNum(m && m.atr, 4)) +
        kv("Cash", fmtNum(m && m.cash)) +
        kv("Equity", fmtNum(m && m.equity)) +
        kv("Open PnL", m && m.positionPnl !== null && m.positionPnl !== undefined ? fmtMoney(m.positionPnl) + " RUB (" + fmtPct(m.positionPnlPercent) + ")" : "—") +
        kv("Position", m ? m.positionState : "—") +
        kv("Pending signal", m && m.pendingSignal ? (m.pendingSignal > 0 ? "LONG" : "SHORT") : "none") +
        kv("UID", instr.uid || "—") +
        kv("FIGI", instr.figi || "—") +
        '</div>' +
        '<div class="signals" id="sig-' + instr.ticker + '">' + (signalsHtml[instr.ticker] || '<span class="empty">Loading signals…</span>') + '</div>' +
        '</div></td></tr>' : '');
  }).join("");
}

function draft(field, value) {
  if (editState) editState.values[field] = value;
}

function editFormHtml(instr) {
  const v = editState && editState.ticker === instr.ticker ? editState.values : {};
  const valueOf = f => esc(v[f] !== undefined && v[f] !== null ? v[f] : (instr[f] === null || instr[f] === undefined ? "" : instr[f]));
  const input = (label, f, opts) =>
    `<label>${label} <input data-f="${f}"${opts || ""} value="${valueOf(f)}" oninput="draft('${f}', this.value)"></label>`;
  const processing = v.processingStatus !== undefined ? v.processingStatus : instr.processingStatus;
  return `<form class="instrument-form" style="margin-top:10px">
    <label>Ticker <input value="${esc(instr.ticker)}" disabled></label>
    ${input("Name", "name")}
    ${input("ISIN", "isin")}
    ${input("FIGI", "figi")}
    ${input("UID", "uid")}
    ${input("Class code", "classCode")}
    ${input("Lot size", "lotSize", ' type="number" min="1"')}
    ${input("Future ticker", "futureTicker")}
    ${input("Future FIGI", "futureFigi")}
    ${input("Future class code", "futureClassCode")}
    <label>Processing status <select data-f="processingStatus" oninput="draft('processingStatus', this.value)">
      ${statusOptions("paused", processing)}${statusOptions("running", processing)}</select></label>
    <div class="row-actions">
      <button class="btn ok" type="button" onclick="actions.saveEdit('${instr.id}')">Save</button>
      <button class="btn" type="button" onclick="actions.cancelEdit()">Cancel</button>
    </div>
  </form>`;
}

function kv(k, v) {
  return '<div class="kv"><b>' + esc(k) + '</b><span>' + esc(v) + "</span></div>";
}

const actions = {
  expand(id) {
    const instr = instruments.find(i => i.id === id);
    if (!instr) return;
    const wasOpen = expandedTicker === instr.ticker;
    expandedTicker = wasOpen ? null : instr.ticker;
    renderRows();
    if (!wasOpen) loadSignals(id, instr.ticker);
  },
  async resolve(id) {
    const instr = instruments.find(i => i.id === id);
    if (!instr) return;
    try {
      await api("/api/instruments/" + id + "/resolve", { method: "POST" });
      toast("Resolved " + instr.ticker + " (UID/FIGI set)");
      await refresh();
    } catch (err) {
      if (err.status === 409) {
        const list = (err.candidates || []).map(c =>
          c.ticker + " (" + c.classCode + ") " + (c.name || "") + " — figi " + c.figi + (c.apiTradeAvailable ? "" : " (not tradeable)"));
        showError("Ambiguous ticker " + instr.ticker + ". Candidates:\n" + list.join("\n"));
      } else {
        showError("Resolve failed: " + (err.message || err.status));
      }
    }
  },
  async pause(id, btn) {
    const instr = instruments.find(i => i.id === id);
    const action = instr && instr.processingStatus === "Running" ? "pause" : "resume";
    await dangerousButton(btn, async () => {
      await api("/api/instruments/" + id + "/" + action, { method: "POST" });
      toast((action === "pause" ? "Paused " : "Resumed ") + instr.ticker);
      await refresh();
    });
  },
  edit(id) {
    const instr = instruments.find(i => i.id === id);
    if (!instr) return;
    if (editState && editState.ticker === instr.ticker) { actions.cancelEdit(); return; }
    const s = lower(instr);
    editState = {
      id: id,
      ticker: instr.ticker,
      values: {
        name: instr.name || "", isin: instr.isin || "", figi: instr.figi || "", uid: instr.uid || "",
        classCode: instr.classCode || "",
        lotSize: String(instr.lotSize), futureTicker: instr.futureTicker || "", futureFigi: instr.futureFigi || "",
        futureClassCode: instr.futureClassCode || "", processingStatus: s.processingStatus,
      },
    };
    expandedTicker = instr.ticker;
    renderRows();
    loadSignals(id, instr.ticker);
  },
  async saveEdit(id) {
    if (!editState) return;
    const v = editState.values;
    const payload = {
      ticker: editState.ticker,
      name: v.name || "", isin: v.isin || "", figi: v.figi || "", uid: v.uid || "", classCode: v.classCode || "",
      lotSize: parseInt(v.lotSize, 10) || 1,
      futureTicker: v.futureTicker || "", futureFigi: v.futureFigi || "", futureClassCode: v.futureClassCode || "",
      processingStatus: v.processingStatus,
    };
    try {
      await api("/api/instruments/" + editState.id, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      toast("Saved " + editState.ticker);
      editState = null;
      await refresh();
    } catch (err) { showError("Update failed: " + err.message); }
  },
  cancelEdit() {
    if (!editState) return;
    editState = null;
    renderRows();
  },
  async del(id, ticker) {
    if (!confirm("Delete instrument " + ticker + " from the system?")) return;
    try {
      await api("/api/instruments/" + id, { method: "DELETE" });
      toast("Deleted " + ticker); await refresh();
    } catch (err) { showError("Delete failed: " + err.message); }
  },
};

async function dangerousButton(btn, fn) {
  btn.disabled = true;
  try { await fn(); } catch (err) { showError(err.message); }
  finally { btn.disabled = false; }
}

const actionsDropdown = document.getElementById("actionsDropdown");
const actionsBtn = document.getElementById("actionsBtn");

function toggleActions(e) {
  if (e) e.stopPropagation();
  const open = actionsDropdown.classList.toggle("open");
  actionsBtn.setAttribute("aria-expanded", open ? "true" : "false");
}

function closeActions() {
  actionsDropdown.classList.remove("open");
  actionsBtn.setAttribute("aria-expanded", "false");
}

document.addEventListener("click", e => {
  if (!actionsDropdown.contains(e.target)) closeActions();
});
document.addEventListener("keydown", e => {
  if (e.key === "Escape") closeActions();
});

async function control(action, btn) {
  closeActions();
  if (action === "stop" && !confirm("Stop ALL instrument engines? Running instruments will be paused.")) return;
  await dangerousButton(btn, async () => {
    await api("/api/control/" + action, { method: "POST" });
    toast("Command '" + action + "' sent");
    await refresh();
  });
}

async function resolveAll(btn) {
  closeActions();
  await dangerousButton(btn, async () => {
    try {
      const res = await api("/api/instruments/resolve-all", { method: "POST" });
      const fails = res.failed || [];
      if (fails.length) {
        showError("Resolve all: " + res.resolved + "/" + res.pending + " resolved.\n" +
          fails.map(f => f.ticker + ": " + f.error + (f.message ? " — " + f.message : "")).join("\n"));
      } else {
        toast("Resolve all: " + res.resolved + "/" + res.pending + " resolved");
      }
      await refresh();
    } catch (err) { showError("Resolve all failed: " + err.message); }
  });
}

function statusOptions(text, current) {
  return '<option value="' + text + '"' + (String(current).toLowerCase() === text ? " selected" : "") + ">" + text + "</option>";
}
function lower(instr) {
  return {
    processingStatus: String(instr.processingStatus || "Paused").toLowerCase(),
  };
}

async function loadSignals(id, ticker) {
  if (signalsHtml[ticker]) return;
  const box = document.getElementById("sig-" + ticker);
  if (box) box.innerHTML = '<span class="empty">Loading…</span>';
  try {
    const list = await api("/api/instruments/" + id + "/signals?limit=50");
    signalsHtml[ticker] = !list.length
      ? '<span class="empty">No signals yet.</span>'
      : '<table><thead><tr><th>Time</th><th>Type</th><th>Dir</th><th>Price</th>' +
        '<th>SL</th><th>TP</th><th>Exit</th><th>Exit reason</th><th>PnL</th></tr></thead><tbody>' +
        list.map(s => '<tr>' +
          '<td data-label="Time" class="mono">' + fmtTime(s.timestamp) + '</td>' +
          '<td data-label="Type">' + esc(s.type) + '</td>' +
          '<td data-label="Dir">' + (s.direction === "Long" ? badge("LONG", "green") : s.direction === "Short" ? badge("SHORT", "red") : "—") + '</td>' +
          '<td data-label="Price" class="mono num">' + fmtNum(s.price, 4) + '</td>' +
          '<td data-label="SL" class="mono num">' + fmtNum(s.stopLoss, 4) + '</td>' +
          '<td data-label="TP" class="mono num">' + fmtNum(s.takeProfit, 4) + '</td>' +
          '<td data-label="Exit" class="mono num">' + fmtNum(s.exitPrice, 4) + '</td>' +
          '<td data-label="Exit reason">' + (s.exitReason ? esc(s.exitReason) : "—") + '</td>' +
          '<td data-label="PnL" class="mono num">' + fmtPct(s.pnlPercent) + '</td>' +
        '</tr>').join("") + '</tbody></table>';
    const b2 = document.getElementById("sig-" + ticker);
    if (b2) b2.innerHTML = signalsHtml[ticker];
  } catch (err) {
    signalsHtml[ticker] = '<span class="empty">Signals failed: ' + esc(err.message) + "</span>";
    const b3 = document.getElementById("sig-" + ticker);
    if (b3) b3.innerHTML = signalsHtml[ticker];
  }
}

function showView(name) {
  currentView = name;
  document.getElementById("view-instruments").hidden = name !== "instruments";
  document.getElementById("view-signals").hidden = name !== "signals";
  document.querySelectorAll(".tab").forEach(el =>
    el.classList.toggle("active", el.dataset.view === name));
  if (name === "signals") {
    if (!document.getElementById("tickerOptions").children.length) loadTickerOptions();
    loadSignalsPage();
  }
}

async function loadTickerOptions() {
  try {
    const list = await api("/api/signals/tickers");
    document.getElementById("tickerOptions").innerHTML =
      list.map(t => '<option value="' + esc(t) + '"></option>').join("");
  } catch (err) { /* options are optional */ }
}

function sigPage(delta) {
  const target = sigState.page + delta;
  if (target < 1 || target > sigState.totalPages) return;
  sigState.page = target;
  loadSignalsPage();
}

function applyTickerFilter() {
  const val = document.getElementById("sigTicker").value.trim().toUpperCase();
  sigState.ticker = val;
  sigState.page = 1;
  loadSignalsPage();
}

function clearTickerFilter() {
  document.getElementById("sigTicker").value = "";
  sigState.ticker = "";
  sigState.page = 1;
  loadSignalsPage();
}

async function resendSignal(id) {
  try {
    await api("/api/signals/" + id + "/resend", { method: "POST" });
    toast("Signal resent to Telegram with a duplicate remark");
  } catch (err) { showError("Resend failed: " + err.message); }
}

async function loadSignalsPage() {
  const box = document.getElementById("sigRows");
  if (currentView !== "signals") return;
  if (!sigState.hasRows) box.innerHTML = '<tr><td colspan="11" class="empty">Loading…</td></tr>';
  let params = "page=" + sigState.page + "&pageSize=" + sigState.pageSize;
  if (sigState.ticker) params += "&ticker=" + encodeURIComponent(sigState.ticker);
  try {
    const res = await api("/api/signals?" + params);
    sigState.total = res.total || 0;
    sigState.totalPages = Math.max(1, Math.ceil(sigState.total / sigState.pageSize));
    if (sigState.page > sigState.totalPages) {
      sigState.page = sigState.totalPages;
      sigState.hasRows = false;
      box.innerHTML = '<tr><td colspan="11" class="empty">Loading…</td></tr>';
      loadSignalsPage();
      return;
    }
    renderSignalRows(res.items || []);
  } catch (err) {
    box.innerHTML = '<tr><td colspan="10" class="empty">Failed: ' + esc(err.message) + "</td></tr>";
  }
}

function renderSignalRows(items) {
  const se = document.scrollingElement || document.documentElement;
  const prevScroll = se.scrollTop;
  const box = document.getElementById("sigRows");
  sigState.hasRows = items.length > 0;
  if (!items.length) {
    box.innerHTML = '<tr><td colspan="11" class="empty">No signals' +
      (sigState.ticker ? " for " + esc(sigState.ticker) : "") + ".</td></tr>";
  } else {
    box.innerHTML = items.map(s => '<tr>' +
      '<td data-label="Time" class="mono">' + fmtTime(s.timestamp) + '</td>' +
      '<td data-label="Ticker"><b>' + esc(s.ticker || "") + '</b></td>' +
      '<td data-label="Type">' + signalTypeBadge(s.type) + '</td>' +
      '<td data-label="Direction">' + (s.direction === "Long" ? badge("LONG", "green") : s.direction === "Short" ? badge("SHORT", "red") : "—") + '</td>' +
      '<td data-label="Price" class="mono num">' + fmtNum(s.price, 4) + '</td>' +
      '<td data-label="SL" class="mono num">' + fmtNum(s.stopLoss, 4) + '</td>' +
      '<td data-label="TP" class="mono num">' + fmtNum(s.takeProfit, 4) + '</td>' +
      '<td data-label="Exit" class="mono num">' + fmtNum(s.exitPrice, 4) + '</td>' +
      '<td data-label="Exit reason">' + (s.exitReason ? esc(humanizeEnum(s.exitReason)) : "—") + '</td>' +
      '<td data-label="PnL" class="mono num">' + fmtPct(s.pnlPercent) + '</td>' +
      '<td data-label="Actions"><button class="btn" title="Repost this signal to Telegram as a duplicate" onclick="resendSignal(\'' + s.id + '\')">Resend signal to Telegram</button></td>' +
    '</tr>').join("");
  }
  document.getElementById("sigTotal").textContent = sigState.total ? sigState.total + " entries" : "";
  document.getElementById("sigPageInfo").textContent = "Page " + sigState.page + " / " + sigState.totalPages;
  document.getElementById("sigPrev").disabled = sigState.page <= 1;
  document.getElementById("sigNext").disabled = sigState.page >= sigState.totalPages;
  se.scrollTop = prevScroll;
}

document.getElementById("sigTicker").addEventListener("keydown", e => {
  if (e.key === "Enter") { e.preventDefault(); applyTickerFilter(); }
});

document.getElementById("instFilter").addEventListener("input", e => {
  instFilter = e.target.value.trim().toUpperCase();
  renderRows();
});

function toggleSimpleView() {
  simpleView = !simpleView;
  try { localStorage.setItem("ptsInstSimpleView", simpleView ? "1" : "0"); } catch (e) { /* storage unavailable */ }
  const btn = document.getElementById("simpleViewBtn");
  if (btn) btn.textContent = simpleView ? "Full view" : "Simple view";
  renderRows();
}
document.getElementById("simpleViewBtn").textContent = simpleView ? "Full view" : "Simple view";

document.getElementById("addForm").onsubmit = async e => {
  e.preventDefault();
  const f = e.target;
  const payload = Object.fromEntries(new FormData(f).entries());
  payload.ticker = payload.ticker.trim().toUpperCase();
  try {
    await api("/api/instruments", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    toast("Created " + payload.ticker);
    f.reset(); await refresh();
  } catch (err) { showError("Create failed: " + err.message); }
};

async function boot() {
  await refresh();
  setInterval(refresh, 5000);
}
boot();
