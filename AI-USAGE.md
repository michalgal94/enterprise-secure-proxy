# AI usage

I used AI mainly to validate decisions, think through edge cases, and compare implementation options. Suggestions were checked against the code and its actual behavior before I decided whether to use them.

OpenAI Codex also generated and revised substantial parts of the C# implementation, migrations, tests, CI scripts and documentation. That included the fixes listed in the defect report.

## Changes I requested

1. **Completing SQL verification.** The first version left the SQL integration tests skipped locally and the full SQL-backed run unfinished. I asked for those checks to be completed before submission rather than left as follow-up work. The existing SQL Server CI job was run, and a standalone HTTPS smoke check was added. All 67 tests passed with no skips, and the full SQL-backed run passed too.

2. **Keeping the README focused.** The initial README mixed setup instructions with interview coaching and details about the editing process. I asked for a clearer English README, separate sections for current and future improvements, and fewer comments that just repeat the code. The documentation was rewritten around the project, its setup and its decisions.

These changes were implemented with Codex after my feedback.

## Tools used

- .NET and EF Core CLI for builds, tests and migrations.
- PowerShell for local commands and the encryption example.
- Python for document generation and small file edits.
- Git and GitHub Actions for publishing and SQL-backed verification.
- Microsoft documentation and NuGet for API details and package versions.

I'm responsible for checking and understanding the submitted code. Test results, including SQL Server and the standalone HTTPS run, are listed in [docs/VERIFICATION.md](docs/VERIFICATION.md).
