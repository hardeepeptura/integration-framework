# Integration Framework

A Tray.ai-style integration platform: build workflows visually and move data between systems —
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

## Containers & Kubernetes

See [deploy/README.md](deploy/README.md) — Docker Compose stack (api + worker + frontend + mssql)
and Helm chart with `values-kind.yaml` / `values-aks.yaml` profiles.

## API surface

| Method | Path | Purpose |
|---|---|---|
| GET/POST | `/api/workflows` | List / create workflows |
| GET/PUT/DELETE | `/api/workflows/{id}` | Read / update / delete |
| POST | `/api/workflows/{id}/validate` | Validate graph |
| POST | `/api/workflows/{id}/run` | Manual run (inline) |
| GET | `/api/runs`, `/api/runs/{id}` | Run history / detail with per-step I/O |
| POST | `/api/runs/{id}/rerun` | Rerun with original input |
| GET/POST | `/api/connections` | List / create connections |
| PUT/DELETE | `/api/connections/{id}` | Update / delete |
| POST | `/api/connections/{id}/test` | Test HTTP auth or DB connectivity |
| POST | `/webhook/{workflowId}` | Webhook trigger |
| GET | `/health` | Liveness + metadata provider info |
| — | `/demo/crm`, `/demo/inventory` | Mock systems for the sample workflow |

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
env vars via `$.env.<name>`, run input via `$.input.<path>`.

## Secrets

Credentials never enter the codebase: connections reference environment variable NAMES
(`password_env` / `*_env` fields); values resolve at runtime from the process environment or
Kubernetes Secrets, and are masked in every API response.
