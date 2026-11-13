# AI usage

I used AI mainly to validate decisions, think through edge cases, and compare implementation options. Suggestions were checked against the code and its actual behavior before I decided whether to use them.

OpenAI Codex also generated and revised substantial parts of the C# implementation, migrations, tests, CI scripts and documentation. That included the fixes listed in the defect report, plus the cache, rate limits, destination circuits, JWT validation, caller grants and telemetry.

## Changes I requested

1. **Completing SQL verification** The first version left the SQL integration tests skipped locally and the full SQL-backed run unfinished. I asked for those checks to be completed before submission rather than left as follow-up work. The existing SQL Server CI job was run, and a standalone HTTPS smoke check was added. All 67 tests passed with no skips, and the full SQL-backed run passed too.

2. **Checking optional requirements properly** The earlier version had a concurrency cap, but no per-caller rate limit or circuit breaker, and caching was only explained as a future choice. I checked these points against section 6 and asked for a more complete implementation where it made sense. Rate limits and destination circuits were added without retries. A bounded cache was added with expiry and invalidation, but stays off by default. Caller permissions and observability were also extended, and the documentation was updated to match the code.

These changes were implemented with Codex after my feedback.

## Tools used

- .NET and EF Core CLI for builds, tests and migrations.
- PowerShell for local commands and the encryption example.
- Python for document generation and small file edits.
- Git and GitHub Actions for publishing and SQL-backed verification.
- Microsoft documentation and NuGet for API details and package versions.

I'm responsible for checking and understanding the submitted code. Test results, including SQL Server and the standalone HTTPS run, are listed in [docs/VERIFICATION.md](docs/VERIFICATION.md).
