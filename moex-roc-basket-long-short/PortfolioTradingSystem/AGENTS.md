# AGENTS.md — model notes (internal)

Working notes for future coding sessions on this repository. This file is for the
agent, not for end users (see `README.md` for user/developer docs).

## Hard constraints

- **Never run the app.** Only build: `dotnet build PortfolioTradingSystem.slnx`
  (angles: working dir `C:\Users\...\PortfolioTradingSystem`, or via `workdir`).
- Every delivery must end with a build that is **0 errors and 0 warnings**.
- **Never modify the Python research** (`../strategy_research.py`, `../report`,
  `../data/`) — it is the single source of truth for the strategy. (Exception,
  2026-09-12, on explicit request: the simulator's execution model was corrected;
  see `../CODE_REVIEW.md`. `../report` is still NOT regenerated.)
- Never commit, amend, or push unless explicitly asked.
- `PortfolioTradingSystem.Tests` (xUnit) covers `MomentumEngineState` math, the
  execution model, `InstrumentEngine` against in-memory fakes, and parity with the
  Python simulator on a fixture of real bars. It references Domain + Application;
  no real broker, database or app state. Run `dotnet test PortfolioTradingSystem.Tests`.
- Do not log credentials/tokens/secret values. Log only presence flags
  (e.g. `tokenConfigured`).

## Verified SDK / codebase facts (do not re-derive)

- Target: .NET 10, solution file `PortfolioTradingSystem.slnx`.
- Candle model: record struct `Candle(DateTimeOffset Time, Open, High, Low, Close,
  Volume, IsComplete)`, Time already in Moscow tz, bar-open semantics.
- T-Bank (Tinkoff.InvestApi) share-kind enums: model uses **`InstrumentType.Share`**
  (NOT `InstrumentKind`); `FindInstrumentRequest.InstrumentKind` is the API field.
- Candle requests use `CandleInstrument.InstrumentId` / `GetCandlesRequest.InstrumentId`
  (string, auto-detects FIGI or UID). Do not reintroduce obsolete `.Figi` fields.
- UID is the primary identifier (`Uid` ≥ `Figi`); `InstrumentShort.Lot` is int.
- Ticker resolution: `TinkoffTickerResolver` filters by `Tinkoff:ResolveClassCode`
  (default `TQBR`), exact ticker match, `DistinctBy(Uid)`, statuses
  Resolved/NotFound/Ambiguous. Ambiguous never guessed.
- Single instrument state: `ProcessingStatus.Paused|Running` (TradingStatus was
  removed deliberately). Engine supervisor gate: `Running` AND has `Uid`/`Figi`.
- Global stop/resume persist ALL instruments' `ProcessingStatus` via
  `IInstrumentRepository.SetProcessingStatusAsync` (bulk `ExecuteUpdate`),
  then `SyncInstrumentsAsync`.
- Admin list ordering: Running first, then ticker (server-side in
  `GET /api/instruments`).
- Admin API surface: see README table. All routes Basic-Auth protected
  (`BasicAuthMiddleware`, configurable `Admin:Username/Password`, default admin/admin).
- Program.cs: `UseSerilogRequestLogging` suppresses successful GET/HEAD
  (2xx/3xx → Verbose); failures (>=400, exceptions) and non-GET stay Information.
- EF Core `EnsureCreated`: model changes DO NOT migrate existing DBs. If a column/
  table changes, the dev DB must be recreated. Recent example: `Uid` column and the
  `TradingStatus` column removal.

## Engine behavior (InstrumentEngine / MomentumEngineState)

- Warm-up: up to `HistoricMaxBars` (700) daily bars; ATR seeded from first high-low,
  then EWM alpha=1/14; ROC from `close/close[n]−1`; PendingSignal +1/−1/0.
- Entry: at the session open, and ONLY when that open was observed — the engine
  either saw the previous session end, or its first candle of the session is inside
  `Strategy:EntryWindowMinutes` (15) of 10:00 MSK. Mid-session activation therefore
  waits for the NEXT session instead of quoting a stale open. An entry is also
  never advised in a session that already produced an exit. (Both replaced the old
  "entries appear immediately after resume" behaviour on 2026-09-12; removing
  either guard fails `InstrumentEngineTests`.)
- Entry timestamp = the 1-minute candle's start time (may predate the engine start
  timestamp). The "Opened" log now includes this time.
- Intraday SL/TP checked only for positions that existed BEFORE the candle.
- Sizing: `units = floor(risk%·cash/(SlAtr·ATR))`, cap `floor(leverage·cash/notional)`,
  `SL=entry∓SlAtr·ATR`, `TP=entry±TpAtr·ATR`, commission both sides, SL before TP.
- Restart recovery: cash = `InitialCapital` + sum(realized PnL) with the open
  position's committed entry cost applied (long: −notional − open commission,
  short: +margin credit − open commission), matching the simulator's cash mechanics.
  Open position restored from `OpenPositions`. A resume mid-session no longer
  re-enters: the session open was not observed, so the engine waits for the next one.
- Open/close each go through `ITradeJournal` — one DbContext, one transaction —
  so a crash cannot leave "trade counted AND position still open".
- Sizing rounds down to whole `Instrument.LotSize` lots and the leverage cap leaves
  room for the opening commission.
- Stream reconnect: exponential backoff 2s → 60s.
- Telegram: manual re-send is driven from the admin UI (never inline on the signal
  itself). Signal-log rows → `POST /api/signals/{id}/resend`; the active position's
  signal in the instruments table → `POST /api/instruments/{id}/resend-active`.
  Both rebuild the message from the persisted `SignalLogEntry` and prefix it with
  "⚠️ This is a duplicate of a previously sent signal — NOT a new signal."
  (`TelegramMessageFormatter.Duplicate`).
- Admin `admin.html` error box is absolutely centered in the header (never pushes
  layout); falls back to in-flow on <900px.
- Market data feeds: `TinkoffMarketDataMultiplexer` multiplexes ALL instruments over ONE
  `MarketDataServerSideStream` connection (T-Bank limit: 32 simultaneous quotes streams,
  300 subscriptions per stream, counter refreshes every 2 min). Error 80001 = "Limit of
  open streams exceeded"; a stream-per-engine design trips it. Port is now subscription
  based: `IMarketDataGateway.SubscribeAsync(instrumentId, ct)` — channels survive stream
  reconnect; on membership change the shared stream is reopened with the full set.

## Known caveats / watch-items

- `EnsureCreated` schema — recreate DB on model changes (see above). The UNIQUE
  index on `OpenPositions.InstrumentId` added 2026-09-12 is exactly such a change.
- `TinkoffMappers.TodayMoscowStart` is a METHOD. As a static readonly field it froze
  at process start and stale-dated every warm-up after long uptime.
- Sunday/Moscow calendar anomalies can produce surprising candle timestamps (e.g. an
  entry at 21:28 Moscow on a non-trading Sunday in the Sept 2026 session); flag
  before shipping changes, don't silently "fix".
- Research CSV (`data/candles_ru_daily/AFLT.csv` and similar) stops 2026-08-28 —
  cannot reproduce live signals for later dates from local files alone.
- Additions to README/AGENTS should stay lean; keep user docs accurate (no invented
  endpoints or options).