# Senior Developer --  Assessment

## Enterprise Secure Proxy Web API

**Required stack:** C# / .NET 8, ASP.NET Core Web API, Entity Framework
Core, SQL Server\
**Application type:** Backend-only Web API

------------------------------------------------------------------------

## 1. Objective

Your task is to develop a top secured proxy api that foward requests. 

The proxy must forward only explicitly configured and permitted
operations to predefined destination APIs. It must not behave as an
unrestricted HTTP forwarder.

The assessment evaluates your ability to understand an unfamiliar
codebase, make safe engineering decisions, implement reliable
server-side functionality, and explain the trade-offs in your solution.

## 2. Scope and exclusions

This is a **backend-only** assignment.

The proxy enforces centrally configured operation and destination
policies. The caller requests an operation by its configured key; the
caller does not choose the destination URL, HTTP method, or destination
path.


## 3. Business scenario

An organization exposes several internal and external APIs. Consumers
must access these APIs through a central Proxy Web API.

The proxy has a database containing: - Destination definitions. -
Allowed operation definitions. - Request parameter rules. - Optional
header forwarding rules. - Encryption profile references. - Operational
settings. - Request execution logs.

A request is accepted only when the requested operation exists, is
enabled, its destination is enabled, and all configured validation and
security rules pass.

## 4. Required proxy endpoint

Implement:

`POST /api/proxy/execute`

Example request:

``` json
{
  "clientId": "customer.get",
  "json": ???
}
```

The request contract may be extended where necessary, but callers must
not be allowed to override trusted destination configuration.

### Required processing sequence

For each incoming request, the proxy must:

1.  Validate the request envelope.
2.  Find the operation using its clientId key.
3.  Json must contain the urlId key.
4.  take into circumstances data protection and more...


**Security requirement:** A request that fails any pre-forwarding
validation or policy check must not reach the destination server.

## 5. Configuration and data requirements

Use SQL Server and EF Core to persist the proxy configuration and audit
information.

The schema should support at least the following concepts.

### 5.1 Destinations

A destination represents a configured upstream API.

Required information: - Unique identifier. - Name. - Base URL. -
Active/inactive state. - Request timeout. - Creation and update
timestamps. - Optional destination-specific settings, such as permitted
ports or authentication profile references.

### 5.2 Operations

An operation represents an explicitly allowed action.

Required information: - Unique identifier. - Unique operation key, for
example `customer.get`. - Destination reference. - HTTP method. -
Relative path or path template. - Active/inactive state. - Maximum
request size. - Whether encryption is required. - Optional encryption
profile reference. - Configuration concurrency token.

The destination URL, HTTP method, and relative path must be obtained
from trusted configuration, not from caller-supplied values.

### 5.3 Parameter rules

Support configurable parameter validation, including: - Parameter
name. - Required or optional. - Data type or validation rule. - Maximum
length or permitted range where relevant. - Whether it belongs in the
path, query string, or body.

Unknown parameters should be rejected unless the operation explicitly
allows them.

### 5.4 Header rules

Where header forwarding is required, define an explicit allowlist.

Do not blindly copy all incoming headers to the upstream server.
Sensitive and hop-by-hop headers must not be forwarded by default.

### 5.5 Encryption profiles

Store configuration references and metadata, not plaintext encryption
keys.

The implementation must use a secure key source abstraction. The actual
secret may be supplied through an approved secret manager or secure
environment configuration for local assessment purposes.

### 5.6 Request logs

Record enough metadata to investigate each execution: -
Correlation/request ID. - Operation key. - Destination reference. -
Start and completion timestamps. - Duration. - Whether forwarding
occurred. - Upstream status code, when available. - Proxy response
status. - Outcome category. - Rejection or failure reason.

Do not store encryption keys, credentials, authorization headers, or
sensitive payload contents in logs.

### 5.7 Configuration history

Describe or implement how configuration changes can be traced, including
which configuration was changed and when. If you implement configuration
history, avoid recording secrets.


## 6. Reliability and performance requirements

Address the following:

### Timeouts and cancellation

-   Apply the timeout configured for the destination or operation.
-   Propagate the incoming request cancellation token.
-   Distinguish caller cancellation from an upstream timeout.
-   Return an appropriate gateway timeout response when the upstream
    exceeds the configured timeout.

### HTTP client lifecycle

-   Use `IHttpClientFactory` or an equivalent correct `HttpClient`
    lifetime strategy.
-   Do not create and dispose a new `HttpClient` for every request.

### Streaming and size limits

-   Avoid unbounded buffering of large request and response bodies.
-   Enforce configured request and response size limits.
-   Prefer streaming where appropriate.

### Retries and idempotency

-   Do not blindly retry non-idempotent operations.
-   Explain which failures may be retried safely.
-   Explain how duplicate processing can be prevented for operations
    such as POST, where the upstream supports idempotency keys or
    deduplication.

### Configuration caching

-   You may cache operation and destination configuration.
-   Explain cache expiration and invalidation.
-   Configuration changes must become effective according to
    a clearly documented consistency model.

### Concurrency

-   Use an appropriate concurrency strategy for configuration updates.
-   Handle stale updates predictably, for example with SQL Server
    `rowversion` and HTTP 409 Conflict.
-   Define what happens to requests already executing when an operation
    or destination is disabled.

### Resilience

-   Discuss or implement rate limiting, bounded concurrency, and circuit
    breaking where appropriate.
-   Avoid adding retries or resilience policies that could duplicate
    non-idempotent operations.

## 7. Error handling and HTTP response behavior

Return consistent, non-sensitive error responses.

At minimum, handle: - Unknown operation. - Disabled operation. - Missing
or disabled destination. - Invalid parameters. - Invalid content type. -
Request too large. - Invalid encrypted payload. - Upstream timeout. -
Upstream connection failure. - Upstream HTTP error response. -
Configuration concurrency conflict.

Do not expose internal hostnames, credentials, stack traces, encryption
details, or sensitive configuration in public error messages.

Preserve upstream HTTP status codes where appropriate. Distinguish a
response returned by the upstream service from a failure generated by
the proxy itself.

## 8. Audit, logging, and observability

Use structured logging.

Each request should be traceable using a correlation ID or request ID.

Record successful, rejected, and failed execution attempts according to
a clearly stated audit policy.

Do not log: - Encryption keys. - Authorization credentials. - Cookies. -
Sensitive request or response bodies. - Other secrets or personal data
unless explicitly required and safely redacted.

Explain what happens if SQL Server is unavailable when an audit record
must be written. State whether audit persistence is mandatory, buffered,
or best-effort, and explain the operational consequences of that
decision.

## 9. Automated testing requirements

Add automated tests using an appropriate .NET testing framework, such as
xUnit or NUnit.

Tests must cover at least:

1.  A valid configured operation is forwarded.
2.  An unknown operation is rejected.
3.  A disabled operation is rejected.
4.  A disabled destination is rejected.
5.  A caller-supplied destination URL cannot override configuration.
6.  An invalid or disallowed HTTP method is rejected.
7.  Missing required parameters are rejected.
8.  Unknown parameters are handled according to the operation policy.
9.  Path traversal attempts are rejected.
10. Disallowed headers are not forwarded.
11. Invalid AES-GCM authentication tags are rejected.
12. Invalid encrypted payloads are never forwarded.
13. Upstream timeout is handled correctly.
14. Upstream HTTP error responses are handled correctly.
15. Cancellation is propagated.
16. Configuration concurrency conflicts are handled.
17. Audit records are created for successful and rejected requests,
    according to the documented audit policy.

### Mock destination requirement

Use a mock destination service or test HTTP handler that records every
request it receives.

For every rejected request, tests must verify that the destination did
not receive the request.

A response code alone is not sufficient proof that an unauthorized
request was blocked.

## 13. Deliverables

Submit the following:

1.  Complete modified Visual Studio solution.
2.  SQL Server schema changes, migrations, or scripts.
3.  Automated tests.
4.  `README.md` with setup and execution instructions.
5.  `DEFECT-REPORT.md` describing discovered and fixed defects.
6.  `DESIGN-NOTES.md` explaining architecture and important technical
    decisions.
7.  AI usage declaration describing which tools were used and for what
    purposes.
8. 1-7 on link to github project. details loaded on word document.

Your solution must be reproducible on a clean development environment
using the documented instructions.


## 14. AI and external assistance

You may use documentation, search engines, IDE assistance, and AI tools.

You must disclose AI usage and remain responsible for the complete
submitted solution.

You may be asked to: - Explain selected parts of your implementation. -
Trace a request through the proxy. - Diagnose a defect. - Modify your
own code to satisfy a new requirement. - Explain security and
reliability trade-offs.

The ability to understand, defend, and modify the submitted code is part
of the assessment.

------------------------------------------------------------------------

## Final requirement

The proxy must be **configuration-driven, secure by default, and fail
closed**.

Only explicitly configured operations may be forwarded. No request may
override trusted destination configuration, and no request may reach an
upstream service before all required policy and validation checks have
succeeded.
