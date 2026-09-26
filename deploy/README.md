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

## 3. Kubernetes (Helm)

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

## Tooling (this machine)

docker/kubectl/helm are not installed locally yet. To enable container builds and
cluster deploys:

```powershell
winget install Docker.DockerDesktop        # then start Docker Desktop
winget install Kubernetes.kubectl
winget install Helm.Helm
winget install Kubernetes.kind             # optional, for local clusters
```
