# AI usage

I used AI mainly to validate decisions, think through edge cases and compare implementation options. Suggestions were checked against the code and its actual behavior before I decided whether to use them.

OpenAI Codex also helped write and revise parts of the C# implementation, migrations, tests, CI scripts and documentation. That included the fixes listed in the defect report, plus the cache, rate limits, destination circuits, JWT validation, caller grants and telemetry.

I'm responsible for checking and understanding the submitted code. Test results, including SQL Server and the standalone HTTPS run, are listed in [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Cases where I improved AI suggestions

1. **Completing SQL verification** The first version left the SQL integration tests skipped locally and the full SQL-backed run unfinished. I asked for those checks to be completed before submission rather than left as follow-up work. The SQL Server CI job was run, and a standalone HTTPS smoke check was added. These checks are now part of the workflow. The expanded suite passes all 98 tests without skips, and the full SQL-backed run passes too.

2. **Checking optional requirements properly** The earlier version had a concurrency cap, but no per-caller rate limit or circuit breaker, and caching was only explained as a future choice. I checked these points against section 6 and asked for a more complete implementation where it made sense. Rate limits and destination circuits were added without retries. A bounded cache was added with expiry and invalidation, but stays off by default. Caller permissions and observability were also extended, and the documentation was updated to match the code.

3. **Keeping the README focused** The initial README mixed setup instructions with interview guidance and details about the editing process. I asked for separate sections for current and future improvements and also technical decisions. The documentation was revised to focus on running the project and understanding its decisions.

4. **Making the LocalDB setup clearer** The setup instructions didn't fully explain how to install LocalDB or when administrator access was needed. I asked for those steps and for clarification on whether LocalDB was essential. Now its separates installation, which needs administrator approval, from normal use under a regular account, and explains the SQL Server and Docker alternatives.

## Tools used

- .NET and EF Core CLI for builds, tests and migrations.
- PowerShell for local commands and the encryption example.
- Python for document generation and small file edits.
- Git and GitHub Actions for publishing and SQL-backed verification.
- Microsoft documentation and NuGet for API details and package versions.
