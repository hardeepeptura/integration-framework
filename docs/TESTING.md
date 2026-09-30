# Testing Process

## How to run

```powershell
# Full suite (must print: total: 94, failed: 0)
$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')
dotnet test backend/IntegrationFramework.slnx --nologo

# Single class
dotnet test backend/IntegrationFramework.slnx --filter "FullyQualifiedName~WorkflowExecutorTests"

# Frontend typecheck + production build (part of test gate)
cd frontend
npm run build        # runs tsc -b && vite build
```

No external services are required: the metadata store uses the EF Core InMemory provider in
tests, outbound HTTP is served by a scripted `IHttpClientFactory`, and DB adapters are tested
via config/parameter-binding units (no live databases).

## Suite layout (`backend/tests/IntegrationFramework.Tests`)

| File | Covers |
|---|---|
| `PathResolverTests` | `$.input / $.env / $.steps` resolution, array indexing, brace interpolation, missing-path nulls |
| `WorkflowValidatorTests` | trigger-first rule, unknown types, duplicate ids, required config, connection/branch references |
| `WorkflowExecutorTests` | run lifecycle, condition branches, loop-per-iteration StepRuns, retry attempts, `onError: continue`, persistence |
| `HttpAndAuthTests` | HTTP node request/response mapping, error surfaces, api_key/bearer/basic auth application from env vars, secret masking |
| `DbConnectorTests` | per-engine connection strings, default ports, password_env resolution errors, `:name → @pN` parameter binding, client factory |
| `SchedulerPolicyTests` | schedule extraction from graph JSON, due computation |
| `ApiIntegrationTests` | end-to-end through `WebApplicationFactory`: health, workflow CRUD + validate + run, webhook trigger, connections CRUD + masking + test-connection, run detail/rerun, demo systems |
| `OAuth2TokenManagerTests` | client_credentials + refresh_token grants (form fields, env secrets), token caching, near-expiry refresh, failure surfaces |
| `EntityMappingApplierTests` | field mapping ($./relative sources, defaults, transforms) + validation rules (type/required/length/range/pattern) |
| `P0ApiTests` | entity-mapping CRUD + validate, mapping node in a run, OAuth2 connection test (masked secret, token acquisition), webhook event persistence + replay + rejections |
| `MinimalFactoryReproTests` | guards the test-host startup pattern (see gotchas) |

## Verified end-to-end (live processes)

Run the real stack and exercise the sample workflow (webhook → transform → inventory reserve):

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:8000'
Start-Process dotnet -ArgumentList 'run','--project','backend/src/IntegrationFramework.Api','--no-build','--no-launch-profile' -WindowStyle Hidden
# wait for http://localhost:8000/health -> {"status":"ok",...}
$wf  = Invoke-RestMethod http://localhost:8000/api/workflows
$run = Invoke-RestMethod -Method Post -Uri "http://localhost:8000/webhook/$(($wf | Where-Object name -match 'Sample')[0].id)" `
        -ContentType 'application/json' -Body '{"sku":"WIDGET-1","quantity":3,"name":"Hardeep"}'
# expect: run.status = success; run.output.body.remaining = 97 (100 - 3)
Stop-Process -Id $GPID # and the IntegrationFramework.Api child process
```

Frontend (built bundle + proxy path):

```powershell
cd frontend; npm run build; npm run preview -- --port 4173
# http://localhost:4173/            -> SPA (title "Neuro-Eptura", #root, bundle script)
# http://localhost:4173/api/workflows -> proxies to :8000 and returns the seeded workflow
```

Last verified 2026-09-26: suite 78/78; live webhook run success (reserved 3, remaining 97);
UI + proxy 200. Phase 2 (P0): suite grew to 94/94 (OAuth2, entity mappings, webhook durability).

## Gotchas learned here (do not re-learn the hard way)

1. **Do not use the `ConfigureWebHost` override** on a `WebApplicationFactory` subclass with
   .NET 10's deferred test host — the server never starts ("The server has not been started or
   no web application was configured"). The working pattern: a **plain fixture class** holding a
   bare `new WebApplicationFactory<Program>()` plus `WithWebHostBuilder(...)` and
   `CreateClient()` on the derived factory. `MinimalFactoryReproTests` exists to catch
   regressions of this quickly (run it in isolation when the host mysteriously won't start).
2. **Test parallelization must stay disabled** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`
   in `TestSetup.cs`) — concurrent deferred-host initialization is racy.
3. **A stale `ASPNETCORE_URLS` env var** in the shell makes the test host try to bind a Kestrel
   port in-process and can break startup. `Remove-Item Env:ASPNETCORE_URLS` if you ever set it
   for a manual `dotnet run`.
4. **`dotnet run` prefers `launchSettings.json`** over `ASPNETCORE_URLS`. For a deterministic
   port use `--no-launch-profile` together with the env var.
5. **When a node makes an HTTP call inside the engine, the request (and its content) is disposed
   when the node returns** — capture request bodies inside the responder lambda in tests, not after.
6. **`ConnectionDto.AuthConfig` is a masked JSON string** in API responses (secrets are
   `********`); parse it before asserting on fields.
7. Machine setup that bit us once: `dotnet nuget list source` was empty — add
   `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` before any restore.
