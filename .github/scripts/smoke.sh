#!/usr/bin/env bash
set -euo pipefail

: "${PROXY_SMOKE_SQL:?Set PROXY_SMOKE_SQL to a dedicated SQL Server database}"
: "${MSSQL_SA_PASSWORD:?Set MSSQL_SA_PASSWORD for the disposable SQL container}"

task_root="$(pwd)"
task_dir="$(mktemp -d)"
api_pid=""
mock_pid=""
cleanup() {
  task_status=$?
  if [[ "$task_status" -ne 0 ]]; then
    for task_log in "$task_dir"/*.log; do
      [[ ! -f "$task_log" ]] || cat "$task_log"
    done
  fi
  [[ -z "$api_pid" ]] || kill "$api_pid" 2>/dev/null || true
  [[ -z "$mock_pid" ]] || kill "$mock_pid" 2>/dev/null || true
}
trap cleanup EXIT

openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -keyout "$task_dir/key.pem" -out "$task_dir/cert.pem" \
  -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost' \
  -addext 'extendedKeyUsage=serverAuth' >"$task_dir/certificate.log" 2>&1
certificate_password="$(openssl rand -hex 24)"
openssl pkcs12 -export -out "$task_dir/cert.pfx" \
  -inkey "$task_dir/key.pem" -in "$task_dir/cert.pem" -passout "pass:$certificate_password"

export ConnectionStrings__Proxy="$PROXY_SMOKE_SQL"
export ProxyApiKey="$(openssl rand -hex 32)"
export AdminApiKey="$(openssl rand -hex 32)"
export PROXY_KEY_LOCAL="$(openssl rand -base64 32)"
export ASPNETCORE_Kestrel__Certificates__Default__Path="$task_dir/cert.pfx"
export ASPNETCORE_Kestrel__Certificates__Default__Password="$certificate_password"
export SSL_CERT_FILE="$task_dir/cert.pem"
export AllowedHosts=localhost
export ConfigurationCache__Enabled=true

api_dll="$task_root/src/SecureProxy/bin/Release/net8.0/SecureProxy.dll"
mock_dll="$task_root/mock/MockDestination/bin/Release/net8.0/MockDestination.dll"
dotnet "$api_dll" --initialize >"$task_dir/initialize.log" 2>&1
dotnet "$mock_dll" --urls https://localhost:7443 >"$task_dir/mock.log" 2>&1 &
mock_pid=$!
dotnet "$api_dll" --urls https://localhost:7444 >"$task_dir/api.log" 2>&1 &
api_pid=$!

api=https://localhost:7444
mock=https://localhost:7443
for attempt in {1..30}; do
  if curl --silent --fail --cacert "$task_dir/cert.pem" "$mock/__test/request-count" >/dev/null && \
     curl --silent --fail --cacert "$task_dir/cert.pem" -H "X-Api-Key: $AdminApiKey" "$api/api/admin/operations/customer.get" >"$task_dir/operation.json"; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then exit 1; fi
  sleep 2
done

request() {
  local expected="$1"
  local body="$2"
  local actual
  actual="$(curl --silent --show-error --cacert "$task_dir/cert.pem" \
    -H "X-Api-Key: $ProxyApiKey" -H 'Content-Type: application/json' \
    -D "$task_dir/headers.txt" -o "$task_dir/response.json" -w '%{http_code}' \
    --data "$body" "$api/api/proxy/execute")"
  last_status="$actual"
  if [[ "|$expected|" != *"|$actual|"* ]]; then
    printf 'Expected HTTP %s, received %s\n' "$expected" "$actual"
    cat "$task_dir/response.json"
    exit 1
  fi
}
assert_count() {
  local actual
  actual="$(curl --silent --show-error --fail --cacert "$task_dir/cert.pem" "$mock/__test/request-count" | jq -r '.count')"
  [[ "$actual" == "$1" ]] || { printf 'Unexpected mock count: %s\n' "$actual"; exit 1; }
}

valid='{"clientId":"customer.get","json":{"urlId":1,"id":12}}'
request 200 "$valid"
jq -e '.id == 12 and .name == "Assessment Customer"' "$task_dir/response.json" >/dev/null
grep -qi '^X-Proxy-Source: upstream' "$task_dir/headers.txt"
assert_count 1
request 400 '{"clientId":"customer.get","json":{"urlId":1,"id":12,"url":"https://untrusted.example"}}'
request 400 '{"clientId":"customer.get","method":"DELETE","json":{"urlId":1,"id":12}}'
request 400 '{"clientId":"customer.get","json":{"urlId":999,"id":12}}'
assert_count 1

version="$(jq -r '.version' "$task_dir/operation.json")"
update="$(jq -cn --arg version "$version" '{enabled:false,version:$version}')"
curl --silent --show-error --fail --cacert "$task_dir/cert.pem" \
  -X PUT -H "X-Api-Key: $AdminApiKey" -H 'Content-Type: application/json' \
  --data "$update" "$api/api/admin/operations/customer.get/enabled" >"$task_dir/updated.json"
request 403 "$valid"
assert_count 1
stale_status="$(curl --silent --show-error --cacert "$task_dir/cert.pem" -o "$task_dir/stale.json" -w '%{http_code}' \
  -X PUT -H "X-Api-Key: $AdminApiKey" -H 'Content-Type: application/json' \
  --data "$update" "$api/api/admin/operations/customer.get/enabled")"
[[ "$stale_status" == 409 ]]
update="$(jq -cn --arg version "$(jq -r '.version' "$task_dir/updated.json")" '{enabled:true,version:$version}')"
curl --silent --show-error --fail --cacert "$task_dir/cert.pem" \
  -X PUT -H "X-Api-Key: $AdminApiKey" -H 'Content-Type: application/json' \
  --data "$update" "$api/api/admin/operations/customer.get/enabled" >"$task_dir/enabled.json"

sql_container="$(docker ps --filter ancestor=mcr.microsoft.com/mssql/server:2022-latest --format '{{.ID}}')"
sql() {
  docker exec -i "$sql_container" /opt/mssql-tools18/bin/sqlcmd -S localhost \
    -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d ProxySmoke "$@"
}
sql -Q "DELETE FROM CallerOperationGrants WHERE CallerId=N'local-demo';"
request 403 "$valid"
assert_count 1
sql -Q "INSERT INTO CallerOperationGrants (CallerId, OperationId) SELECT N'local-demo', Id FROM Operations WHERE [Key]=N'customer.get';"
sql < "$task_root/docs/enable-demo-encryption.sql"
curl --silent --show-error --fail --cacert "$task_dir/cert.pem" -X POST \
  -H "X-Api-Key: $AdminApiKey" "$api/api/admin/configuration-cache/invalidate"
request 400 "$valid"
assert_count 1
encrypted="$(pwsh -NoProfile -File "$task_root/scripts/Encrypt-Payload.ps1" -ClientId customer.get -Json '{"urlId":1,"id":12}')"
request 200 "$encrypted"
assert_count 2
corrupt="$(printf '%s' "$encrypted" | jq -c '.encrypted.tag="AAAAAAAAAAAAAAAAAAAAAA=="')"
request 400 "$corrupt"
assert_count 2

kill "$mock_pid"
wait "$mock_pid" 2>/dev/null || true
mock_pid=""
# Earlier successes may expire from the sampling window on a slow runner.
for attempt in {1..6}; do
  request '502|503' "$encrypted"
  if [[ "$last_status" == 503 ]]; then break; fi
done
[[ "$last_status" == 503 ]]
request 503 "$encrypted"
sql -Q "SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM ExecutionLogs WHERE Outcome=N'Succeeded' AND Forwarded=1 AND UpstreamStatus=200 AND CompletedAt IS NOT NULL) THROW 51000, 'Missing success audit', 1;
IF NOT EXISTS (SELECT 1 FROM ExecutionLogs WHERE Outcome=N'Rejected' AND Forwarded=0 AND ProxyStatus=400 AND CompletedAt IS NOT NULL) THROW 51000, 'Missing rejection audit', 1;
IF NOT EXISTS (SELECT 1 FROM ExecutionLogs WHERE Outcome=N'Failed' AND ProxyStatus=502 AND Reason=N'upstream_connection_failed') THROW 51000, 'Missing failure audit', 1;
IF NOT EXISTS (SELECT 1 FROM ExecutionLogs WHERE Reason=N'operation_not_permitted' AND CallerId=N'local-demo' AND Forwarded=0) THROW 51000, 'Missing permission rejection', 1;
IF NOT EXISTS (SELECT 1 FROM ExecutionLogs WHERE Reason=N'upstream_circuit_open' AND Forwarded=0 AND ProxyStatus=503) THROW 51000, 'Missing circuit rejection', 1;
IF EXISTS (SELECT 1 FROM ConfigurationChanges WHERE ActorId <> N'local-admin') THROW 51000, 'Missing history actor', 1;
IF (SELECT COUNT(*) FROM ConfigurationChanges) <> 2 THROW 51000, 'Incorrect configuration history', 1;"
printf 'SQL-backed HTTPS smoke passed: forwarding, caller grants, cache invalidation, rowversion, AES-GCM, circuit breaking and persisted audit.\n'
