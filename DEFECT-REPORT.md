# Defect report - Bugs along with their fixes

These are the issues fixed during development, along with the tests or checks that cover them.

## Fixed issues

| Issue | Fix | Check |
|---|---|---|
| Missing or invalid encryption keys were treated like bad caller data | Return 503 for key configuration problems, and 400 for invalid encrypted input | Missing_or_invalid_secret_is_a_service_failure; invalid-encryption tests |
| A broken upstream response body returned a generic 503 | Treat an IOException from the upstream body as a 502, without exposing its message | Broken_upstream_body_is_a_gateway_failure |
| Malformed admin JSON could return 503 | Keep the correct client-error status when request binding fails | Malformed_admin_requests_return_client_errors |
| An allowlisted header could still be hop-by-hop if named in Connection | Exclude headers named by Connection as well as the fixed blocked headers | Connection_nominated_headers_are_not_forwarded |
| Destination UpdatedAt was not refreshed on changes | Update the timestamp on tracked changes and preserve CreatedAt | Destination_update_preserves_creation_time_and_refreshes_update_time |
| Audit admission had no application-level deadline | Give the initial audit write its own five-second deadline; block forwarding if it fails | Admission-failure test and executor deadline |
| Upstream HTTP errors had no audit reason code | Save upstream_http_error alongside the status and outcome | Upstream_error_preserves_status_and_hides_sensitive_body |
| Forwarded was set before the dispatch-intent write could fail | Save the intent first, then mark the attempt just before Send | Dispatch_intent_failure_does_not_claim_forwarding_or_send |
| A crash or completion-write failure could leave the forwarding result unclear | Keep the durable Dispatching record and document that unfinished attempts need reconciliation | Audit_completion_failure_leaves_durable_dispatch_intent |
| The HTTPS test certificate failed with Windows Schannel | Import the generated certificate as PFX so the private key is available | NetworkTests |

## Security checks

The tests also cover destination overrides, disabled operations and destinations, invalid methods, missing and unknown parameters, traversal encodings, header filtering, AES-GCM authentication, request and response limits, timeouts and cancellation. Rejected requests must leave the mock destination's request count unchanged.

The SQL rowversion tests passed against SQL Server in CI and checked stale updates and transactional history. Locally, they run when PROXY_TEST_SQL is set. InMemory tests alone cannot verify those behaviors.

## Verification

All 67 automated tests passed against SQL Server in [GitHub Actions](https://github.com/michalgal94/enterprise-secure-proxy/actions/runs/43347254387). The standalone HTTPS API-to-mock run with SQL also passed.

## Future work

- Replace shared API keys with caller identities and operation permissions before production use.
- Set up egress restrictions, key rotation, monitoring and auditing for configuration changed directly in SQL. More detail is in DESIGN-NOTES.md.
