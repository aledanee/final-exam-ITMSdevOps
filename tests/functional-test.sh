#!/usr/bin/env bash
# Deliverable 3 — Functional validation of the Task Manager API.
# Asserts the exact status code of every endpoint in the assignment.
#
# Usage: ./tests/functional-test.sh [BASE_URL]
#   default BASE_URL: http://192.168.49.2:30080   (Minikube NodePort)
#   Docker Compose:   ./tests/functional-test.sh http://localhost:8080
#
# Exit code: 0 = all checks passed, 1 = at least one failure.
set -u
BASE="${1:-http://192.168.49.2:30080}"
PASS=0; FAIL=0

req() { # METHOD PATH [BODY] -> "STATUS|BODY"
  local m="$1" p="$2" b="${3:-}"
  if [ -n "$b" ]; then
    curl -s -m 20 -o /tmp/ft-body -w '%{http_code}' -X "$m" "$BASE$p" \
      -H 'Content-Type: application/json' -d "$b"
  else
    curl -s -m 20 -o /tmp/ft-body -w '%{http_code}' -X "$m" "$BASE$p"
  fi
}

check() { # DESCRIPTION EXPECTED STATUS [BODY]
  local desc="$1" want="$2" res body
  res=$(req "$3" "$4" "${5:-}")
  if [ "$res" = "$want" ]; then
    PASS=$((PASS+1)); printf 'PASS  %-52s -> %s\n' "$desc" "$res"
  else
    FAIL=$((FAIL+1)); printf 'FAIL  %-52s -> %s (expected %s)\n' "$desc" "$res" "$want"
  fi
}

echo "=== Functional validation: $BASE ==="

# 1. health
check "GET /health returns 200"                       200 GET  /health
check "GET /health body says Healthy"                 200 GET  /health
grep -q '"status":"Healthy"' /tmp/ft-body \
  && { PASS=$((PASS+1)); echo "PASS  health body contains \"status\":\"Healthy\""; } \
  || { FAIL=$((FAIL+1)); echo "FAIL  health body missing \"status\":\"Healthy\""; }

# 2. create
check "POST /api/tasks valid body -> 201"             201 POST /api/tasks '{"title":"functional test","description":"d3"}'
grep -q '"id"' /tmp/ft-body \
  && { PASS=$((PASS+1)); echo "PASS  created task response contains id"; } \
  || { FAIL=$((FAIL+1)); echo "FAIL  created task response missing id"; }
TASK_ID=$(sed -n 's/.*"id":\([0-9]*\).*/\1/p' /tmp/ft-body | head -1)

# 3. read
check "GET /api/tasks -> 200 array"                   200 GET  /api/tasks
grep -q 'functional test' /tmp/ft-body \
  && { PASS=$((PASS+1)); echo "PASS  list contains the created task"; } \
  || { FAIL=$((FAIL+1)); echo "FAIL  list missing the created task"; }
check "GET /api/tasks/$TASK_ID -> 200"                200 GET  "/api/tasks/$TASK_ID"
check "GET /api/tasks/999999 -> 404"                  404 GET  /api/tasks/999999

# 4. update
check "PUT /api/tasks/$TASK_ID -> 200"                200 PUT  "/api/tasks/$TASK_ID" '{"title":"functional test","description":"updated","isCompleted":true}'
grep -q '"isCompleted":true' /tmp/ft-body \
  && { PASS=$((PASS+1)); echo "PASS  update persisted isCompleted=true"; } \
  || { FAIL=$((FAIL+1)); echo "FAIL  update did not persist isCompleted=true"; }

# 5. validation
check "POST /api/tasks missing title -> 400"          400 POST /api/tasks '{"description":"no title"}'

# 6. delete
check "DELETE /api/tasks/$TASK_ID -> 204"             204 DELETE "/api/tasks/$TASK_ID"
check "GET deleted task -> 404"                       404 GET  "/api/tasks/$TASK_ID"

echo "=== Result: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ]
