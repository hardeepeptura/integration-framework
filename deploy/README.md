# Deployment Guide

## 1. Local development (no containers)

Backend (uses InMemory metadata store when no connection string is set):

```powershell
cd backend
dotnet run --project src/IntegrationFramework.Api
# API on http://localhost:8000  (launch profile default; /health to verify)

# Against SQL Server / Azure SQL:
$env:METADATA_CONNECTION_STRING = "Server=<host>,1433;Database=<db>;User Id=<user>;Password=<password>;TrustServerCertificate=True"
dotnet run --project src/IntegrationFramework.Api
```

Frontend:

```powershell
cd frontend
npm install
npm run dev     # http://localhost:5173, proxies /api /webhook /demo to :8000
```

## 2. Docker Compose (containers, no Kubernetes)

```powershell
cd deploy
copy docker\.env.example docker\.env   # then edit MSSQL_SA_PASSWORD
docker compose up -d --build
# UI: http://localhost:8080   API: http://localhost:8000/health
```

Services: `api` (engine API), `worker` (same image + `IF_ROLE=worker` → runs the scheduler),
`frontend` (nginx SPA + `/api` proxy), `mssql` (metadata store, volume-backed).

## 3. CI/CD (GitHub Actions)

- **CI** (`.github/workflows/ci.yml`) runs on every push to `main` and every PR: backend
  build + full xUnit suite, frontend `tsc -b && vite build`.
- **Release** (`.github/workflows/release.yml`) runs on `v*` tags (e.g. `git tag v0.2.0 && git push origin v0.2.0`):
  test gate → build & push `backend` / `frontend` images to
  `ghcr.io/hardeepeptura/integration-framework/{backend,frontend}` (tags: `0.2.0`, `0.2`, `latest`)
  → creates the GitHub Release with auto-generated notes.
  First-time setup: make the two GHCR packages public (repo → Packages → package settings →
  Change visibility) if clusters should pull without auth.

### Database migrations

The SQL Server schema ships as an EF Core migration (`IntegrationFramework.Core/Migrations`),
verified via `dotnet ef migrations script --idempotent`. The **api** container applies pending
migrations on startup (`Database.Migrate()`); **worker** containers skip it so concurrent
starts don't race on `__EFMigrationsHistory`. The Helm chart needs no migration job.

## 4. Kubernetes (Helm)

Prerequisites: docker, kubectl, helm installed locally (not currently installed on this
machine — see "Tooling" below).

### Build and load images (kind)

```powershell
# From the repository root:
docker build -f deploy/docker/Dockerfile.backend  -t integration-api:local .
docker build -f deploy/docker/Dockerfile.frontend -t integration-frontend:local .
kind load docker-image integration-api:local integration-frontend:local
```

### Install

```powershell
kubectl create namespace integration
helm install integration deploy/helm/integration-framework `
  -n integration `
  -f deploy/helm/integration-framework/values-kind.yaml
```

Secrets are never stored in values: pass `metadata.existingSecret` (a Secret with key
`connection-string`) or create secrets out-of-band:

```powershell
kubectl -n integration create secret generic integration-metadata `
  --from-literal=connection-string="Server=...;Database=...;User Id=...;Password=...;TrustServerCertificate=True"
kubectl -n integration create secret generic integration-secrets `
  --from-literal=mssql-password="..."
```

### AKS

1. `az acr build --registry youracr --image integration-api:latest -f deploy/docker/Dockerfile.backend .`
2. `az acr build --registry youracr --image integration-frontend:latest -f deploy/docker/Dockerfile.frontend .`
3. Point `values-aks.yaml` image repositories at your ACR; create the metadata + secrets
   (see file comments); install with `-f values-aks.yaml`.
4. Install ingress-nginx + cert-manager if TLS is enabled.

### SSO (corporate login via Microsoft Entra ID)

The login mechanism mirrors the legacy DevOpsAutomateHub portal (`cloudopsautomated`):
OIDC against `https://login.microsoftonline.com/{tenant}/v2.0` using the **same corporate
app registration**, with a sliding cookie session. No client secret is required — the
legacy app uses the implicit `id_token` flow. If `SSO_CLIENT_SECRET` is set, the API
switches to the more secure authorization-code flow instead.

Copied configuration (same values as the legacy portal's Web.config `ida:*` keys):

| Setting | Value |
|---|---|
| Tenant ID | `adb48caa-0b14-4256-b178-a5e508c807f5` (CondecoSoftware/Eptura corporate tenant) |
| Client ID | `f1fa82b5-7798-4d68-8bb6-cac54eb3ddeb` (DevOpsAutomateHub app registration) |
| Redirect URI | `http://localhost:8080/auth/callback` (must be added to the app registration) |
| Flow | implicit `id_token` (no secret) or authorization-code (with `SSO_CLIENT_SECRET`) |

Local run with SSO enabled:

```powershell
$env:SSO_TENANT_ID = "adb48caa-0b14-4256-b178-a5e508c807f5"
$env:SSO_CLIENT_ID = "f1fa82b5-7798-4d68-8bb6-cac54eb3ddeb"
dotnet run --project src/IntegrationFramework.Api
```

Behavior matches the legacy portal: AJAX calls get `401` instead of a cross-origin
redirect; `preferred_username` is shown as the signed-in name; session is sliding,
`SSO_SESSION_TIMEOUT_MINUTES` (default 60).

In Kubernetes, set `sso.enabled=true, sso.tenantId=..., sso.clientId=...` on the Helm
release (client secret optional via `sso.existingSecret`).

### Scheduled triggers

The worker Deployment is intentionally **single-replica** (`Recreate` strategy) — the
scheduler runs in-process and would double-fire across replicas. For higher volume,
replace it with a Kubernetes CronJob hitting `POST /api/workflows/{id}/run`.

## Environment variables

| Variable | Purpose | Set by |
|---|---|---|
| `METADATA_CONNECTION_STRING` | SQL Server / Azure SQL connection string for the metadata store | Compose / Helm |
| `IF_ROLE` | `api` = API only (no scheduler); `worker` = scheduler on; unset = both (local dev) | Compose / Helm |
| `<CONNECTION>_PASSWORD` env vars | Resolved for `db_query` steps; names come from each connection's `password_env` (e.g. `MSSQL_PASSWORD`) | env / K8s Secret |
| `SSO_TENANT_ID` / `SSO_CLIENT_ID` | Enable corporate SSO (Entra ID) when both are set — see SSO section | Compose / Helm |
| `SSO_CLIENT_SECRET` | Optional; enables authorization-code flow instead of implicit `id_token` | K8s Secret |
| `SSO_SESSION_TIMEOUT_MINUTES` | Sliding cookie session timeout, default 60 | Compose / Helm |

## Tooling (this machine)

docker/kubectl/helm are not installed locally yet. To enable container builds and
cluster deploys:

```powershell
winget install Docker.DockerDesktop        # then start Docker Desktop
winget install Kubernetes.kubectl
winget install Helm.Helm
winget install Kubernetes.kind             # optional, for local clusters
```
