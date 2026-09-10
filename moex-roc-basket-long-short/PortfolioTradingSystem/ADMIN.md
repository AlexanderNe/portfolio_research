# Administrator Guide — PortfolioTradingSystem

Production operations for the `trading` systemd service on Ubuntu 24.04. Full
deployment steps are in `DEPLOYMENT.md`.

## Service name

The systemd unit is `trading.service`.

## Start

```bash
sudo systemctl start trading
```

Verify it is running:

```bash
sudo systemctl status trading --no-pager
journalctl -u trading -n 50 --no-pager
```

On first start the database schema is created and the 39 research-basket tickers
are seeded (all `Paused`). Expect the log line
`Portfolio Trading System starting; listening on ...` and no startup exceptions.

The app listens on port **5080** by default (configurable via
`ASPNETCORE_URLS`). Admin panel: `http://<host>:5080/admin`.

## Stop

```bash
sudo systemctl stop trading
```

The process receives SIGTERM and shuts down gracefully. All engine state,
open positions, and realized PnL are persisted in PostgreSQL and survive
restarts.

## Restart

```bash
sudo systemctl restart trading
```

Restarts are **expensive**: every running engine warms up by replaying up to
700 daily bars. If you only need to pause/resume trading activity, prefer
the admin UI or API instead of a process restart:

```bash
# Pause all instruments via API
curl -u admin:admin -X POST http://localhost:5080/api/control/stop

# Resume all instruments via API
curl -u admin:admin -X POST http://localhost:5080/api/control/resume
```

Individual instrument control:

```bash
# Pause one instrument (by database ID)
curl -u admin:admin -X POST http://localhost:5080/api/instruments/{id}/pause

# Resume one instrument
curl -u admin:admin -X POST http://localhost:5080/api/instruments/{id}/resume
```

## Update

1. **Build and publish** on the development machine:

   ```powershell
   dotnet publish .\PortfolioTradingSystem.Api -c Release -r linux-x64 --self-contained false
   ```

2. **Copy to the server:**

   ```powershell
   scp -r .\PortfolioTradingSystem.Api\bin\Release\net10.0\publish\* user@server:/opt/portfolio-trading/
   ```

3. **Fix the executable bit** (files copied from Windows lose it):

   ```bash
   sudo chmod +x /opt/portfolio-trading/PortfolioTradingSystem.Api
   ```

4. **Restart the service:**

   ```bash
   sudo systemctl restart trading
   ```

> **Schema changes:** the database is created with EF Core `EnsureCreated`,
> which does **not** migrate an existing database. If the data model changed
> (new/removed columns or tables), you must recreate the database before
> restarting. Drop and recreate `portfolio_trading`, then start the service —
> it will re-seed all instruments.

## View logs

### systemd journal (stdout/stderr)

```bash
# Last 100 lines
journalctl -u trading -n 100 --no-pager

# Follow live (Ctrl+C to stop)
journalctl -u trading -f

# Logs since a specific time
journalctl -u trading --since "2026-09-09 09:00" --no-pager

# Logs for today only
journalctl -u trading --since today --no-pager
```

### Serilog application logs

Serilog writes daily rolling files at `/opt/portfolio-trading/logs/`:

```
logs/log-20260909.txt
logs/log-20260908.txt
...
```

14 files are kept. These contain detailed application events: warm-up summaries,
open/close signals (with entry times), auth failures, ticker resolution
outcomes, and stream reconnect events.

```bash
# Tail today's log
tail -f /opt/portfolio-trading/logs/log-$(date +%Y%m%d).txt

# Search recent logs for errors
grep -i error /opt/portfolio-trading/logs/log-$(date +%Y%m%d).txt
```

## Troubleshooting

### Service fails immediately (`status=203/EXEC`)

The apphost lost its executable bit after scp from Windows. Fix:

```bash
sudo chmod +x /opt/portfolio-trading/PortfolioTradingSystem.Api
```

Verify it is an ELF binary (not a Windows PE32):

```bash
file /opt/portfolio-trading/PortfolioTradingSystem.Api
# Expected: ELF 64-bit ... x86-64
```

### Port already in use

```bash
sudo ss -tlnp | grep 5080
```

Kill the conflicting process or change `ASPNETCORE_URLS` in the service file.

### Missing environment variables

The app logs warnings for missing tokens at startup but will not connect to
T-Bank or Telegram without them. Verify the service file contains all
required variables (see `DEPLOYMENT.md` section 5).

### Database connection refused

Ensure PostgreSQL is running and the connection string matches:

```bash
sudo systemctl status postgresql --no-pager
psql -U trading -d portfolio_trading -c "SELECT 1"
```
