# Neuro-Eptura

An integration platform (Tray.ai-style): build workflows visually and move data between systems —
HTTP APIs and direct database connections (SQL Server / Azure SQL, PostgreSQL, MySQL).

- **Engine** — ASP.NET Core (.NET 10) workflow engine: node graph execution, per-step logging,
  retries, condition branches, loops, scheduled/webhook/manual triggers
- **Visual builder** — React 19 + React Flow canvas with a per-node config panel, validation and
  test runs with per-node status badges
- **Metadata store** — SQL Server / Azure SQL via EF Core (InMemory fallback for local dev)
- **Deployment** — Docker Compose stack and a Helm chart for Kubernetes (kind / AKS profiles)

## Quickstart (local dev)

```powershell
# Terminal 1 - API (InMemory store when no connection string is set)
cd backend
dotnet run --project src/IntegrationFramework.Api      # http://localhost:8000

# Terminal 2 - UI
cd frontend
npm install
npm run dev                                            # http://localhost:5173
```

A seeded sample workflow (webhook → transform → mock inventory reserve) ships ready to run.

## Containers, CI/CD & Kubernetes

CI runs build + tests on every push/PR; tagging `v*` publishes images to GHCR and cuts a GitHub
Release (`.github/workflows/`). See [deploy/README.md](deploy/README.md) for the quick start and
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the full deployment process (tooling, Compose, kind,
AKS, config reference, troubleshooting). The api container applies EF Core migrations on startup.

## Testing

See [docs/TESTING.md](docs/TESTING.md) — suite layout, how to run (94 tests), the live E2E
procedure, and platform gotchas.

## Phase 2 features (P0)

- **OAuth2 connections** — http connections support `authType: "oauth2"` with the
  `client_credentials` and `refresh_token` grants. Tokens are fetched/cached per connection and
  refreshed ahead of expiry; `client_secret` / `refresh_token` resolve from `*_env` references and
  are masked in API responses. The connection test endpoint proves a token can be acquired.
- **Entity mappings** — reusable field mappings between two systems with validation rules
  (type/required/length/range/regex), stored in the metadata store and applied by the
  `entity_mapping` workflow node. `POST /api/entity-mappings/{id}/validate` dry-runs a payload.
- **Webhook durability** — every inbound webhook delivery is persisted before execution
  (body + header snapshot, status, linked run). Failed/failed-to-process deliveries can be
  replayed via `POST /api/webhook-events/{id}/replay`, and the Webhooks page in the UI can
  simulate deliveries against any workflow.

## API surface

| Method | Path | Purpose |
|---|---|---|
| GET/POST | `/api/workflows` | List / create workflows (list filterable by `?projectId=`) |
| GET/PUT/DELETE | `/api/workflows/{id}` | Read / update / delete (update accepts `projectId`) |
| POST | `/api/workflows/{id}/validate` | Validate graph |
| POST | `/api/workflows/{id}/run` | Queue a manual run (202-queued, async-first) |
| GET | `/api/runs` | Paged run history: `?search=&page=&pageSize=` (search matches workflow name, status, error text, or an exact run id; pageSize default 100) |
| GET | `/api/runs/{id}` | Run detail with per-step I/O |
| POST | `/api/runs/{id}/rerun` | Queue a rerun with the original input (202-queued) |
| GET/POST | `/api/projects` | List / create projects (workflow grouping) |
| GET/PUT/DELETE | `/api/projects/{id}` | Read / update / delete (delete blocked while non-empty) |
| GET | `/api/dashboard/summary` | Aggregated totals/rates/buckets (`?range=hour|24h|7d|30d|6m&projectId=`) |
| GET/POST | `/api/connections` | List / create connections |
| PUT/DELETE | `/api/connections/{id}` | Update / delete |
| POST | `/api/connections/{id}/test` | Test HTTP auth or DB connectivity |
| POST | `/webhook/{workflowId}` | Webhook trigger (persisted as a durable event) |
| GET | `/api/webhook-events`, `/api/webhook-events/{id}` | Delivery history (filter by workflow/status) |
| POST | `/api/webhook-events/{id}/replay` | Re-execute the stored delivery body |
| GET/POST | `/api/entity-mappings` | List / create entity mappings |
| PUT/DELETE | `/api/entity-mappings/{id}` | Update / delete |
| POST | `/api/entity-mappings/{id}/validate` | Apply mapping + rules to a sample payload |
| GET | `/health` | Liveness + metadata provider info |
| — | `/demo/crm`, `/demo/inventory`, `/demo/secure`, `/demo/oauth2` | Mock systems + demo OAuth2 server for the seeded samples |

## Workflow graph model

```json
{
  "nodes": [
    { "id": "trigger", "type": "trigger", "config": { "trigger": "webhook" } },
    { "id": "shape",   "type": "transform", "config": { "mapping": { "sku": "$.input.sku" } } },
    { "id": "reserve", "type": "http_request",
      "config": { "url": "{$.env.self_base_url}/demo/inventory/reserve", "method": "POST",
                  "body": { "sku": "$.steps.shape.sku" } } }
  ]
}
```

Array order is execution order; condition nodes route via `on_true`/`on_false` node lists; loop
nodes iterate `body` over `$.`-referenced arrays. Step outputs resolve via `$.steps.<id>.<path>`,
env vars via `$.env.<name>`, run input via `$.input.<path>`. The `entity_mapping` node applies a
stored entity mapping (field mappings + validation rules) to the run input or a `source` path and
fails the step on any validation error.

## Secrets

Credentials never enter the codebase: connections reference environment variable NAMES
(`password_env` / `*_env` fields); values resolve at runtime from the process environment or
Kubernetes Secrets, and are masked in every API response.
