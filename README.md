# Yastık6 API

[![CI](https://github.com/cinargorkemturudu/yastik6-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/cinargorkemturudu/yastik6-backend/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

The backend for **Yastık6**, a gold / currency / crypto price tracker. It aggregates live prices from four independent providers, applies anomaly detection before anything reaches a client, and serves a mobile app over a small, static-key-authenticated HTTP API.

**Live demo:** a keyless, read-only tier of the production API is public: [`https://yastik6.net/api/public`](https://yastik6.net/api/public) (endpoint list and Swagger docs). The full project story is in the [portfolio showcase](https://github.com/cinargorkemturudu/Yastik6-Portfolio-Showcase). The Flutter client lives in [yastik6-app](https://github.com/cinargorkemturudu/yastik6-app).

## Stack

- ASP.NET Core Web API on **.NET 10** (`Yastık6.Api`)
- SQLite via **EF Core** (writes/migrations) + **Dapper** (read-heavy history queries)
- **Serilog** — console, rolling file, and [Seq](https://datalust.co/seq) sinks, with correlation-ID enrichment
- **Polly** for provider retry policies
- Docker / Docker Compose for deployment

## Architecture

```
Controllers  →  Application  →  Domain/Entities
 (thin HTTP)    (features, validators,     (entities)
                 interfaces)
                     ↓
                Infrastructure
     (providers, persistence, caching, alerts)
```

DI wiring is centralized in `ApplicationConfig/DependencyInjection.cs`.

### Price pipeline

Four independent providers feed the same pipeline, each behind a common `BasePriceClient` (Polly retry + structured logging):

- **Harem** — gold/metal prices (RapidAPI)
- **Yahoo Finance** — currencies, BIST stocks, crypto
- **FreeCurrency** — currency fallback
- **Binance** — crypto fallback

A background worker (`PriceSnapshotRefresher`) polls each provider on its own interval, falls back to a secondary provider if one crashes, and hands raw prices to `PriceProcessor`, which:

1. Validates the USD/TRY rate first (everything else depends on it converting crypto to TRY)
2. Filters zero/negative prices and expired ("dead") assets
3. Converts crypto to TRY using the validated rate
4. Runs every price through `PriceShieldValidator` — a flash-crash shield that rejects any price that moved more than 40% since the last known-good value, and a zero-price shield, both with a 1-hour alert-silencing window to avoid spamming Telegram

Each mapper computes its own `ValidUntilUtc` based on market state (open/closed, weekday/weekend) — that value is intentionally left untouched by every downstream layer.

## API

- `GET /api/prices/market` — cached snapshot of all current prices (refreshed in the background, not on-request)
- `GET /api/prices/history/{productCode}?range=...` — historical price points

All routes under `/api/` require an `X-Mobile-Client-Key` header matching `ApiSettings:MobileClientKey`. `/swagger` is key-gated outside `Development`. Any 2xx response to a route outside `/api/`, `/swagger`, `/health`, `/` fires a Telegram alert — an intentional anomaly-detection tripwire.

## Security model

The mobile client is an app anyone can inspect, so the design assumes its key leaks.

- **The client key is an identifier, not a secret.** `X-Mobile-Client-Key` is compiled into the app, so anyone holding a build can recover it. It filters casual scrapers and gives one value to rotate; it does not authenticate people. The server stores no user data: there are no accounts, and portfolios live on the device.
- **What an extracted key can reach:** `/api/prices/*`, which is public market data. Responses come from an in-memory snapshot refreshed by a background worker, so a request never calls a price vendor or touches the database.
- **Key check:** constant-time comparison, so response timing can't reveal how much of a guess was right. A wrong key gets a 401 before any controller runs.
- **Rate limiting:** a per-IP sliding window of 600 requests/minute on the app routes. It is deliberately generous: the app polls every 10 seconds, and Turkish mobile carriers put many users behind one NAT'd IP, so a tight per-IP limit would lock out legitimate users. The keyless public tier has much stricter per-visitor limits, a shared global ceiling, a Telegram alert when the ceiling is hit, and a kill switch (`PublicApi__Enabled=false`).
- **Other layers:** Cloudflare in front of nginx; Swagger is key-gated outside Development; provider keys live only in the server's `.env`; a tripwire alerts on any successful response to a route the API doesn't define.
- **Known limits:** there is no per-install attestation (Apple App Attest / Google Play Integrity), because that needs store developer accounts. Rotating the client key means shipping an app update. Per-install tokens with per-token rate limits are the planned next step.

## Getting started

Requires the **.NET 10 SDK** and Python 3 (only for the local Harem stub below).

The Harem gold feed is a paid RapidAPI service. To run without a key, use the bundled stub, which serves a small valid Harem payload. The other providers (Yahoo Finance, Binance) need no key.

```bash
python3 scripts/harem-stub.py &    # fake Harem feed on 127.0.0.1:8765

export ApiSettings__MobileClientKey=demo-key
export Harem__ApiKey=dummy Harem__MarketPath=/harem_altin/prices/dummy
export Harem__BaseUrl=http://127.0.0.1:8765 Harem__Host=localhost

dotnet run --project "Yastık6.Api"   # listens on http://0.0.0.0:5179

curl -H "X-Mobile-Client-Key: demo-key" http://localhost:5179/api/prices/market
```

For real data, copy `.env.example` to `.env`, fill in your own keys, and load it (`set -a; source .env; set +a`) before `dotnet run`. Docker Compose reads `.env` directly: `docker compose up -d --build`.

## Testing

```bash
dotnet test
```

Covers mapper correctness (Harem, Yahoo Finance name/type mapping), input validation, and the price pipeline's business rules — flash-crash/zero-price shields, USD/TRY-gated crypto conversion, expired-asset filtering, and a regression test locking in a real bug fix where `ValidUntilUtc` was being overwritten by two different layers after the mappers had already computed it correctly.

CI/CD: GitHub Actions (`.github/workflows/ci.yml`) runs the full suite on every push and pull request. Deployment is an Azure Pipelines run (`azure-pipelines.yml`) on a self-hosted agent: tests first, then an SSH deploy that pulls the code and rebuilds the Docker Compose stack. A failing test blocks the deploy stage entirely.

## Environment variables

Set in `.env` (see `.env.example`); the app refuses to start without a mobile client key and a Harem market path:

- `ApiSettings__MobileClientKey` — shared secret validated against the frontend's `X-Mobile-Client-Key` header
- `Harem__ApiKey` — RapidAPI key for the Harem gold-price provider
- `Harem__MarketPath` — endpoint path from your RapidAPI subscription

## License

[MIT](LICENSE)
