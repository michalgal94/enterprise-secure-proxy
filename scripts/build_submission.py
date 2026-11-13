"""Generate the Word submission summary."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
from xml.sax.saxutils import escape
import argparse

parser = argparse.ArgumentParser()
parser.add_argument('--repository', default='https://github.com/michalgal94/enterprise-secure-proxy')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
paragraphs = [
    ('Enterprise Secure Proxy — Senior Developer Assessment', True),
    ('Repository: ' + args.repository, False),
    ('Implemented with C# / .NET 8, ASP.NET Core Web API, EF Core and SQL Server.', False),
    ('Delivered artifacts', True),
    ('EnterpriseSecureProxy.sln: application, recording-handler tests, HTTP pipeline tests, SQL-specific integration test and HTTPS mock destination.', False),
    ('Database: EF Core migrations and docs/schema.sql. Optional encryption demo setup: docs/enable-demo-encryption.sql.', False),
    ('README.md: setup, example requests, technical decisions, current improvements and future plans.', False),
    ('DESIGN-NOTES.md: architecture, trust boundaries, audit consistency, concurrency, security and reliability trade-offs.', False),
    ('DEFECT-REPORT.md: identified risks, fixes and remaining boundaries. AI-USAGE.md: tool and assistance disclosure.', False),
    ('docs/REQUIREMENTS-MAP.md and docs/VERIFICATION.md: coverage and actual verification results.', False),
    ('Execution overview', True),
    ('Install .NET 8 SDK and SQL Server (or Docker). Restore/build. Configure separate proxy/admin API keys and SQL connection through environment variables. Trust a local HTTPS development certificate. Run the application with --initialize. Start the mock on HTTPS port 7443 and proxy on HTTPS port 7444. Use the README commands for the exact sequence.', False),
    ('Security decisions', True),
    ('Only trusted configuration determines URL, HTTP method and path. json.urlId must match the configured destination ID. All validation and AES-GCM authentication precede forwarding. Redirects, cookie forwarding, automatic retries and unrestricted header propagation are disabled.', False),
    ('Audit intent is mandatory before dispatch. Post-dispatch audit failure cannot undo an upstream side effect. Rowversion protects the enabled-state management endpoint. In-flight requests may complete after configuration is disabled.', False),
    ('Verification and handoff', True),
    ('All 67 tests passed with no skips in GitHub Actions against SQL Server 2022. The standalone API and mock HTTPS run with SQL also passed. See docs/VERIFICATION.md for the run and results.', False),
    ('The public repository contains the complete solution and documentation. Follow README.md for local setup; use the GitHub Actions run linked in docs/VERIFICATION.md for SQL verification evidence. LocalDB is an optional Windows database engine and needs administrator rights to install.', False),
    ('AI disclosure', True),
    ('OpenAI Codex was used for planning, code, tests and documentation. AI-USAGE.md describes its contribution and the tools used.', False),
]
body = ''.join('<w:p><w:r>' + ('<w:rPr><w:b/><w:sz w:val="28"/></w:rPr>' if heading else '') + '<w:t xml:space="preserve">' + escape(text) + '</w:t></w:r></w:p>' for text, heading in paragraphs)
document = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' + body + '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134"/></w:sectPr></w:body></w:document>'
content_types = '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
relationships = '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
target = root / 'docs/SUBMISSION.docx'
with ZipFile(target, 'w', ZIP_DEFLATED) as output:
    output.writestr('[Content_Types].xml', content_types)
    output.writestr('_rels/.rels', relationships)
    output.writestr('word/document.xml', document)
print(target)
