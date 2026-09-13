# Portfolio Trading System

A .NET 10 service that runs the validated `roc_momentum` daily momentum strategy on
the MOEX equity basket using T-Bank (Tinkoff) market data. It streams live 1-minute
candles, aggregates them into sessions, tracks strategy state in PostgreSQL, exposes
a browser admin panel, and posts entry/exit signals to Telegram.

**Important:** this system tracks strategy state and publishes *advisory signals* — it
does not place real orders. Execution is left to the operator.

## Related research

The strategy logic is a faithful C# port of the Python research simulator:

- `../strategy_research.py` — Simulator + `strat_roc_momentum`.
- `../report` — backtest output. The research package is the single source of truth
  for strategy parameters; do not re-derive them in the app.

## Repository layout

| Project | Purpose |
|---|---|
| `PortfolioTradingSystem.Domain` | Strategy engine state, entity/domain models, enums. Pure, no dependencies. |
| `PortfolioTradingSystem.Application` | Engine orchestration, supervisor, metrics, configuration, ports (interfaces). |
| `PortfolioTradingSystem.Infrastructure` | T-Bank clients (gRPC), ticker resolution, EF Core repositories, Telegram, DB seeding. |
| `PortfolioTradingSystem.Api` | ASP.NET host: admin endpoints, embedded admin UI, auth, logging. |

## Quick start

Requirements:

- .NET SDK 10
- PostgreSQL (default `localhost:5432`, db `portfolio_trading`, user `postgres`)
- A T-Bank Invest API token (https://www.tinkoff.ru/invest/settings/) or sandbox token

Build:

```powershell
dotnet build PortfolioTradingSystem.slnx
```

Configure `PortfolioTradingSystem.Api/appsettings.json` (or environment variables,
e.g. `Tinkoff__AccessToken`):

```jsonc
"Tinkoff": {
  "AccessToken": "t.<your-token>",
  "SandboxMode": false,
  "ResolveClassCode": "TQBR"   // MOEX equities main board; empty = any class
},
"Telegram": { "Enabled": true, "BotToken": "...", "ChannelId": "@channel" }
```

On first run the schema is created (`EnsureCreated`) and the 39 research-basket
tickers are seeded (all `Paused`; nothing trades until resumed).

Run:

```powershell
dotnet run --project PortfolioTradingSystem.Api
```

Admin panel: <http://localhost:5080/admin> (HTTP Basic auth, defaults `admin`/`admin`).

## Workflow

1. **Add** an instrument by ticker only (T-Bank resolution fills UID/FIGI via
   `ResolveClassCode` classes; ambiguous tickers are reported, never guessed).
2. **Resolve** fills UID/FIGI for instruments that still lack them (single or "Resolve all").
3. **Resume** (per instrument or "Resume all") starts the engine: warm-up on up to
   700 historical daily bars, then live 1-minute streaming.
4. Watch position/ROC/ATR/signal in the panel or get Telegram notifications.

## Strategy (roc_momentum)

Once warmed up, each completed trading session updates indicators from **the last
completed daily bar only** (no look-ahead):

- `ROC = close / close[5 sessions ago] − 1`
- signal `+1` if `ROC > 0.01`, `−1` if `ROC < −0.01`, else `0`
- ATR = EWM smoothing (alpha = 1/14) of true range

Entry at the next session's open when the prior close produced a signal. Sizing:

- `units = floor(risk% · cash / (2·ATR))`, capped by
  `floor(leverage · cash / (notional · (1 + commission)))`, then rounded **down to
  whole exchange lots** (`Instrument.LotSize`)
- `SL = entry ∓ 2·ATR`, `TP = entry ± 3·ATR`
- commission `0.04%` per side; SL checked before TP (research convention); a
  candle that opens beyond the level fills at the open

**When an entry is advised.** Only when this session's open was actually observed:
either the engine saw the previous session end, or its first candle of the session
falls inside `Strategy:EntryWindowMinutes` (default 15) of the 10:00 Moscow open.
An engine started or resumed at 14:00 waits for the next session rather than
advising a fill at a price hours old. An entry is also never advised in a session
in which a position has already been closed - that price is gone by the time the
stop or target is hit.

Documented live deviation: intraday SL/TP are also checked on each 1-minute candle
after the entry candle (research checks daily only). This is not simply "more
conservative": it also takes winners at the target earlier, so the live trade
distribution differs from the backtest by an amount that has not been measured.

## Configuration reference

`Strategy` (defaults = research): `RocBars=5`, `RocThreshold=0.01`, `AtrPeriod=14`,
`SlAtr=2`, `TpAtr=3`, `RiskPct=10`, `CommissionPct=0.04`, `PointRub=1`, `Leverage=1`,
`InitialCapital=100000`, `ShortsEnabled=true`, `EntryWindowMinutes=15`.

`Tinkoff`: `ApiUrl`, `AccessToken`, `SandboxMode`, `HistoricMaxBars=700`,
`ResolveClassCode=TQBR`.

`Engine`: `RefreshInstrumentsIntervalSeconds=60`, `ReconnectDelaySeconds=2`,
`ReconnectMaxDelaySeconds=60`.

`Admin`: `Username`, `Password`, `Realm`. `BasicAuth` protects the whole app
(constant-time comparison; credentials never logged). **`Admin:Password` ships
empty and auth fails closed**: until it is set every request is rejected and
startup logs an error. `appsettings.Development.json` keeps `admin`/`admin` so a
local `dotnet run` still works.

## Admin API

| Method | Route | Purpose |
|---|---|---|
| GET | `/` | Text summary (instrument count, engines, global state) |
| GET | `/admin` | Embedded admin UI |
| GET | `/chart?id=` | Per-instrument price chart (new tab) |
| GET | `/api/instruments` | Instruments + metrics (Running first, then ticker) |
| GET | `/api/instruments/{id}` | Single instrument + metrics |
| GET | `/api/instruments/{id}/chart-data` | Chart payload: day candles + trades + open position (SL/TP) |
| GET | `/api/instruments/{id}/signals?limit=` | Signal log (default 100, max 500) |
| GET | `/api/signals?ticker=&page=&pageSize=` | Paged signal log for all instruments, newest first (pageSize default 50, max 200) |
| GET | `/api/signals/tickers` | Distinct tickers present in the signal log |
| POST | `/api/instruments` | Create by ticker |
| PUT | `/api/instruments/{id}` | Update fields |
| DELETE | `/api/instruments/{id}` | Delete |
| POST | `/api/instruments/{id}/resolve` | Resolve ticker to T-Invest instrument |
| POST | `/api/instruments/resolve-all` | Resolve all instruments lacking UID |
| POST | `/api/instruments/{id}/close` | Close the open position now (optional `?price=`, reason `Manual`) |
| POST | `/api/instruments/{id}/pause` | Pause engine |
| POST | `/api/instruments/{id}/resume` | Resume engine |
| POST | `/api/signals/{id}/resend` | Re-post a logged signal to Telegram (prefixed "duplicate, NOT a new signal") |
| POST | `/api/instruments/{id}/resend-active` | Re-post the active (open) position signal to Telegram as a duplicate |
| POST | `/api/control/stop` | Global stop (all instruments → Paused) |
| POST | `/api/control/resume` | Global resume (all instruments → Running) |
| POST | `/api/control/reset` | Clear metrics, restart all engines |

## Instrument state

One state per instrument: `Paused` ↔ `Running`.

- Seeds start `Paused`. Only `Running` instruments with a UID or FIGI get an engine.
- `Pause`/`Resume` (per instrument) and the global control endpoints drive this state.

## Persistence

Tables (EF Core, `EnsureCreated`): `Instruments`, `OpenPositions`, `TradeLogs`,
`SignalLogs`. Positions and realized PnL survive restarts (cash = initial capital +
sum of realized PnL). Opening and closing a position are each one transaction, so a
crash cannot leave a trade counted *and* its position still open. Deleting an
instrument cascades to its positions, trades and signals. **Because the schema is
created with `EnsureCreated`, model changes do not migrate an existing database** —
recreate the DB when the schema changes. `OpenPositions.InstrumentId` gained a
UNIQUE index, so an existing database must be recreated after this change.

## Logging

Serilog to console and `logs/log-yyyyMMdd.txt` (daily, 14 files kept). Successful
GET requests are suppressed (Verbose); failures and mutations log at Information.
Logs include warm-up summary (last bar, ROC, signal, ATR), every open/close signal
(with entry time), auth failures, and ticker-resolution outcomes.

## Development

- Build: `dotnet build PortfolioTradingSystem.slnx` (must be 0 errors / 0 warnings).
- Unit tests: `PortfolioTradingSystem.Tests` (xUnit) covers `MomentumEngineState`
  algorithm math, the execution model, `InstrumentEngine` against in-memory fakes,
  and **parity with the Python simulator** (`ResearchParityTests` replays 300 real
  SBER bars and compares every trade with `Fixtures/sber_trades.csv`, generated by
  `strategy_research.py`). Run `dotnet test PortfolioTradingSystem.Tests`. It
  references Domain and Application; no real broker, database or app state.
- `Directory.Build.props` sets `TreatWarningsAsErrors` and the .NET analysers, so
  "0 errors / 0 warnings" is enforced rather than assumed.
- C# conventions: file-scoped namespaces, records for domain events, no comments
  beyond XML docs for public API.