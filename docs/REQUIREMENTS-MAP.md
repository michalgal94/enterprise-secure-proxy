# Assessment requirements and evidence

Reviewed against every section of docs/assignment.md. Implementation coverage and completed runtime verification are separate: SQL-specific integration and GitHub publication remain pending in this environment.

## Functional, data and reliability requirements

| Assignment | Implementation and evidence | Assessment |
|---|---|---|
| 1-3: Backend-only, configured permitted operations | ASP.NET Core application; callers select clientId only; no unrestricted forwarding endpoint | Implemented |
| 4: POST /api/proxy/execute | Program endpoint; HttpTests successful and rejected HTTP requests | Verified |
| 4: Envelope then operation lookup then urlId/policies | Closed-envelope parse in ProxyExecutor; RequestPolicy validates destination ID before dispatch | Verified |
| 4: Failed pre-forward checks never reach destination | Recording-handler zero-call assertions; real HTTPS traversal request does not increase destination count | Verified |
| 5.1: Destination fields | Destination stores ID/name/base URL/state/timeout/permitted port/timestamps; context stamps tracked updates | Model and behavior verified; SQL persistence pending |
| 5.2: Operation fields and trusted routing | Operation stores key/destination/method/path/state/size/encryption/profile/rowversion; caller overrides rejected | Behavior verified; real SQL rowversion pending |
| 5.3: Parameter types, required/optional, length/range/location | ParameterRule and RequestPolicy; path/query/body/optional/type/range/unknown tests | Verified |
| 5.4: Header allowlist | Configured AND hard allowlist; dynamic Connection names excluded; no credentials/cookies/Host | Verified |
| 5.5: Encryption references and secure key source | EncryptionProfile, IKeySource, environment-backed AES-256 key source; no key bytes in SQL | Verified with valid/invalid encryption and missing/invalid keys |
| 5.6: Execution metadata | ExecutionLog stores correlation/known key/destination/times/duration/dispatch/status/outcome/reason | Executor verified; live SQL persistence pending |
| 5.7: Configuration history | Atomic enabled-state change/history, previous version/time; direct SQL audit policy documented | Implementation present; SQL transaction verification pending |
| 6: Timeouts and cancellation | Destination deadline covers send/body; caller token propagated; distinct 504/499 | Verified |
| 6: HTTP client lifecycle | Named IHttpClientFactory + pooled SocketsHttpHandler | Exercised through real HTTPS test |
| 6: Streaming and size bounds | Hard/per-operation request limits; bounded chunk reads; response cap with/without Content-Length | Verified; bounded buffering trade-off documented |
| 6: Retries/idempotency | No retries; safe-retry constraints and upstream deduplication explained | Addressed in README/DESIGN-NOTES |
| 6: Caching/consistency | No cache; fresh query; already loaded requests may finish after disable | Explicit consistency model documented |
| 6: Concurrency | SQL rowversion, original-version predicate and 409; stale-token unit test | Unit verified; actual SQL race pending |
| 6: Resilience | 32-request concurrency cap/no queue, bounded connections, no retries; circuit breaking discussed | Saturation test verifies 33rd request is blocked |
| 7: Safe and distinct errors | Proxy codes/correlation, upstream status/source, sanitized error bodies; client binding errors preserve 4xx | Verified; missing destination defensively rejected by policy |
| 8: Structured logging and audit policy | Metadata-only logs, mandatory admission, dispatch intent, independent completion, explicit outage policy | Success/rejection/failure/outage tests verified |
| 8: SQL unavailable during audit | Admission failure prevents dispatch; post-dispatch failure returns 503 and leaves intent; reconciliation documented | Injected failure behavior verified; real SQL outage pending |
| 14: AI usage | AI-USAGE describes tools and their contributions | Documented |

SQL Server is the configured persistence provider. Operational settings are represented by destination timeout/port and operation size/policy fields; there is no separate arbitrary settings table or general configuration editor.

## All 17 required automated tests

| # | Required behavior | Test evidence |
|---:|---|---|
| 1 | Valid operation forwarded | Valid_operation_is_forwarded_and_audited; Valid_HTTP_request_is_forwarded_and_response_is_identified; NetworkTests |
| 2 | Unknown operation rejected | Rejections_never_reach_destination: unknown; HTTP unknown-operation test |
| 3 | Disabled operation rejected | Rejections_never_reach_destination: disabled |
| 4 | Disabled destination rejected | Rejections_never_reach_destination: destination |
| 5 | Destination URL override blocked | Rejections_never_reach_destination: url; Unknown_allowed_parameters_go_only_to_body checks actual host |
| 6 | Invalid/disallowed method rejected | Rejections_never_reach_destination: method and caller_method |
| 7 | Required parameters enforced | Rejections_never_reach_destination: required/missing_url_id |
| 8 | Unknown parameter policy | Rejections_never_reach_destination: unknown_parameter; Unknown_allowed_parameters_go_only_to_body |
| 9 | Traversal blocked | All_path_traversal_encodings_are_blocked; template scenario; real HTTPS traversal assertion |
| 10 | Disallowed headers excluded | Headers_require_both_configured_and_hard_allowlists; Connection_nominated_headers_are_not_forwarded |
| 11 | Invalid AES-GCM authentication tag | Invalid_encryption_is_never_forwarded(true) |
| 12 | Invalid ciphertext never forwarded | Invalid_encryption_is_never_forwarded(false); plaintext_required_encryption rejection |
| 13 | Upstream timeout | Upstream_timeout_returns_504; stalled unknown-length body deadline test |
| 14 | Upstream HTTP error | Upstream_error_preserves_status_and_hides_sensitive_body |
| 15 | Cancellation propagated | Caller_cancellation_is_propagated |
| 16 | Configuration concurrency conflicts | Stale_configuration_returns_conflict_without_forwarding; Real_SQL_rowversion_rejects_stale_writer_and_records_history_atomically (SQL pending) |
| 17 | Successful/rejected audit | Valid operation and rejection tests; audit admission/dispatch/completion outage tests; Real_SQL_persists_success_and_rejection_audits (SQL pending) |

Every pre-forward rejection case in executor tests checks an empty recording handler. HTTP rejection tests also check the injected recorder; the concurrency test proves the rejected request does not add a 33rd destination call. The missing-destination defensive policy test checks the error and no calls; a missing SQL destination normally cannot exist because of the foreign key. NetworkTests checks the actual server request count before/after rejected traversal. A gateway failure after dispatch is distinguished from a pre-forward rejection and correctly expects a destination attempt.

## Submission deliverables

| # | Deliverable | Location/status |
|---:|---|---|
| 1 | Complete Visual Studio solution | EnterpriseSecureProxy.sln with application, tests and mock |
| 2 | SQL schema/migrations/script | src/SecureProxy/Migrations and docs/schema.sql; live application pending |
| 3 | Automated tests | tests/SecureProxy.Tests; actual results in VERIFICATION.md |
| 4 | Setup/execution README | README.md, English, build/SQL/secrets/init/mock/API/requests/encryption/tests/submission |
| 5 | Defect report | DEFECT-REPORT.md, fixes and checks |
| 6 | Architecture/technical decisions | DESIGN-NOTES.md, boundaries, sequence, trade-offs, failure windows and consistency |
| 7 | AI usage declaration | AI-USAGE.md, tools and contributions including review fixes |
| 8 | GitHub link in Word | docs/SUBMISSION.docx and generator exist; actual repository URL/publication and remote CI remain pending |

## Remaining checks

Before submitting: run both SQL integration tests, check the standalone SQL-backed setup, publish the repository, confirm CI passes, and add the real URL to the Word document. Production limitations are covered in DESIGN-NOTES.md.
