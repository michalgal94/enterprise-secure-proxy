# Enterprise Secure Proxy

[Repository](https://github.com/michalgal94/enterprise-secure-proxy) | [Build and tests](https://github.com/michalgal94/enterprise-secure-proxy/actions/workflows/ci.yml)

A backend-only Web API using ASP.NET Core, EF Core and SQL Server. Callers select an explicitly configured operation,
while trusted database configuration determines the destination, HTTP method and path.

## How it works

The caller sends an operation key and data. The API looks up that operation, checks its rules, and only then sends a request to the configured destination. If a check fails, nothing is sent upstream.

I treated `urlId` as the destination's numeric ID because its meaning isn't fully defined in the assignment. It must match the operation's destination and is removed before forwarding. It is never used as a URL.

The main parts are request validation, forwarding and audit logging. [DESIGN-NOTES.md](DESIGN-NOTES.md) explains the decisions behind them. [DEFECT-REPORT.md](DEFECT-REPORT.md) lists the bugs along with their fixes, and [AI-USAGE.md](AI-USAGE.md) covers the tools used.

## Repository layout

```text
EnterpriseSecureProxy.sln
src/SecureProxy/
  Models.cs                 Entities, DbContext and contracts
  Policy.cs                 Parameter validation, URI construction, AES-GCM
  ProxyExecutor.cs          Audit, forwarding, deadlines, bounded reads
  ConfigurationAdmin.cs     Optimistic concurrency and history
  ConfigurationCatalog.cs   Optional bounded cache and invalidation
  Security.cs               JWT validation and local demo authentication
  ProxyRateLimiting.cs       Per-caller quota and shared concurrency cap
  DestinationCircuitBreaker.cs  Per-destination failure isolation
  Observability.cs          Safe errors, metrics and activity tracing
  Program.cs                Endpoints, authentication, dependency setup
  Migrations/               SQL Server migrations
tests/SecureProxy.Tests/    Recording-handler, HTTP and SQL tests
mock/MockDestination/        Local HTTPS upstream
docs/schema.sql            Idempotent SQL migration script
scripts/Encrypt-Payload.ps1  Client encryption example (PowerShell 7)
.github/workflows/ci.yml     Build and tests with SQL Server
```

## Prerequisites

- .NET 8 SDK in the 8.0.4xx feature band. `global.json` selects its latest installed patch. Tested with SDK 8.0.428 and runtime 8.0.34.
- SQL Server 2022, Windows SQL Server Express LocalDB, or Docker with Linux containers. Docker is only required for the compose option.
- PowerShell for the examples. The encryption script requires PowerShell 7 (`pwsh`).
- Visual Studio with .NET 8 support, or another editor and the CLI.
- NuGet access during restore. EF Core and ASP.NET Core test packages are pinned to 8.0.34.

If .NET is installed in your user profile but isn't on PATH:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --info
```

## 1. Build and run checks

From the repository root:

```powershell
dotnet restore
dotnet tool restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet dev-certs https --trust
```

Trust the development certificate used by both applications. Outbound TLS uses normal certificate validation, don't disable it to make the example work.

SQL integration requires `PROXY_TEST_SQL`,
otherwise those tests are explicitly skipped. See [verification results](docs/VERIFICATION.md).

## 2. Configure SQL and secrets

Use shell environment variables or an approved secret store, never committed secrets. These generated values are local demo credentials:

```powershell
$env:MSSQL_SA_PASSWORD = 'Local!' + [guid]::NewGuid().ToString('N')
$env:ProxyApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:AdminApiKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$env:ConnectionStrings__Proxy = "Server=localhost,1433;Database=SecureProxy;User Id=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
docker compose up -d sql
docker compose logs sql
```

Wait for `SQL Server is now ready for client connections`. Retain the original password: the volume preserves the database and its password. Changing an environment variable doesn't change an existing SQL login.

Alternatively, use an existing SQL Server instance or the Windows LocalDB option below.

### Windows option: install LocalDB

LocalDB is optional. The project needs a SQL Server database, but it can use LocalDB, a regular SQL Server instance or the Docker setup above. The full SQL-backed run has already passed in GitHub Actions.

Install **SQL Server 2022 Express LocalDB** using the [Microsoft instructions](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb?view=sql-server-ver16). Select LocalDB when downloading SQL Server Express media.

For installation, open PowerShell from the Start menu using **Run as administrator**, approve the Windows prompt with an existing administrator account, and run:

```powershell
msiexec /i "C:\path\to\SqlLocalDB.msi"
```

Replace the path with the downloaded installer. If you don't have administrator credentials, the computer's administrator or IT team needs to approve the installation. You don't need to permanently change your account type. After installation, close the elevated terminal and use your normal account for the following steps.

After installation, start an instance and set the connections in PowerShell:

```powershell
$localDb = Join-Path $env:ProgramFiles 'Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe'
if (-not (Test-Path $localDb)) { throw 'Install SQL Server 2022 Express LocalDB first.' }
& $localDb info MSSQLLocalDB
if ($LASTEXITCODE -ne 0) { & $localDb create MSSQLLocalDB }
& $localDb start MSSQLLocalDB
$env:ConnectionStrings__Proxy = 'Server=(localdb)\MSSQLLocalDB;Database=SecureProxy;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:PROXY_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Database=SecureProxyTests;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
```

Keep the proxy/admin keys from the setup above. Continue with initialization below, then run `dotnet test --configuration Release` to include the SQL tests. LocalDB replaces the Docker database. it doesn't need an `sa` login or a SQL password.

Use different proxy/admin keys of at least 32 characters. `sa` and `TrustServerCertificate=True` are local demo choices. Production requires a validated SQL certificate, a restricted application account and a separate migration identity.

## 3. Initialize schema and demo configuration

In the terminal holding the connection string:

```powershell
dotnet run --project src/SecureProxy -- --initialize
```

This applies migrations, seeds the demo when no destinations exist, then exits. It also creates the `local-demo` caller with a grant for `customer.get` when that operation exists and the caller hasn't been created. It leaves an existing caller's state and grants alone. Schema changes are not automatically applied by every application instance. The demo operation forwards `GET customers/{id}` to `https://localhost:7443/`.

The destination ID is normally 1 in a fresh database. Otherwise query `SELECT Id, Name FROM Destinations` and use the actual ID in `json.urlId`.

Schema-only alternatives: `dotnet ef database update --project src/SecureProxy` or execute [docs/schema.sql](docs/schema.sql) using a SQL tool. Don't run all alternatives. Schema-only deployment doesn't seed demo configuration.

## 4. Start mock and proxy

In a separate terminal:

```powershell
dotnet run --project mock/MockDestination -- --urls https://localhost:7443
```

In the terminal holding the original connection string and keys:

```powershell
dotnet run --project src/SecureProxy -- --urls https://localhost:7444
```

Use a third terminal for client calls, setting `ProxyApiKey` to the same original value used by the proxy. For admin calls, use the original `AdminApiKey` too. Environment variables are local to a shell and its child processes. Generating replacement keys in a new client shell will not authenticate.

`AllowedHosts` is `localhost`. Set approved deployment hostnames explicitly. HTTPS is required. TLS termination behind a reverse proxy needs an explicitly trusted forwarded-header configuration, which is not enabled here.

## 5. Execute an operation

With the original proxy key in the client shell:

```powershell
$headers = @{ 'X-Api-Key' = $env:ProxyApiKey; 'Accept-Language' = 'he' }
$body = @{ clientId = 'customer.get'; json = @{ urlId = 1; id = 12 } } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri https://localhost:7444/api/proxy/execute -Headers $headers -ContentType application/json -Body $body
```

Expected JSON: `{"id":12,"name":"Assessment Customer"}`. Responses include a server-generated `X-Correlation-Id`, `X-Proxy-Source`, `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.

Try adding `destinationUrl` to the envelope, removing `id`, or changing `urlId`: each must be blocked before dispatch. Authorization, cookies and Host are never forwarded, even if mistakenly configured in an allowlist.

### Contract and parameters

```json
{"clientId":"customer.get","json":{"urlId":1,"id":12}}
```

- Names are case-sensitive. Unknown envelope fields and duplicate properties are rejected.
- `urlId` is a required integer matching the configured destination; it is stripped before forwarding.
- Supported rule types: `string`, `integer`, `number`, `boolean`. Numeric strings are not converted.
- Path values permit ASCII letters, digits, `_` and `-`. A path parameter is always needed to complete its template.
- Query names/values are escaped. Body parameters form a new JSON object.
- Unknown parameters are rejected by default. When explicitly allowed, they go only into the body and cannot affect routing. GET/HEAD have no body.
- The encrypted variant has `encrypted` instead of `json`; even `json:null` is rejected. Authenticated plaintext includes `urlId` and operation parameters.

## 6. Update configuration and handle concurrency

In the local demo, management uses a separate key. In JWT mode it requires the signed `proxy.admin` role. Endpoints read operation state and change `Enabled`; they don't expose destination creation or URL overrides.

```powershell
$adminHeaders = @{ 'X-Api-Key' = $env:AdminApiKey }
$op = Invoke-RestMethod https://localhost:7444/api/admin/operations/customer.get -Headers $adminHeaders
$update = @{ enabled = $false; version = $op.version } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri https://localhost:7444/api/admin/operations/customer.get/enabled -Headers $adminHeaders -ContentType application/json -Body $update
```

Reusing the old version returns 409. To re-enable, read the current version and send `enabled=true`. SQL generates rowversion and EF includes the expected version in the UPDATE predicate, covering races after the initial check. The state change and history, including the authenticated actor, commit atomically. A successful update also invalidates this instance's configuration cache.

Other configuration is managed through controlled SQL/migrations by an authorized operator. Direct SQL changes need DBA/deployment auditing. Application history covers the exposed state-update endpoint. Update related configuration within a transaction.

## 7. Demonstrate encryption

AES-256-GCM protects input in addition to TLS. SQL holds references, not secret bytes. AAD is the exact operation key encoded as UTF-8.

After initialization, execute [docs/enable-demo-encryption.sql](docs/enable-demo-encryption.sql). In PowerShell 7, generate a key **before starting/restarting the API from that shell**:

```powershell
$env:PROXY_KEY_LOCAL = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Use the same key in the client PowerShell 7 session:

```powershell
$encryptedBody = ./scripts/Encrypt-Payload.ps1 -ClientId customer.get -Json '{"urlId":1,"id":12}'
Invoke-RestMethod -Method Post -Uri https://localhost:7444/api/proxy/execute -Headers $headers -ContentType application/json -Body $encryptedBody
```

The envelope has base64 `nonce` (12 bytes), `ciphertext` and `tag` (16 bytes). Invalid tags/ciphertext return 400 with zero upstream calls. Missing/invalid keys return 503. Nonces must never repeat with the same key. GCM doesn't prevent replay: use a replay ledger or upstream idempotency where required. Responses are not application-encrypted and payloads are not persisted.

Restore the plaintext demo with `UPDATE Operations SET EncryptionRequired=0 WHERE [Key]=N'customer.get'`.

If configuration caching is enabled, invalidate it after either direct SQL encryption update, or wait for its TTL. See the cache instructions below.

## Caller identities and permissions

`Security:Mode=DevelopmentKey` is the default so the assessment can run without an identity-provider account. `ProxyApiKey` identifies `local-demo`, and `AdminApiKey` identifies `local-admin` with the admin role. Neither identity can execute an operation without a matching, enabled SQL caller and explicit grant. Initialization grants only `local-demo` access to `customer.get`.

For an existing OpenID Connect identity provider, set these before starting the API:

```powershell
$env:Security__Mode = 'Jwt'
$env:Security__Authority = 'https://your-issuer.example.com'
$env:Security__Audience = 'your-proxy-api-audience'
```

Use the actual issuer and audience registered for this API. The provider must issue signed access tokens with a stable, non-empty `sub` of at most 128 characters. Signature, issuer, audience and expiry are validated using the provider's HTTPS discovery and signing keys. Admin tokens need a `roles` claim containing `proxy.admin`. JWT mode doesn't accept the demo API keys as a fallback.

Use [docs/grant-caller.sql](docs/grant-caller.sql) to create an enabled caller and grant one configured operation. Set its subject to the token's exact `sub` and its operation key to the intended operation. A valid token alone doesn't grant execution access. To revoke access, delete that grant or set the caller's `Enabled` to 0; these checks read SQL on every execution, even when configuration caching is enabled. An already authorized, in-flight request may still finish.

```powershell
$headers = @{ Authorization = "Bearer $env:PROXY_ACCESS_TOKEN" }
Invoke-RestMethod -Method Post -Uri https://localhost:7444/api/proxy/execute -Headers $headers -ContentType application/json -Body $body
```

Obtain the token using your provider's supported flow. The repository doesn't create an identity-provider tenant or store access tokens. Signed-token tests cover accepted and rejected tokens, roles, separate caller quotas and grant revocation; a live provider still needs deployment configuration.

## Cache and resilience settings

The defaults are in `src/SecureProxy/appsettings.json`. Environment overrides use `__` between sections and setting names.

| Setting | Default | Behavior |
|---|---:|---|
| `ConfigurationCache:Enabled` | `false` | Fresh operation/destination/rule lookup per execution |
| `ConfigurationCache:TtlSeconds` | `10` | Absolute expiry when enabled; allowed range 1-60 seconds |
| `ConfigurationCache:Capacity` | `256` | Maximum cached operations; unknown keys aren't cached |
| `Resilience:RequestsPerMinute` | `60` | Fixed one-minute window per authenticated subject |
| `Resilience:MaxConcurrency` | `32` | Active proxy executions per instance, with no queue |
| `Resilience:CircuitBreakerEnabled` | `true` | Separate Polly circuit for each configured destination |
| `Resilience:MinimumThroughput` | `5` | Minimum calls in the sampling window before opening |
| `Resilience:FailureRatio` | `0.5` | Open when at least half the sampled calls fail |
| `Resilience:SamplingSeconds` | `30` | Failure sampling window |
| `Resilience:BreakSeconds` | `15` | Open period before a recovery probe |

Caching is available, but off by default: the assessment doesn't show a load problem that justifies accepting stale configuration. To enable it, set `$env:ConfigurationCache__Enabled = 'true'` before startup. Successful admin state changes clear the local cache after commit. Direct SQL changes can be made visible locally with:

```powershell
Invoke-RestMethod -Method Post -Uri https://localhost:7444/api/admin/configuration-cache/invalidate -Headers $adminHeaders
```

Other instances see changes after expiry, unless each is invalidated separately. Loads already in progress may use their previous snapshot; invalidation prevents them from putting it back into this instance's cache. Disabling an operation isn't an immediate kill switch. Caller state and grants are never cached.

Both quota and concurrency rejection return 429 before dispatch. Time-window rejections include `Retry-After`; a concurrency rejection has no reliable retry time. The circuit counts connection/body failures, destination timeouts, oversized upstream responses, upstream 408 and 5xx. It ignores caller cancellation, policy rejection and other upstream 4xx. An open circuit returns 503 without sending; Polly admits a recovery probe after the break. All limits and circuit state are per process. Multiple instances need coordinated ingress limits if a shared quota is required. No policy retries a request.

## Errors and resource limits

Errors use `{ "code": "...", "correlationId": "..." }`, including authentication, throttling, management and unknown-route errors. `X-Proxy-Source=upstream` identifies an actual upstream response. `proxy` identifies a locally generated failure.

| Condition | HTTP | Could the upstream receive the request? |
|---|---:|---|
| Invalid envelope, parameters or ciphertext | 400 | No |
| Unknown operation | 404 | No |
| Disabled operation/destination | 403 | No |
| Unknown, disabled or ungranted caller | 403 | No |
| Missing/invalid authentication | 401 | No |
| Unsupported content type | 415 | No |
| Request exceeds limits | 413 | No |
| Invalid trusted configuration | 503 | No |
| Audit admission failure | 503 | No |
| Caller quota or concurrency limit | 429 | No |
| Destination circuit open | 503 | No |
| Upstream deadline | 504 | Possibly; it might still complete |
| Connection failure | 502 | The outcome can be ambiguous |
| Oversized response | 502 | Yes |
| Upstream error/redirect | Original status | Yes |
| Caller cancellation | 499 in audit | Depends on the phase; caller may be gone |
| Audit completion failure after dispatch | 503 | Yes; no upstream rollback |
| Stale configuration version | 409 | Management only |

Upstream error/redirect bodies and headers are discarded to prevent disclosure. No Location or Set-Cookie is copied. Successful content passes through as JSON for application/json, otherwise as application/octet-stream. Authorized destination owners remain responsible for the business data in successful responses.

The envelope hard cap is 1 MiB, with per-operation caps. Authenticated plaintext must also fit. Base64 overhead reduces usable encrypted size. Responses are capped per operation up to 8 MiB. Chunked reads are bounded even without Content-Length. A response is buffered within the cap before emitting headers, allowing consistent 502 errors. Temporary copies increase peak memory. At most 32 proxy requests per instance execute, with no waiting queue.

No automatic retries are configured. A timeout or post-dispatch 503 is not proof a POST did not execute. When the upstream supports idempotency, explicitly allow Idempotency-Key and reuse it on retries. Forwarding that header alone doesn't implement deduplication.

## Audit and consistency

- Every attempt admitted into the executor requires durable SQL intent before forwarding. Successes, rejections and failures get completion metadata.
- HTTPS, authentication and rate/concurrency rejections emit structured metadata and metrics outside ExecutionLog. Host rejection belongs to server/ingress logs. Body logging is not enabled.
- Dispatching is persisted before Send, with Forwarded=false. Completed rows mark Forwarded=true only for a dispatch attempt. An unfinished Dispatching row is ambiguous even when Forwarded=false: the process may have sent the request before saving completion. No flag proves upstream receipt.
- Admission failure blocks dispatch. Completion-write failure emits Critical metadata-only telemetry and returns 503, retaining the prior intent for reconciliation. There is no distributed exactly-once guarantee.
- Configuration caching is optional and off by default. Caller state/grants always read SQL. Already authorized/in-flight requests may finish after disabling configuration or revoking access. See DESIGN-NOTES for consistency and revocation semantics.
- Logs omit bodies, credentials, cookies, key bytes and exception messages. Operation keys are recorded only after recognition. Configure retention, access control and alerts for unfinished audits in deployment.

Console logs are structured JSON with UTC timestamps. Audit rows also include recognized caller and trace IDs; configuration history includes the administrator's identity. The `SecureProxy` Meter publishes execution count/duration, ingress rejections and circuit transitions with fixed, low-cardinality tags. The `SecureProxy` ActivitySource provides execution spans when a listener is attached. These are ready for a collector, but the repository doesn't deploy a monitoring backend. The protected `GET /api/admin/health` checks SQL connectivity without exposing connection details.

## Automated verification

All **98 tests passed with no failures or skips** against SQL Server 2022 in [GitHub Actions](https://github.com/michalgal94/enterprise-secure-proxy/actions/runs/43491907864). The standalone SQL-backed HTTPS check passed too. The local run passes 96 tests and explicitly skips the two SQL tests when `PROXY_TEST_SQL` isn't set. See [VERIFICATION](docs/VERIFICATION.md) for the recorded revision and results.

```powershell
dotnet test --configuration Release --logger trx
$env:PROXY_TEST_SQL = "Server=localhost,1433;Database=SecureProxyTests;User Id=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
dotnet test --configuration Release --filter FullyQualifiedName~SqlServerTests
```

With LocalDB, use an integrated-security connection string targeting a dedicated test database. SQL tests apply migrations, create unique records and clean up their own data. Never target a production database.

The GitHub workflow also starts the standalone proxy and mock with SQL Server, then checks valid and blocked requests, live grant revocation with the cache enabled, explicit invalidation, stale configuration updates, encrypted input, circuit opening and saved audit/actor records. The Linux runner uses `.github/scripts/smoke.sh` for this check.

The recording handler captures every outgoing method, URI, header and body. Pre-forward rejection tests assert no received calls. TestServer checks middleware/endpoints, SQL tests check provider-specific concurrency/history. EF InMemory doesn't prove SQL semantics or migration compatibility.

See [VERIFICATION](docs/VERIFICATION.md) for test results and remaining checks, and [REQUIREMENTS-MAP](docs/REQUIREMENTS-MAP.md) for coverage.

## Main decisions

- **Routing comes from configuration** The caller can't choose the host, method or path. Redirects are disabled for the same reason.
- **Requests are checked before sending** Tests also check that rejected requests never reach the destination.
- **Bodies have size limit** Input is validated in full, and responses are buffered up to a fixed limit. This makes error handling predictable, but uses more memory than full streaming.
- **No automatic retries** A timeout doesn't mean a POST wasn't processed. Retrying safely needs support from the destination, such as an idempotency key.
- **Cache only when useful** Caching is implemented but off by default. It has a size cap, short TTL and explicit invalidation. Caller permissions remain uncached.
- **SQL handles concurrent updates** Rowversion prevents one administrator from silently overwriting another's changes.

The [security and reliability trade-offs](DESIGN-NOTES.md#security-and-reliability-trade-offs) table explains the costs too, including SQL availability, memory use, stale configuration and ambiguous outcomes after dispatch.

## Current improvements

- **Stricter input checks** Unknown fields, duplicate parameters, invalid types and path traversal attempts are rejected before forwarding.
- **Safer forwarding** Only approved headers are sent. Credentials, cookies and hop-by-hop headers stay out, and redirects aren't followed.
- **Better failure handling** Upstream timeouts, broken connections and caller cancellation have separate outcomes. The timeout covers reading the response body too.
- **More useful audit records** The API saves an intent before sending, records the result afterwards, and doesn't mark a request as forwarded when the intent write fails. Destination updates also refresh their timestamps.
- **Broader tests** The tests check what the destination actually receives, including a real local HTTPS call and the 32-request concurrency limit. CI also runs the SQL integration tests and a full SQL-backed HTTPS check.
- **Permissions per caller** JWT mode validates access tokens, then SQL grants decide which operations each caller can use. Token validation and permission revocation have separate tests.
- **Outage protection** Per-caller quotas and per-destination circuits reject excess or unhealthy traffic without adding retries.
- **Optional cache and telemetry** Bounded configuration caching has tested expiry/invalidation. Metrics, execution spans, JSON logs and protected SQL readiness are available for deployment monitoring.

## Future improvements

- **Connect the deployment identity provider** JWT support and operation grants are implemented. Register the deployed API, configure its real issuer/audience and agree on token roles and lifetimes.
- **Move keys to a secret manager** The key-source interface is already there. The next step is managed storage and a clear rotation process.
- **Connect monitoring and alerts** Export the existing metrics and spans, then alert on latency, rejections, open circuits and unfinished audit records.
- **Coordinate multiple instances** Add shared ingress quotas and invalidation if deployment needs a common limit or faster cross-instance updates.
- **Measure before tuning** Load-test memory, quota and breaker thresholds before enabling caching or changing response buffering.

## Deliverables and submission

Public repository: **https://github.com/michalgal94/enterprise-secure-proxy**

- [Visual Studio solution](EnterpriseSecureProxy.sln)
- [SQL script](docs/schema.sql) and [EF migrations](src/SecureProxy/Migrations)
- [Automated tests](tests/SecureProxy.Tests/SecureProxy.Tests.csproj)
- This README, [DESIGN-NOTES](DESIGN-NOTES.md),
  [DEFECT-REPORT](DEFECT-REPORT.md), [AI-USAGE](AI-USAGE.md)
- [Word submission document](docs/SUBMISSION.docx)

## References

- [Microsoft: IHttpClientFactory](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-8.0)
- [Microsoft: EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)
- [Microsoft: AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-8.0)
- [Microsoft: LocalDB](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb)
