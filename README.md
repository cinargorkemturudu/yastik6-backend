# Yastık6 API

The backend for **Yastık6**, a gold / currency / crypto price tracker. It aggregates live prices from four independent providers, applies anomaly detection before anything reaches a client, and serves a mobile app over a small, static-key-authenticated HTTP API.

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

## Getting started

Requires the **.NET 10 SDK**.

```bash
dotnet restore
cp .env.example .env   # fill in ApiSettings__MobileClientKey and Harem__ApiKey
dotnet run --project "Yastık6.Api"   # listens on http://0.0.0.0:5179
```

Or via Docker: `docker compose up -d --build` (reads the same `.env`).

## Testing

```bash
dotnet test
```

Covers mapper correctness (Harem, Yahoo Finance name/type mapping), input validation, and the price pipeline's business rules — flash-crash/zero-price shields, USD/TRY-gated crypto conversion, expired-asset filtering, and a regression test locking in a real bug fix where `ValidUntilUtc` was being overwritten by two different layers after the mappers had already computed it correctly.

CI (`azure-pipelines.yml`) runs the full suite on a Microsoft-hosted agent before deploying; a failing test blocks the deploy stage entirely.

## Environment variables

Set in `.env` (see `.env.example`); the app throws on startup if either is missing:

- `ApiSettings__MobileClientKey` — shared secret validated against the frontend's `X-Mobile-Client-Key` header
- `Harem__ApiKey` — RapidAPI key for the Harem gold-price provider
