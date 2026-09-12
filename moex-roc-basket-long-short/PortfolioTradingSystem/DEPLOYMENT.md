# Deployment — Ubuntu 24.04 LTS

Runbook for the `PortfolioTradingSystem.Api` .NET 10 ASP.NET Core service. The
app uses PostgreSQL (schema auto-created by EF Core `EnsureCreated` on first
start), the T-Bank Invest API, and Telegram.

## 1. Install .NET 10

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-10.0
```

The SDK includes the runtime. For run-only machines you may install
`aspnetcore-runtime-10.0` instead and skip building on the server.

## 2. PostgreSQL

```bash
sudo apt install -y postgresql
sudo systemctl enable --now postgresql

sudo -u postgres psql -c "CREATE USER trading WITH PASSWORD '<strong-password>';"
sudo -u postgres psql -c "CREATE DATABASE portfolio_trading OWNER trading;"
```

The app creates its own schema (`EnsureCreated`) on first start, so the DB user
must own (or have rights to create tables in) the database — DB ownership above
is sufficient.

## 3. Set the server timezone

The engine timestamps candles in Moscow time:

```bash
sudo timedatectl set-timezone Europe/Moscow
```

## 4. Publish the app

Option A — cross-compile from the development machine (no SDK required on the
server):

```powershell
dotnet publish .\PortfolioTradingSystem.Api -c Release -r linux-x64 --self-contained false
scp -r .\PortfolioTradingSystem.Api\bin\Release\net10.0\publish\* user@server:/opt/portfolio-trading/
```

Option B — build on Ubuntu after copying the `PortfolioTradingSystem` sources:

```bash
dotnet publish PortfolioTradingSystem.Api -c Release -o /opt/portfolio-trading
```

Either way, prepare the target directory and its log subdirectory:

```bash
sudo mkdir -p /opt/portfolio-trading/logs
```

> **Files copied with `scp` from Windows lose the executable bit.** Before starting
> the service, ensure the apphost can be executed, or systemd will fail with
> `status=203/EXEC`:
>
> ```bash
> sudo chmod +x /opt/portfolio-trading/PortfolioTradingSystem.Api
> ```
>
> If `file /opt/portfolio-trading/PortfolioTradingSystem.Api` shows `PE32+` instead
> of `ELF 64-bit ... x86-64`, the app was published without `-r linux-x64` —
> republish with the RID (see option A) and redeploy.

## 5. Secrets

`appsettings.json` ships with placeholder/default values. Production secrets are
passed via environment variables (`__` separates sections), not committed:

| Variable | Purpose |
|---|---|
| `Tinkoff__AccessToken` | T-Bank Invest API token |
| `Telegram__Enabled` | `true` |
| `Telegram__BotToken` | Bot token |
| `Telegram__ChannelId` | e.g. `@channel` |
| `Database__ConnectionString` | See section 2 |
| `Admin__Username` / `Admin__Password` | Admin panel credentials (Basic Auth). **Required**: auth fails closed while the password is empty. |

`appsettings.Production.json` only forces `SandboxMode: false`; it is loaded
automatically when `ASPNETCORE_ENVIRONMENT=Production`.

## 6. systemd service

```bash
sudo useradd --system --home-dir /opt/portfolio-trading --no-create-home dotnet-service
sudo chown -R dotnet-service:dotnet-service /opt/portfolio-trading
```

Create `/etc/systemd/system/trading.service`:

```ini
[Unit]
Description=Portfolio Trading System
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
Type=simple
User=dotnet-service
Group=dotnet-service
WorkingDirectory=/opt/portfolio-trading
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
EnvironmentFile=/etc/portfolio-trading.env
ExecStart=/opt/portfolio-trading/PortfolioTradingSystem.Api
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/opt/portfolio-trading/logs

[Install]
WantedBy=multi-user.target
```

Secrets go in `/etc/portfolio-trading.env` — **not** in `Environment=` lines, which
any local user can read with `systemctl show`:

```bash
sudo install -m 0600 /dev/null /etc/portfolio-trading.env
sudo tee /etc/portfolio-trading.env >/dev/null <<'EOF'
Database__ConnectionString=Host=localhost;Port=5432;Database=portfolio_trading;Username=trading;Password=<strong-password>
Tinkoff__AccessToken=<token>
Telegram__Enabled=true
Telegram__BotToken=<token>
Telegram__ChannelId=@channel
Admin__Username=admin
Admin__Password=<admin-password>
EOF
```

## 7. Start and verify

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now trading
sudo systemctl status trading --no-pager
journalctl -u trading -n 50
```

First start creates the schema and seeds the 39 research-basket tickers (all
`Paused` — nothing trades until resumed in the admin UI). Expect the log line
`Portfolio Trading System starting; listening on ...` and no startup exceptions.

## 8. Exposure

The admin UI is protected only by HTTP Basic Auth over plain HTTP, so the
credentials travel in clear text. **Do not open port 5080 to the internet.** Bind
the app to localhost and terminate TLS in nginx:

The unit file above already binds loopback only.

```bash
sudo ufw allow 22/tcp
sudo ufw allow 443/tcp
sudo ufw enable        # 5080 stays closed; nginx proxies to it over loopback
```

The app logs a warning at startup when it binds `0.0.0.0` over plain HTTP, and an
error when `Admin:Password` is unset (in which case every request is rejected).

## Operations notes

- Serilog writes daily logs at `logs/log-yyyyMMdd.txt` (14 files kept) under the
  service working directory.
- Restarts are expensive: warm-up replays up to 700 daily bars per running engine,
  and per `AGENTS.md` a start/resume can re-enter an instrument immediately and can
  show timestamps on a non-trading Sunday. Prefer `Pause`/`Resume` over process
  restarts where possible.
- The DB uses `EnsureCreated`; model changes require recreating the database
  (see README → Persistence). The UNIQUE index on `OpenPositions.InstrumentId`
  is such a change: an existing database must be recreated.
- A stopped engine is restarted automatically by the supervisor within
  `Engine:RefreshInstrumentsIntervalSeconds`; a restart is logged as a warning
  ("Engine X is no longer running"). Repeated warnings mean the instrument keeps
  failing — check the error in the admin panel.