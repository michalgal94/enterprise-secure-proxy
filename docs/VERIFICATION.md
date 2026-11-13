# Verification record - 2026-11-12

Environment: Windows x64, .NET SDK 8.0.428, runtime 8.0.34. EF Core and ASP.NET Core test packages: 8.0.34.

## Completed checks

| Check | Result |
|---|---|
| Restore and build | Succeeded; Release solution build: 0 warnings, 0 errors; application, tests and standalone mock built |
| Final `dotnet test --configuration Release --logger trx` | **65 passed, 0 failed, 2 skipped; 67 total** |
| EF migration/model comparison | `dotnet ef migrations has-pending-model-changes --project src/SecureProxy`: no pending model changes |
| SQL migration artifact | Idempotent docs/schema.sql exists and matches the EF schema version; not executed against a live SQL instance here |
| Dependency audit | `dotnet list package --vulnerable --include-transitive`: no vulnerable packages reported by the configured NuGet source at check time; not a complete security assessment |
| Real HTTPS destination | Passed: actual Kestrel HTTPS server and the production named HttpClient; valid dispatch, traversal rejection and redirect not followed |
| HTTP pipeline | Successful forwarding, unknown operation, authentication/transport gates, malformed admin input, correlation/source headers and concurrency saturation passed |
| Bounded I/O and cancellation | Request/response caps, absent Content-Length, stalled response body, caller abort, upstream timeout and broken response stream passed |
| Security/policy | URL/method/path override attempts, duplicate input, disabled operation/destination, path encodings, parameter placement/types/ranges, headers and AES-GCM cases passed |
| Audit failure injection | Admission and dispatch-intent failures send nothing; completion failure preserves durable Dispatching state; forwarding is not falsely claimed on a failed intent write |
| Documentation | Setup instructions, design notes, defect report, AI declaration and requirements map are included |

## What the real HTTPS test proves

NetworkTests uses an actual local TCP/TLS Kestrel destination that records incoming requests. The proxy's inbound side runs through TestServer and its database uses EF InMemory. The test keeps the application's actual named SocketsHttpHandler and changes only its test trust roots to a generated certificate. No global certificate store is changed and no accept-all validation callback is used. It verifies that the destination receives a valid request, receives no traversal request, and receives no additional redirected request.

The database in this test is InMemory. The standalone mock-to-proxy run with SQL Server still needs to be checked.

## SQL verification remains pending

These two tests are explicitly skipped because PROXY_TEST_SQL is not configured:

- Real_SQL_rowversion_rejects_stale_writer_and_records_history_atomically
- Real_SQL_persists_success_and_rejection_audits

SQL Server was unavailable locally. LocalDB installation failed because the account lacks administrator rights (MSI exit 1603). These tests still need a real SQL connection.

InMemory tests do not prove SQL transactions, rowversion generation or migration execution. Set PROXY_TEST_SQL to a dedicated SQL database or run the supplied GitHub workflow to complete this verification. Run the documented standalone HTTPS mock and proxy against that database as well.

## Submission remains pending

The repository still needs to be published and CI needs to pass. The workflow includes a disposable SQL Server service. Add the published URL to the Word document before submitting.

REQUIREMENTS-MAP.md maps all 17 required test scenarios to the code. The remaining submission steps are live SQL verification, the standalone SQL-backed run, and GitHub/Word delivery.
