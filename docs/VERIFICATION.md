# Verification record - 2026-11-13

Verified in [GitHub Actions](https://github.com/michalgal94/enterprise-secure-proxy/actions/runs/43347254387) against SQL Server 2022. The run completed successfully.

## Results

| Check | Result |
|---|---|
| Release build | Passed for the API, tests and standalone mock |
| Automated tests with SQL Server | **67 passed, 0 failed, 0 skipped**; confirmed from the uploaded TRX artifact |
| SQL migrations | Applied successfully to the integration-test and standalone smoke databases |
| SQL concurrency | A real stale-writer race returns 409; the failed update does not add a history entry |
| SQL audit persistence | Success and rejection records are checked using a fresh database context |
| Standalone SQL-backed HTTPS run | Passed with the separately started API and mock |
| AES-GCM | Valid encrypted input succeeds; a corrupted tag is rejected without another destination call |
| Request policy | Override attempts, traversal, disabled operations and invalid parameters are blocked |
| Failure handling | Timeouts, caller cancellation, broken body reads, audit-write failures and connection failure are covered |
| Resource limits | Request/response caps and the 32-request concurrency limit are covered |
| Public delivery | Repository is public and the Word document contains its actual URL |

Repository: [michalgal94/enterprise-secure-proxy](https://github.com/michalgal94/enterprise-secure-proxy).

The verified CI run used commit `d425041a8dad4843f0321fc377351ae955ec32e8`. Its test artifact records 67 executed tests and no failures, timeouts or skipped tests. The workflow reruns on subsequent pushes.

## Full SQL-backed run

`.github/scripts/smoke.sh` starts the built API and mock as separate processes. Both use HTTPS, and the API uses a dedicated SQL Server database. Normal certificate validation remains enabled; the Linux processes trust the temporary test certificate through SSL_CERT_FILE.

The script checks:

- A configured request reaches the mock and returns the expected customer.
- URL/method override attempts and a wrong destination ID return 400 without increasing the mock's count.
- A disabled operation returns 403 without forwarding.
- An old rowversion returns 409; re-enabling uses the current version.
- Valid AES-GCM input reaches the mock; plaintext and corrupted encrypted input are rejected when encryption is required.
- Stopping the mock produces a 502.
- SQL contains completed success, rejection and failure audits, plus exactly the two successful configuration changes.

The recorded CI output ends with: `SQL-backed HTTPS smoke passed: forwarding, blocked requests, rowversion, AES-GCM, connection failure and persisted audit.`

## Local environment

The local Windows Release build passed with 0 warnings and 0 errors using SDK 8.0.428 and runtime 8.0.34. The local test run passed 65 tests and skipped the two SQL tests because no local SQL instance was available. Both skipped tests then passed in GitHub Actions.

LocalDB is not installed on this Windows account. Its installer needs administrator rights; the earlier attempt returned MSI 1603. This does not block submission or CI verification. The README explains how to install LocalDB on a Windows machine with administrator access, or use Docker instead.

Additional local checks passed: no pending EF model changes, no vulnerable dependencies reported by the configured NuGet source, valid Word ZIP/XML, working document links, and no local installers/build output/credentials tracked in Git. The dependency check is not a complete security assessment.

## Submission

The public repository contains the solution, migration and SQL script, automated tests, README, design notes, defect report, AI usage declaration and Word summary. REQUIREMENTS-MAP.md maps every assessment section and all 17 required test scenarios to the implementation.

Production follow-up items, such as per-caller permissions and managed key rotation, remain documented as design limitations and future improvements.
