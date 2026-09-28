# Deployment Process

Canonical artifact locations: container files under `deploy/docker/`, Helm chart under
`deploy/helm/integration-framework/`, and the quick-start version of this guide in
`deploy/README.md`.

## 0. Tooling

| Tool | Install | Check |
|---|---|---|
| .NET SDK 10 | `winget install Microsoft.DotNet.SDK.10` | `dotnet --list-sdks` |
| Node + npm | already present (Node 25 / npm 11) | `node --version` |
| Docker Desktop | `winget install Docker.DockerDesktop` (start it after) | `docker --version` |
| kubectl | `winget install Kubernetes.kubectl` | `kubectl version --client` |
| Helm | `winget install Helm.Helm` | `helm version` |
| kind (local clusters) | `winget install Kubernetes.kind` | `kind version` |

Notes from this machine (2026-09): new shells may not see freshly installed tools — refresh
`$env:Path` from the Machine/User scope. `dotnet nuget list source` must list a source (add
nuget.org if empty). `gh` is installed and authenticated as `hardeepeptura`.

## 1. Local development

```powershell
# API on :8000 - InMemory metadata store unless METADATA_CONNECTION_STRING is set
cd backend
dotnet run --project src/IntegrationFramework.Api

# Optional: run against SQL Server / Azure SQL (migrations applied at startup)
$env:METADATA_CONNECTION_STRING = "Server=<host>,1433;Database=<db>;User Id=<user>;Password=<password>;TrustServerCertificate=True"

# UI on :5173 (dev server proxies /api, /webhook, /demo to :8000)
cd frontend
npm install
npm run dev
```

Port note: `dotnet run` uses `launchSettings.json`; to force a port, add `--no-launch-profile`
and set `ASPNETCORE_URLS`. With `IF_ROLE` unset the scheduler runs in-process.

## 2. Docker Compose (full stack, no K8s)

```powershell
cd deploy
Copy-Item docker\.env.example docker\.env   # set MSSQL_SA_PASSWORD
docker compose up -d --build
# UI http://localhost:8080  |  API http://localhost:8000/health
docker compose down    # add -v to drop the mssql volume
```

Services: `api` (IF_ROLE=api, no scheduler), `worker` (same image, IF_ROLE=worker, scheduler),
`frontend` (nginx SPA proxying /api /webhook /demo), `mssql` (volume `mssql-data`).

## 3. Kubernetes via Helm

### kind (local)

```powershell
docker build -f deploy/docker/Dockerfile.backend  -t integration-api:local .
docker build -f deploy/docker/Dockerfile.frontend -t integration-frontend:local .
kind load docker-image integration-api:local integration-frontend:local
kubectl create namespace integration
helm install integration deploy/helm/integration-framework -n integration `
  -f deploy/helm/integration-framework/values-kind.yaml
```

### AKS

```powershell
az acr build --registry <acr> --image integration-api:latest    -f deploy/docker/Dockerfile.backend .
az acr build --registry <acr> --image integration-frontend:latest -f deploy/docker/Dockerfile.frontend .
# Secrets out-of-band (never in values/git):
kubectl -n integration create secret generic integration-metadata `
  --from-literal=connection-string="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"
kubectl -n integration create secret generic integration-secrets `
  --from-literal=mssql-password="..."
helm install integration deploy/helm/integration-framework -n integration `
  -f deploy/helm/integration-framework/values-aks.yaml
```

Upgrade/rollback: `helm upgrade integration ... -f <values>`; `helm rollback integration <rev>`.

## 4. Configuration reference

| Variable | Purpose |
|---|---|
| `METADATA_CONNECTION_STRING` (or `ConnectionStrings:Metadata`) | SQL Server / Azure SQL for the framework's own metadata; absent → InMemory (dev only) |
| `IF_ROLE` | `api` = API only; `worker` = scheduler on; unset = both (local dev). Worker deployment is single-replica by design |
| `Self:BaseUrl` | Base URL injected as `$.env.self_base_url` for workflows that call back into the framework (sample workflow uses it) |
| `<NAME>` per connection `password_env` | Runtime password for `db_query` steps (e.g. `MSSQL_PASSWORD`) |

Secret rules: passwords live in env vars / Kubernetes Secrets only; `*_env` fields in connection
configs store the variable NAME; the API masks password/token/secret/key fields as `********`.

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `NU1100` / "no versions available" on restore | NuGet source missing: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| Health returns `metadataProvider: InMemory` in a K8s/compose deploy | `METADATA_CONNECTION_STRING` not reaching the pod — check ConfigMap/Secret wiring |
| Test host: "The server has not been started" | See `docs/TESTING.md` gotchas (factory pattern, parallelization, stale `ASPNETCORE_URLS`) |
| App binds an unexpected port | `dotnet run` used launchSettings; add `--no-launch-profile` + `ASPNETCORE_URLS` |
| Scheduled workflow fires twice | More than one worker replica; keep `worker` at 1 or move to a K8s CronJob |
| `db_query` step error "Environment variable '...' is not set" | The connection's `password_env` name has no value in the API/worker process env |
