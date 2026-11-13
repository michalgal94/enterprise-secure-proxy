# AI usage

I used OpenAI Codex to help plan the solution, generate and revise the C# code, migrations, tests and documentation, and review security and reliability decisions. This included the README wording and the additional fixes and tests listed in the defect report.

Tools used alongside Codex:

- .NET and EF Core CLI for builds, tests and migrations.
- PowerShell for local commands and the encryption example.
- Python for document generation and small file edits.
- Git and GitHub Actions for publishing and SQL-backed verification.
- Microsoft documentation and NuGet for API details and package versions.

I'm responsible for checking and understanding the submitted code. Test results, including SQL Server and the standalone HTTPS run, are listed in [docs/VERIFICATION.md](docs/VERIFICATION.md).
