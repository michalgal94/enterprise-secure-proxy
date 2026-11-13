# Design notes - Architecture and technical decisions

## Scope and trust model

The API only forwards configured operations. `urlId` is treated as the configured destination ID: it must be present in the validated data and match the operation's destination. It's never used as a URL.

The administrator/DBA, deployment settings, destination definitions and DNS are trusted. Caller input and upstream responses are untrusted. SQL access must be restricted. Authentication supports an external OpenID Connect provider through signed JWT access tokens. SQL separately controls which enabled caller can execute each operation. A signed `roles=proxy.admin` claim permits administration; it doesn't automatically grant operation execution.

The default `DevelopmentKey` mode keeps the local setup independent of an external provider: separate keys identify `local-demo` and `local-admin`. `Jwt` mode validates issuer, audience, signature and expiry, requires HTTPS discovery and never falls back to a demo key. One authority is configured, and SQL identities use its exact `sub`. Caller/grant columns use binary collation; an additional ordinal comparison prevents SQL's trailing-space equality from accepting a different subject. Use opaque service subjects instead of email addresses. A real identity-provider tenant and its access-token policies are deployment configuration, not hosted by this repository.

```mermaid
sequenceDiagram
  participant C as Caller
  participant A as ASP.NET pipeline
  participant E as ProxyExecutor
  participant P as RequestPolicy
  participant D as SQL Server
  participant U as Configured upstream
  C->>A: HTTPS + credentials + operation envelope
  A->>E: Authenticated request, within quota/concurrency limits
  E->>D: Persist intent
  E->>D: Load operation/rules on cache miss; always check caller grants
  E->>P: Authenticate/decrypt and validate all data
  P-->>E: Prepared URI/method/body or rejection
  alt All pre-forward checks passed
    E->>D: Persist dispatch intent
    E->>U: Circuit permits send once, linked timeout/cancellation
    U-->>E: Bounded response
  end
  E->>D: Persist outcome independently of caller disconnect
  E-->>C: Safe response + correlation
```

## Responsibilities

`Models.cs` holds the relational model and contracts. `RequestPolicy` has no network side effects: it converts trusted configuration plus the untrusted envelope into a prepared request or a categorized rejection. Its only external dependency is the key source. `ProxyExecutor` owns authorization, execution and audit writes. `OperationCatalog` loads configuration through the optional bounded cache. `DestinationCircuitBreaker` isolates destination failures. `Security` and `ProxyRateLimiterSetup` handle identities and admission limits. `Program` wires these parts to HTTP endpoints. `ConfigurationAdmin` implements the small configuration mutation use case.

I kept one application project because the current scope doesn't need separate assemblies or a mediator. EF Core already handles the unit of work. Tests cover the executor, HTTP pipeline and SQL-specific behavior.

## Routing and SSRF

Only configured methods GET/POST/PUT/PATCH/DELETE/HEAD are supported. Destination URLs must use HTTPS, contain no userinfo/query/fragment, end with `/`, and match their configured permitted port. Relative templates cannot contain traversal segments, percent escapes, backslashes, schemes, queries or fragments. Path values use an intentionally narrow ASCII identifier alphabet. query values are escaped separately. The completed URI is checked for authority and base-path containment. Dynamic path templates must be completely resolved.

Redirects, cookies, automatic decompression and system proxies are disabled on SocketsHttpHandler. The request carries only headers present in both the operation allowlist and a hard safety allowlist (Accept-Language, X-Request-Id, Idempotency-Key). Headers nominated by an incoming Connection header are excluded as hop-by-hop, even if otherwise allowlisted. No caller Host, credentials, connection headers, cookies or API keys are copied. There is no user-controlled authorization profile in this implementation. Response headers are not copied.

Internal destinations are allowed because they are an explicit business requirement. This implementation doesn't pin DNS results or enforce IP CIDRs. A compromised trusted configuration or DNS can redirect traffic. production must combine restricted configuration writers, trusted DNS, egress ACLs and optionally a ConnectCallback with organization-specific network allowlists. Blocking all private IPs would break the required internal API scenario.

## Encryption and protection

Profiles contain key references, never key bytes. The environment key source resolves a reference under `PROXY_KEY_` and requires a 32-byte base64 key. It can be replaced by a secret-manager implementation without changing validation. AES-256-GCM uses a 12-byte nonce, 16-byte tag and operation-key UTF-8 AAD. The entire plaintext is authenticated before parsing or forwarding. Temporary plaintext/key arrays are cleared. parsed JSON and request buffers remain managed memory and are not a comprehensive memory-erasure guarantee.

GCM provides integrity and confidentiality, not caller authorization or replay protection. Nonces must not repeat under one key. Each key reference should represent a versioned key. retain old profiles during coordinated rotation, then disable them. There is no automatic key rotation or replay ledger. TLS remains mandatory. Responses are not application-encrypted. No sensitive payloads are persisted. At-rest SQL/storage encryption is a deployment responsibility.

## Resource bounds and lifecycle

The envelope hard limit is 1 MiB, with per-operation limits up to that value. Encrypted requests must fit both the wire-envelope limit and the decrypted-data limit, so base64 overhead reduces usable plaintext size. Responses are limited per operation, never above 8 MiB. Content-Length is checked when present. chunked streams are still limited while reading. Request/response reads use 8 KiB chunks. The bounded result is buffered before emitting response headers so an oversized response can produce a consistent 502. There are temporary array/MemoryStream copies. peak memory is higher than one buffer per request.

By default, each authenticated subject can make 60 proxy requests per fixed one-minute window, and up to 32 proxy requests can execute at once across this instance. Neither limiter queues requests. Excess calls return 429 before execution or dispatch, with Retry-After when a time-window lease supplies it. The concurrency policy is attached to endpoint metadata, so path casing or a trailing slash doesn't remove the limit. These settings are bounded and validated at startup. Fixed windows allow bursts across a window boundary; distributed quotas belong at a coordinated ingress if multiple instances must share one budget.

HttpClientFactory reuses handlers/connections. Client-level timeout is infinite because a linked destination deadline covers send and body read. Upstream connections are bounded per server, connection lifetime is two minutes, connect timeout five seconds, and response headers are capped.

A Polly circuit is kept per configured destination, not per caller or operation. At least five calls in 30 seconds and a failure ratio of 50% open it for 15 seconds. Connection/body failures, destination timeouts, oversized upstream responses, upstream 408 and 5xx count as failures. Other upstream 4xx, caller cancellation and policy rejections don't. An open circuit returns 503 without invoking Send; a half-open probe checks recovery. The whole send/body read is inside the circuit, so receiving headers alone doesn't count as success. No retries are added. Circuit state is local and resets on restart; thresholds need load testing before production. The registry contains trusted destination IDs, so configuration administrators also control its cardinality.

## Reliability and response semantics

Incoming cancellation flows through configuration loading and HTTP I/O. Audit admission and completion each use their own five-second deadline, independent of a disconnected caller. Caller cancellation is recorded as 499, although the disconnected caller may receive no HTTP response. A destination deadline yields 504. connection errors, including an IOException while reading the upstream body, yield 502. An incoming body read failure is a 400. Cancellation can never guarantee that an upstream side effect was undone.

Minimal API binding is configured to throw BadHttpRequestException consistently across environments. The safe exception middleware preserves its 400/415/413 client-error status instead of turning malformed management input into a 503. Exception text is never returned. Invalid base64 management versions are handled by the management use case itself.

Proxy, authentication, quota, management and route errors use the same code/correlationId JSON shape. Missing trusted destination configuration returns 503, disabled configuration returns 403, and stale management writes return 409. A database foreign key normally prevents a missing destination; the policy still defends against a corrupt/incomplete configuration graph. No internal URL, exception message, key details or stack trace is returned. Upstream error statuses remain identifiable as described below.

There are no automatic retries. A safe retry policy would require an actually idempotent operation, a bounded attempt/time budget and appropriate transient failures. Non-idempotent operations require upstream-supported idempotency or durable deduplication. forwarding an allowlisted Idempotency-Key doesn't itself implement deduplication. An error after dispatch is not permission to retry a POST blindly.

Upstream non-success statuses, including redirects, are retained with a generic body and `X-Proxy-Source=upstream`. Their original bodies and headers are discarded. Successful bodies are returned as supplied by the trusted destination. A proxy-generated failure uses `X-Proxy-Source=proxy`. No Location is exposed and no redirect is followed. Only application/json is preserved as a response content type. other content becomes application/octet-stream. This is intentionally not a transparent HTTP proxy.

## Configuration consistency and history

Configuration caching is optional and disabled by default. With it off, each execution loads the operation, destination, rules and encryption profile afresh with one EF query. When enabled, a private cache holds at most 256 known operations for an absolute 10-second TTL, configurable within a 1-60 second range. Unknown keys aren't cached, and caller state/grants always come from SQL.

An enabled-state update commits SQL and history first, then clears the local cache. A generation check prevents an earlier configuration load from refilling the cache after invalidation. The protected cache-invalidation endpoint is available after direct SQL updates. Other processes and direct SQL changes become visible on their next load after expiry unless explicitly invalidated too. The freshness window is the TTL plus any already-running load/execution; it isn't a cluster-wide revocation guarantee. The SQL outage policy still blocks execution even on a configuration cache hit because mandatory audit and grants require SQL.

SQL Server's default READ COMMITTED prevents dirty reads but doesn't promise a database-wide point-in-time snapshot. Related configuration updates must be made atomically. If strict statement snapshots are required under concurrent multi-table writes, enable READ_COMMITTED_SNAPSHOT on the deployment database. No long-lived SQL transaction is held across a remote HTTP request.

A request uses its loaded configuration and authorization decision for the remainder of execution. An already loaded/in-flight request may finish after an operation, destination or caller is disabled or a grant is revoked. There is no immediate distributed kill switch. Stronger revocation requires coordination with the dispatch boundary and possibly upstream cancellation support.

Operation and destination have SQL rowversion columns. The exposed operation-enabled update takes an expected base64 version, checks it, and relies on EF/SQL's original-version predicate for the race after that check. A stale update returns 409. SaveChanges atomically records the state change with its prior version, timestamp and authenticated actor. Older history migrated from the previous schema has an empty actor rather than an invented identity. Direct SQL changes, caller grants and initial seeding require audited DBA/deployment processes. A future general administration API would need destination/child-rule history and aggregate version updates too.

ProxyDb stamps destination CreatedAt/UpdatedAt on inserts and refreshes UpdatedAt on tracked updates while preserving the original creation time. Direct SQL writes must set timestamps themselves. the context hook is not a database trigger.

## Audit policy and failure windows

Execution audit applies to requests admitted through the HTTPS/authentication/rate/concurrency gates into ProxyExecutor. That includes caller-permission and open-circuit rejections. HTTPS, authentication and limiter rejections emit structured metadata and metrics without an execution row; host rejection before this middleware belongs to server/ingress logs. Oversized/invalid JSON reaching the executor is audited. If no database is available, even a rejection may be reported as audit_unavailable because the mandatory intent cannot be written.

Before network effects, a durable Started row is required. Before Send, Dispatching is persisted with Forwarded=false. Only after that write and a cancellation check is Forwarded set true in memory immediately before invoking Send, then persisted with completion. For completed rows, Forwarded means dispatch was attempted, not confirmed receipt. A failed dispatch-intent write cannot claim forwarding. For unfinished Dispatching rows, Forwarded=false is ambiguous: the process may have sent the request before failing to save completion. SQL cannot atomically commit an HTTP side effect. After a completion-write failure, the proxy emits a Critical metadata-only event and returns 503, retaining the prior durable intent. Operators must reconcile unfinished attempts and clients must avoid blind retries. A transactional outbox would improve asynchronous delivery durability but would change the synchronous API contract and still require upstream deduplication.

Audit stores correlation, recognized caller ID, active trace ID, known operation key, destination ID, timestamps, duration, dispatch flag, upstream/proxy status, outcome and fixed reason codes. It excludes user payload text, secrets and exception messages. Unknown operation keys and unknown subjects aren't persisted verbatim. Configure retention, indexing/partitioning as volume grows, access restrictions, alerts for failed audit completion and reconciliation of stale Started/Dispatching records.

Console logs use JSON with UTC timestamps and structured fields. Every request reaching the middleware gets a new correlation ID, returned in its response and public error. The `SecureProxy` ActivitySource creates a `proxy.execute` span when a listener is enabled; audit records retain the current trace ID. The `SecureProxy` Meter exposes `proxy.executions`, `proxy.execution.duration` (ms), `proxy.ingress_rejections` and `proxy.circuit_transitions`. Metric tags contain only fixed outcomes, statuses, reasons and circuit states, avoiding caller/request cardinality and payload data. A protected `/api/admin/health` checks SQL connectivity. Instrumentation is implemented and tested; an OpenTelemetry collector/exporter, dashboard, retention policy and alert rules still need deployment setup.

## Security and reliability trade-offs

| # | Decision | Benefit | Cost or limitation |
| ---: | --- | --- | --- |
| 1 | Mandatory SQL audit before dispatch | Every admitted outbound attempt has durable intent | A SQL outage stops forwarding. Cached config doesn't make SQL optional |
| 2 | Persist completion after the upstream call | Records actual result separately from intent | No transaction spans SQL and HTTP; a completion failure can leave an ambiguous side effect |
| 3 | Cache off by default; bounded opt-in TTL | Simple freshness by default, lower configuration read load when enabled | Cached policy can remain stale until invalidation/expiry; in-flight work may finish |
| 4 | Fresh caller/grant checks | Revocation isn't delayed by the configuration cache | Extra SQL reads; revocation after authorization can't undo in-flight work |
| 5 | JWT identity plus explicit grants | Caller identity and operation permission are separate checks | Provider availability/signing-key rotation and token policies require operational ownership |
| 6 | Bounded response buffering | Reliable size enforcement and consistent safe errors before headers | More peak memory than direct streaming; capped concurrency limits the exposure |
| 7 | No automatic retries | Avoids accidental duplicate POST effects | Callers must decide on recovery using upstream idempotency support |
| 8 | Per-destination circuit and local admission limits | Fails fast during outages and bounds resource use | Thresholds can reject a recovering destination; per-process limits don't form a cluster quota |
| 9 | Restricted path alphabet and header allowlists | Smaller injection/forwarding surface | Some valid upstream contracts need an explicit policy extension |
| 10 | Internal destinations allowed from trusted config | Supports the required internal API use case | DNS and egress controls remain part of deployment security |

## Verification boundaries

* EF InMemory tests cover application behavior. SQL-specific checks use a real SQL Server database. SQL integration tests perform migrations, exercise a stale-writer race, and verify successful/rejected audit persistence with a fresh context. They passed in CI, local runs require PROXY_TEST_SQL. HTTP tests exercise middleware, endpoint binding, successful forwarding and the 32-request concurrency gate with TestServer.

* NetworkTests starts a real local Kestrel HTTPS destination and keeps the application's production named HTTP client settings. Only the test client's trust roots are replaced with a freshly generated certificate. no global trust store is changed and no accept-all certificate callback is used. The test records received requests and checks real dispatch, traversal rejection and non-following of redirects. On Windows the generated certificate is exported/imported as PFX so Schannel can access its private key.

* The GitHub workflow also starts the standalone API and mock against SQL Server 2022. Its smoke script checks forwarding, grant revocation with caching enabled, cache invalidation, management concurrency, AES-GCM, circuit opening and persisted audit/actor records. JWT tests use real RSA-signed tokens with a test issuer configuration; they don't contact an external identity provider. Cache/breaker timing tests use a fake clock. See docs/VERIFICATION.md for the latest verified run and local setup details.
