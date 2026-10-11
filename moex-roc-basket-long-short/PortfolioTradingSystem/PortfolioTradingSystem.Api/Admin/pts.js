/*
  Shared helpers for the embedded admin UI. Loaded as a CLASSIC script (not an
  ES module) so that the pages' inline onclick="" handlers and top-level
  function/const bindings keep working in the global scope.

  Exposes a single global: window.PTS.
*/
(function () {
  "use strict";

  function esc(s) {
    return String(s === null || s === undefined ? "" : s).replace(/[&<>"']/g, c => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
    }[c]));
  }

  function fmtNum(v, digits) {
    if (v === null || v === undefined || isNaN(v)) return "—";
    return Number(v).toLocaleString("ru-RU", {
      minimumFractionDigits: 0,
      maximumFractionDigits: digits === undefined ? 2 : digits
    });
  }

  // Unsigned, up to `digits` decimals (instrument prices / ROC / ATR).
  function fmtPrice(v, digits) {
    if (v === null || v === undefined || isNaN(v)) return "—";
    return Number(v).toLocaleString("ru-RU", {
      minimumFractionDigits: 0,
      maximumFractionDigits: digits === undefined ? 4 : digits
    });
  }

  function fmtPct(v) {
    if (v === null || v === undefined || isNaN(v)) return "—";
    return (v > 0 ? "+" : "") + Number(v).toLocaleString("ru-RU", {
      minimumFractionDigits: 2, maximumFractionDigits: 2
    }) + "%";
  }

  // Signed amount, 2 decimals (admin/details PnL display).
  function fmtMoney(v) {
    if (v === null || v === undefined || isNaN(v)) return "—";
    return (v > 0 ? "+" : "") + Number(v).toLocaleString("ru-RU", {
      minimumFractionDigits: 2, maximumFractionDigits: 2
    });
  }

  // Unsigned amount, 2 decimals (equity axis/legend).
  function fmtMoneyPlain(v) {
    if (v === null || v === undefined || isNaN(v)) return "—";
    return Number(v).toLocaleString("ru-RU", {
      minimumFractionDigits: 2, maximumFractionDigits: 2
    });
  }

  // Date-time with seconds (admin). Empty input -> "—".
  function fmtTime(iso) {
    if (!iso) return "—";
    const d = new Date(iso);
    return isNaN(d) ? "—" : d.toLocaleString("ru-RU", {
      day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit"
    });
  }

  // Date-time without seconds (details signal rows/tooltips).
  function fmtTimeShort(iso) {
    const d = new Date(iso);
    return isNaN(d) ? "—" : d.toLocaleString("ru-RU", {
      day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit"
    });
  }

  // Accepts an ISO string (details) or an epoch-ms number (equity).
  function fmtDay(value) {
    const d = new Date(value);
    return isNaN(d) ? "—" : d.toLocaleDateString("ru-RU", {
      day: "2-digit", month: "2-digit", year: "2-digit"
    });
  }

  // MOEX calendar day (UTC+3) of an instant, as an int yyyymmdd. The strategy's
  // candles and fills live on Moscow trading days; mapping both to this key (and
  // NOT to the absolute instant) keeps a marker on the exact day the signal
  // happened even when the candle's and the fill's timestamps disagree intraday.
  function mskDayKey(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return null;
    const m = new Date(d.getTime() + 3 * 3600 * 1000);
    return m.getUTCFullYear() * 10000 + (m.getUTCMonth() + 1) * 100 + m.getUTCDate();
  }

  function badge(text, cls) {
    return '<span class="badge ' + (cls || "gray") + '">' + esc(text) + "</span>";
  }

  function signalTypeBadge(t) {
    if (t === "PositionOpened") return '<span class="badge green">OPEN</span>';
    if (t === "PositionClosed") return '<span class="badge blue">CLOSE</span>';
    return '<span class="badge gray">' + esc(t || "—") + "</span>";
  }

  function humanizeEnum(s) {
    return s ? String(s).replace(/([A-Z])/g, " $1").trim() : "";
  }

  function setPosCls(el, v) {
    el.classList.remove("up", "down");
    if (typeof v === "number" && !isNaN(v)) el.classList.add(v >= 0 ? "up" : "down");
  }

  // Thin fetch wrapper: same-origin credentials, structured errors.
  // Throws an Error carrying `.status` (401 included); JSON bodies are merged
  // onto the error object so callers can read server fields (e.g. candidates).
  async function api(path, opts) {
    const res = await fetch(path, Object.assign({ credentials: "same-origin" }, opts || {}));
    if (res.status === 401) {
      const e = new Error("401");
      e.status = 401;
      throw e;
    }
    if (!res.ok) {
      let text = "";
      try { text = await res.text(); } catch (e) { /* ignore */ }
      const err = new Error(text || ("HTTP " + res.status));
      err.status = res.status;
      try { Object.assign(err, JSON.parse(text)); } catch (e) { /* not JSON */ }
      throw err;
    }
    if (res.status === 204) return null;
    const ct = res.headers.get("content-type") || "";
    return ct.indexOf("json") >= 0 ? res.json() : res.text();
  }

  window.PTS = {
    esc, fmtNum, fmtPrice, fmtPct, fmtMoney, fmtMoneyPlain,
    fmtTime, fmtTimeShort, fmtDay, mskDayKey,
    badge, signalTypeBadge, humanizeEnum, setPosCls, api
  };
})();
