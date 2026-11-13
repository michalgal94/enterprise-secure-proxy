# Verification record - 2026-11-13

Verified against SQL Server 2022 in [GitHub Actions](https://github.com/michalgal94/enterprise-secure-proxy/actions/runs/43491907864). The expanded test suite and standalone SQL-backed HTTPS check both completed successfully.

## Results

| Check | Result |
|---|---|
| Release build | Passed for the API, tests and standalone mock |
| Expanded local automated suite | **96 passed, 0 failed, 2 SQL tests skipped** |
| Automated tests with SQL Server | **98 passed, 0 failed, 0 skipped**; confirmed from the uploaded TRX artifact |
| SQL migrations | Applied successfully to the integration-test and standalone smoke databases |
| SQL concurrency | A real stale-writer race returns 409; the failed update does not add a history entry |
| SQL audit persistence | Success and rejection records are checked using a fresh database context |
| Standalone SQL-backed HTTPS run | Passed with the separately started API and mock |
| AES-GCM | Valid encrypted input succeeds; a corrupted tag is rejected without another destination call |
| Request policy | Override attempts, traversal, disabled operations and invalid parameters are blocked |
| Failure handling | Timeouts, caller cancellation, broken body reads, audit-write failures and connection failure are covered |
| Resource limits | Request/response caps and the 32-request concurrency limit are covered |
| Caller identity and grants | Signed-token validation, role checks, grants and disabled callers pass; real SQL verifies revocation and rejects case/trailing-space subject variants |
| Cache | Disabled-by-default behavior, absolute expiry, admin invalidation and stale-load protection pass with a fake clock |
| Resilience | Per-caller quotas and destination circuit opening, isolation, recovery and cancellation handling pass locally |
| Error and telemetry contracts | Consistent safe errors, metric tag bounds and trace/audit identity pass locally |
| Public delivery | Repository is public and the Word document contains its actual URL |

Repository: [michalgal94/enterprise-secure-proxy](https://github.com/michalgal94/enterprise-secure-proxy).

The verified CI run used commit `d37c026649ab879e710d942c8723063e5af471b5`. Its uploaded TRX artifact records 98 total/executed/passed tests and zero failures, errors, timeouts or unexecuted tests. The workflow reruns on subsequent pushes.

## Full SQL-backed run

`.github/scripts/smoke.sh` starts the built API and mock as separate processes. Both use HTTPS, and the API uses a dedicated SQL Server database. Normal certificate validation remains enabled; the Linux processes trust the temporary test certificate through SSL_CERT_FILE.

The script checks:

- A configured request reaches the mock and returns the expected customer.
- URL/method override attempts and a wrong destination ID return 400 without increasing the mock's count.
- A disabled operation returns 403 without forwarding.
- An old rowversion returns 409; re-enabling uses the current version.
- Configuration caching is enabled, a caller grant is revoked in SQL, and the request returns 403 without another destination call.
- Direct SQL encryption updates are followed by protected local cache invalidation.
- Valid AES-GCM input reaches the mock; plaintext and corrupted encrypted input are rejected when encryption is required.
- Stopping the mock produces 502 failures, then the destination circuit opens and returns 503 without dispatch.
- SQL contains completed success, rejection, permission and circuit audits, plus exactly two successful configuration changes with the admin actor.

The recorded smoke output ends with: `SQL-backed HTTPS smoke passed: forwarding, caller grants, cache invalidation, rowversion, AES-GCM, circuit breaking and persisted audit.`

## Local environment

The local Windows Release build passed with 0 warnings and 0 errors using SDK 8.0.428 and runtime 8.0.34. The expanded local run passed 96 tests and skipped the two SQL tests because no local SQL instance was available. Both SQL tests, including their new authorization checks, then passed in GitHub Actions.

LocalDB is not installed on this Windows account. Its installer needs administrator rights; the earlier attempt returned MSI 1603. This does not block submission or CI verification. The README explains how to install LocalDB on a Windows machine with administrator access, or use Docker instead.

Additional local checks passed: no pending EF model changes, no vulnerable dependencies reported by the configured NuGet source, valid Word ZIP/XML, working document links, and no local installers/build output/credentials tracked in Git. The dependency check is not a complete security assessment.

## Submission

The public repository contains the solution, migration and SQL script, automated tests, README, design notes, defect report, AI usage declaration and Word summary. REQUIREMENTS-MAP.md maps every assessment section and all 17 required test scenarios to the implementation.

JWT support and per-caller permissions are implemented. A real identity-provider registration, telemetry exporter/backend, managed key rotation, egress restrictions and coordinated limits for multiple instances remain deployment tasks. Signed-token tests use an RSA test issuer configuration; no external provider tenant has been provisioned or tested live.
