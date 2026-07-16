#!/usr/bin/env bash
# unturned-godot test runner (Phase 1 of docs/TESTING_PROPOSAL.md).
# One command, one output grammar, machine- AND human-readable:
#   [SUITE] <name> | PASS/FAIL | <p> passed, <f> failed in <t>s
#   [SUMMARY] TOTAL: <P> passed, <F> failed | first failure: <name> | trx: <dir>
# Exit: 0 = clean, 1 = test failure, 2 = infrastructure failure (build/dotnet error).
#
# Layers (fable's proposal): L0 = engine-free `dotnet test` (this phase). L1 = batched in-engine
# TestHost (phase 2, not built yet). L2 = visual golden PNGs (phase 4, not built yet).
#
# Usage: ./test.sh [--l0] [--l1] [--visual] [--all] [--only <glob>] [--failfast] [-h]
set -uo pipefail
cd "$(dirname "$0")"

RESULTS="${UG_TEST_RESULTS:-.testresults}"
ONLY="*"; FAILFAST=0; RUN_L0=0; RUN_L1=0; RUN_VISUAL=0

while [ $# -gt 0 ]; do
  case "$1" in
    --l0) RUN_L0=1 ;;
    --l1) RUN_L1=1 ;;
    --visual) RUN_VISUAL=1 ;;
    --all) RUN_L0=1; RUN_L1=1; RUN_VISUAL=1 ;;
    --only) ONLY="$2"; shift ;;
    --failfast) FAILFAST=1 ;;
    -h|--help)
      grep -E '^# ' "$0" | sed 's/^# //'; exit 0 ;;
    *) echo "unknown arg: $1 (see --help)"; exit 2 ;;
  esac
  shift
done
# default = the fast, always-available set (currently just L0; becomes --l0 --l1 in phase 2)
if [ $RUN_L0 -eq 0 ] && [ $RUN_L1 -eq 0 ] && [ $RUN_VISUAL -eq 0 ]; then RUN_L0=1; fi

rm -rf "$RESULTS"; mkdir -p "$RESULTS"
TOTAL_PASS=0; TOTAL_FAIL=0; FIRST_FAILURE=""; INFRA_FAIL=0

run_suite() {  # $1 = suite dir under tests/
  local dir="$1" name; name="$(basename "$dir")"
  case "$name" in $ONLY) ;; *) return ;; esac   # --only glob filter
  local log="$RESULTS/$name.log"
  dotnet test "$dir" -c Debug --nologo -v q \
    --logger "trx;LogFileName=$name.trx" --results-directory "$RESULTS" >"$log" 2>&1
  local code=$?
  # the dotnet summary line: "Passed!/Failed!  - Failed: F, Passed: P, Skipped: S, Total: T, Duration: D ms"
  local line; line="$(grep -E 'Failed:[[:space:]]*[0-9]+, Passed:' "$log" | tail -1)"
  if [ -z "$line" ]; then   # no summary -> build/restore/crash = infra failure, not a test failure
    echo "[SUITE] $name | ERROR | build or runner failure (see $log)"
    grep -E 'error|Build FAILED|MSB[0-9]+|Unhandled exception' "$log" | head -3 | sed 's/^/         /'
    INFRA_FAIL=1
    [ $FAILFAST -eq 1 ] && finish
    return
  fi
  local f p dur
  f="$(sed -E 's/.*Failed:[[:space:]]*([0-9]+),.*/\1/' <<<"$line")"
  p="$(sed -E 's/.*Passed:[[:space:]]*([0-9]+),.*/\1/' <<<"$line")"
  dur="$(sed -E 's/.*Duration:[[:space:]]*([0-9.]+ *m?s).*/\1/' <<<"$line")"
  TOTAL_PASS=$((TOTAL_PASS + p)); TOTAL_FAIL=$((TOTAL_FAIL + f))
  if [ "$f" -eq 0 ] && [ $code -eq 0 ]; then
    echo "[SUITE] $name | PASS | $p passed in $dur"
  else
    echo "[SUITE] $name | FAIL | $f failed, $p passed in $dur"
    # simplest-failing-case-first: name the failed tests (from the TRX) + a copy-pasteable repro
    if [ -f "$RESULTS/$name.trx" ]; then
      grep 'outcome="Failed"' "$RESULTS/$name.trx" | grep -oE 'testName="[^"]+"' | sed -E 's/testName="([^"]+)"/  ✗ \1/' | head -12
    fi
    echo "  repro: ./test.sh --only $name"
    [ -z "$FIRST_FAILURE" ] && FIRST_FAILURE="$name"
    [ $FAILFAST -eq 1 ] && finish
  fi
}

finish() {
  local status name
  if [ $INFRA_FAIL -eq 1 ]; then status="INFRA-ERROR"; else [ $TOTAL_FAIL -eq 0 ] && status="ok" || status="FAILURES"; fi
  echo "[SUMMARY] TOTAL: $TOTAL_PASS passed, $TOTAL_FAIL failed${FIRST_FAILURE:+ | first failure: $FIRST_FAILURE} | $status | trx: $RESULTS"
  if [ $INFRA_FAIL -eq 1 ]; then exit 2; fi
  [ $TOTAL_FAIL -eq 0 ] && exit 0 || exit 1
}

if [ $RUN_L0 -eq 1 ]; then
  echo "== L0: engine-free unit tests (dotnet test) =="
  for d in tests/*/; do compgen -G "${d}*.csproj" >/dev/null && run_suite "$d"; done
fi
if [ $RUN_L1 -eq 1 ]; then
  echo "== L1: in-engine tests -- NOT BUILT YET (phase 2 of docs/TESTING_PROPOSAL.md) =="
fi
if [ $RUN_VISUAL -eq 1 ]; then
  echo "== L2: visual golden tests -- NOT BUILT YET (phase 4 of docs/TESTING_PROPOSAL.md) =="
fi
finish
