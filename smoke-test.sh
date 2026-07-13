#!/usr/bin/env bash
# Smoke test for the Clinical Trial Pre-Screening Assistant.
# Assumes the stack is already running: docker compose up --build
set -uo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
EMBEDDING_URL="${EMBEDDING_URL:-http://localhost:8001}"
QDRANT_URL="${QDRANT_URL:-http://localhost:6333}"

PASS=0
FAIL=0

check() {
  local name="$1"
  local expected="$2"
  local actual="$3"
  if [ "$actual" = "$expected" ]; then
    echo "[PASS] $name (HTTP $actual)"
    PASS=$((PASS + 1))
  else
    echo "[FAIL] $name (expected HTTP $expected, got $actual)"
    FAIL=$((FAIL + 1))
  fi
}

# The API returns indented JSON ("field": "value", with a space after the
# colon), so extraction must not assume a compact "field":"value" layout.
extract_field() {
  local json="$1"
  local field="$2"
  echo "$json" | grep -o "\"$field\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/'
}

echo "== Backend health =="
code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/health")
check "GET /api/health" 200 "$code"

echo "== Backend database health =="
code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/health/database")
check "GET /api/health/database" 200 "$code"

echo "== Backend AI health =="
code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/health/ai")
check "GET /api/health/ai" 200 "$code"
curl -s "$BASE_URL/api/health/ai"
echo

echo "== Embedding service health =="
code=$(curl -s -o /dev/null -w "%{http_code}" "$EMBEDDING_URL/health")
check "GET $EMBEDDING_URL/health" 200 "$code"

echo "== Qdrant reachable =="
code=$(curl -s -o /dev/null -w "%{http_code}" "$QDRANT_URL/")
check "GET $QDRANT_URL/" 200 "$code"

echo "== Sample protocol loading =="
response=$(curl -s -X POST "$BASE_URL/api/protocols/use-sample")
protocol_id=$(extract_field "$response" "protocolId")
if [ -n "$protocol_id" ]; then
  echo "[PASS] POST /api/protocols/use-sample (protocolId=$protocol_id)"
  PASS=$((PASS + 1))
else
  echo "[FAIL] POST /api/protocols/use-sample (no protocolId in response: $response)"
  FAIL=$((FAIL + 1))
fi

if [ -n "$protocol_id" ]; then
  echo "== Criteria extraction (fallback-capable) =="
  extract_response=$(curl -s -X POST "$BASE_URL/api/protocols/$protocol_id/extract-criteria")
  criteria_count=$(echo "$extract_response" | grep -o '"criterionId"' | wc -l)
  if [ "$criteria_count" -gt 0 ]; then
    echo "[PASS] POST /api/protocols/$protocol_id/extract-criteria ($criteria_count criteria)"
    PASS=$((PASS + 1))
  else
    echo "[FAIL] POST /api/protocols/$protocol_id/extract-criteria (no criteria returned: $extract_response)"
    FAIL=$((FAIL + 1))
  fi

  echo "== Approve criteria =="
  curl -s -X POST "$BASE_URL/api/protocols/$protocol_id/criteria/approve" \
    -H "Content-Type: application/json" -d '{}' > /dev/null
  echo "[INFO] Approved all extracted criteria."

  echo "== Generate question bank =="
  qb_response=$(curl -s -X POST "$BASE_URL/api/protocols/$protocol_id/generate-question-bank")
  if echo "$qb_response" | grep -q '"demographics"'; then
    echo "[PASS] POST /api/protocols/$protocol_id/generate-question-bank"
    PASS=$((PASS + 1))
  else
    echo "[FAIL] POST /api/protocols/$protocol_id/generate-question-bank (unexpected response: $qb_response)"
    FAIL=$((FAIL + 1))
  fi

  echo "== Question sections endpoint =="
  code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/protocols/$protocol_id/questions/sections")
  check "GET /api/protocols/$protocol_id/questions/sections" 200 "$code"

  echo "== Screening session + next-question endpoint =="
  session_response=$(curl -s -X POST "$BASE_URL/api/screening-sessions" \
    -H "Content-Type: application/json" \
    -d "{\"protocolId\":\"$protocol_id\",\"patientAlias\":\"Smoke-Test-Patient\"}")
  session_id=$(extract_field "$session_response" "sessionId")

  if [ -n "$session_id" ]; then
    echo "[PASS] POST /api/screening-sessions (sessionId=$session_id)"
    PASS=$((PASS + 1))
    code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/screening-sessions/$session_id/next-question")
    check "GET /api/screening-sessions/$session_id/next-question" 200 "$code"

    echo "== AI-assisted early-stop (numeric-range disqualifying criterion) =="
    early_stop_checked=false
    for i in $(seq 1 15); do
      nq=$(curl -s "$BASE_URL/api/screening-sessions/$session_id/next-question")
      if ! echo "$nq" | grep -q '"hasNextQuestion"[[:space:]]*:[[:space:]]*true'; then
        break
      fi

      qid=$(extract_field "$nq" "questionId")
      answer_type=$(extract_field "$nq" "answerType")
      if [ -z "$qid" ]; then
        break
      fi

      # The first canTriggerEarlyStop question with a numeric answer type is the
      # age-vs-required-range case: answer with a disqualifying value and confirm
      # the backend (not just the yes/no heuristic) recommends stopping.
      if echo "$nq" | grep -q '"canTriggerEarlyStop"[[:space:]]*:[[:space:]]*true' && [ "$answer_type" = "number" ]; then
        ans_response=$(curl -s -X POST "$BASE_URL/api/screening-sessions/$session_id/answers" \
          -H "Content-Type: application/json" \
          -d "{\"questionId\":\"$qid\",\"response\":\"16\"}")
        if echo "$ans_response" | grep -q '"earlyStopRecommended"[[:space:]]*:[[:space:]]*true'; then
          echo "[PASS] Disqualifying numeric answer (16) returns earlyStopRecommended=true"
          PASS=$((PASS + 1))
        else
          echo "[FAIL] earlyStopRecommended was not true after a disqualifying numeric answer: $ans_response"
          FAIL=$((FAIL + 1))
        fi
        early_stop_checked=true
        break
      fi

      default_response="N/A"
      if [ "$answer_type" = "yes_no" ]; then
        default_response="Yes"
      fi
      curl -s -X POST "$BASE_URL/api/screening-sessions/$session_id/answers" \
        -H "Content-Type: application/json" \
        -d "{\"questionId\":\"$qid\",\"response\":\"$default_response\"}" > /dev/null
    done

    if [ "$early_stop_checked" != "true" ]; then
      echo "[FAIL] Could not locate a numeric canTriggerEarlyStop question within 15 answers"
      FAIL=$((FAIL + 1))
    fi

    echo "== End session early + summary after early stop =="
    end_response=$(curl -s -X POST "$BASE_URL/api/screening-sessions/$session_id/end")
    if echo "$end_response" | grep -q '"status"[[:space:]]*:[[:space:]]*"Completed"'; then
      echo "[PASS] POST /api/screening-sessions/$session_id/end"
      PASS=$((PASS + 1))
    else
      echo "[FAIL] POST /api/screening-sessions/$session_id/end (unexpected response: $end_response)"
      FAIL=$((FAIL + 1))
    fi

    code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE_URL/api/screening-sessions/$session_id/summary")
    check "GET /api/screening-sessions/$session_id/summary (after early end)" 200 "$code"
  else
    echo "[FAIL] POST /api/screening-sessions (no sessionId in response: $session_response)"
    FAIL=$((FAIL + 1))
  fi
fi

echo
echo "===================================="
echo "Smoke test results: $PASS passed, $FAIL failed"
echo "===================================="

if [ "$FAIL" -gt 0 ]; then
  exit 1
fi
exit 0
