#!/bin/sh
set -eu

API_URL=${ICPAAAS_ACCEPTANCE_API_URL:-}
EMAIL=${ICPAAAS_ACCEPTANCE_EMAIL:-}
PASSWORD=${ICPAAAS_ACCEPTANCE_PASSWORD:-}
WORKSPACE=${ICPAAAS_ACCEPTANCE_WORKSPACE:-}
DESTINATION=${ICPAAAS_ACCEPTANCE_DESTINATION:-}
CALLER_ID=${ICPAAAS_ACCEPTANCE_CALLER_ID:-}

pass(){ printf '%s\n' "[PASS] $*"; }
fail(){ printf '%s\n' "[FAIL] $*" >&2; exit 1; }
need(){ command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }
need curl;need jq
[ -n "$API_URL" ]||fail 'ICPAAAS_ACCEPTANCE_API_URL is required'
[ -n "$EMAIL" ]||fail 'ICPAAAS_ACCEPTANCE_EMAIL is required'
[ -n "$PASSWORD" ]||fail 'ICPAAAS_ACCEPTANCE_PASSWORD is required'
API_URL=${API_URL%/}

live=$(curl -fsS "$API_URL/health/live")||fail 'liveness endpoint failed'
[ "$(printf '%s' "$live"|jq -r .status)" = live ]||fail 'invalid liveness response';pass 'API liveness'
ready=$(curl -fsS "$API_URL/health/ready")||fail 'readiness endpoint failed'
printf '%s' "$ready"|jq -e . >/dev/null||fail 'invalid readiness response';pass 'dependency readiness'
payload=$(jq -n --arg email "$EMAIL" --arg password "$PASSWORD" --arg workspace "$WORKSPACE" '{email:$email,password:$password,workspace:$workspace}')
login=$(curl -fsS -H 'Content-Type: application/json' --data "$payload" "$API_URL/api/v1/auth/login")||fail 'login failed'
token=$(printf '%s' "$login"|jq -r .accessToken);[ -n "$token" ]&&[ "$token" != null ]||fail 'login returned no access token';pass 'authenticated login'
auth(){ curl -fsS -H "Authorization: Bearer $token" "$@"; }
me=$(auth "$API_URL/api/v1/me")||fail '/me failed';tenant=$(printf '%s' "$me"|jq -r '.tenantId // empty');pass 'session and role claims'
caps=$(auth "$API_URL/api/v1/system/capabilities")||fail 'capability endpoint failed';printf '%s' "$caps"|jq -e . >/dev/null;pass 'platform capability report'
agents=$(auth "$API_URL/api/v1/agents")||fail 'agent endpoint list failed';printf '%s' "$agents"|jq -e . >/dev/null||fail 'invalid agent endpoint response';pass 'agent endpoint API'
processes=$(auth "$API_URL/api/v1/contact-center/processes")||fail 'process API failed';printf '%s' "$processes"|jq -e . >/dev/null||fail 'invalid process response';pass 'process configuration API'
campaigns=$(auth "$API_URL/api/v1/contact-center/campaigns")||fail 'campaign API failed';printf '%s' "$campaigns"|jq -e . >/dev/null||fail 'invalid campaign response';pass 'campaign execution API'
from=$(date -u -d '30 days ago' +%Y-%m-%dT00:00:00Z 2>/dev/null||date -u -v-30d +%Y-%m-%dT00:00:00Z)
to=$(date -u +%Y-%m-%dT23:59:59Z)
report=$(auth "$API_URL/api/v1/reports/summary?from=$from&to=$to")||fail 'report API failed';printf '%s' "$report"|jq -e .totals >/dev/null||fail 'invalid report response';pass 'report query and tenant isolation'
if [ -n "$DESTINATION" ];then
 [ -n "$tenant" ]||fail 'tenant ID unavailable for test call'
 call=$(jq -n --arg tenant "$tenant" --arg destination "$DESTINATION" --arg caller "$CALLER_ID" '{tenantId:$tenant,destination:$destination,callerId:(if ($caller|length)>0 then $caller else null end),engineKey:null,trunkKey:null}')
 call_response=$(curl -fsS -H "Authorization: Bearer $token" -H 'Content-Type: application/json' --data "$call" "$API_URL/api/v1/telephony/test-call")||fail 'managed SIP test call failed';printf '%s' "$call_response"|jq -e . >/dev/null||fail 'invalid managed SIP response';pass 'managed SIP originate accepted'
else
 printf '%s\n' '[SKIP] Real SIP call (set ICPAAAS_ACCEPTANCE_DESTINATION to enable)'
fi
printf '%s\n' 'ICPaaS acceptance checks completed successfully.'
