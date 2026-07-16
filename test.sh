#!/usr/bin/env bash
# unturned-godot test runner (Phase 1 of docs/TESTING_PROPOSAL.md).
# One command, one output grammar, machine- AND human-readable:
#   [SUITE] <name> | PASS/FAIL | <p> passed, <f> failed in <t>s
#   [SUMMARY] TOTAL: <P> passed, <F> failed | first failure: <name> | trx: <dir>
# Exit: 0 = clean, 1 = test failure, 2 = infrastructure failure (build/dotnet error).
#
# Layers (fable's proposal): L0 = engine-free `dotnet test`. L1 = batched in-engine TestHost
# (one headless boot). L2 = visual golden PNGs (xvfb+lavapipe renders diffed vs tests/visual/golden;
# opt-in via --visual/--all, ~30s/scene; re-baseline with tools/visual_tests.py --update <name>).
#
# --report renders the run to a static HTML dashboard (tools/gen_report.py) after it finishes --
# served via Caddy at claw.bitvox.me/ugtests/ for at-a-glance review (UG_REPORT_DIR overrides the dir).
#
# Usage: ./test.sh [--l0] [--l1] [--visual] [--all] [--only <glob>] [--failfast] [--report] [-h]
set -uo pipefail
cd "$(dirname "$0")"

RESULTS="${UG_TEST_RESULTS:-.testresults}"
GODOT="${GODOT:-$HOME/godot46/Godot_v4.6-stable_mono_linux_arm64/Godot_v4.6-stable_mono_linux.arm64}"
ONLY="*"; FAILFAST=0; RUN_L0=0; RUN_L1=0; RUN_VISUAL=0; REPORT=0

while [ $# -gt 0 ]; do
  case "$1" in
    --l0) RUN_L0=1 ;;
    --l1) RUN_L1=1 ;;
    --visual) RUN_VISUAL=1 ;;
    --all) RUN_L0=1; RUN_L1=1; RUN_VISUAL=1 ;;
    --only) ONLY="$2"; shift ;;
    --failfast) FAILFAST=1 ;;
    --report) REPORT=1 ;;
    -h|--help)
      grep -E '^# ' "$0" | sed 's/^# //'; exit 0 ;;
    *) echo "unknown arg: $1 (see --help)"; exit 2 ;;
  esac
  shift
done
# default = the fast sub-60s set: engine-free logic (L0) + batched in-engine (L1). --visual (L2) is opt-in.
if [ $RUN_L0 -eq 0 ] && [ $RUN_L1 -eq 0 ] && [ $RUN_VISUAL -eq 0 ]; then RUN_L0=1; RUN_L1=1; fi

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

run_l1() {  # batched in-engine tests: build the game once, boot headless godot, run every GameTest, parse its report
  echo "== L1: in-engine tests (headless godot, one boot) =="
  if ! dotnet build game/UnturnedGodot.csproj -c Debug -v q -nologo >"$RESULTS/l1_build.log" 2>&1; then
    echo "[SUITE] L1 | ERROR | game build failed (see $RESULTS/l1_build.log)"
    grep -E 'error|Build FAILED' "$RESULTS/l1_build.log" | head -3 | sed 's/^/         /'
    INFRA_FAIL=1; return
  fi
  if [ ! -x "$GODOT" ]; then
    echo "[SUITE] L1 | ERROR | godot binary not found/executable: $GODOT (set \$GODOT)"; INFRA_FAIL=1; return
  fi
  local glob=""; [ "$ONLY" != "*" ] && glob="=$ONLY"
  local log="$RESULTS/l1.log"
  timeout 300 "$GODOT" --path game --headless -- "--tests$glob" >"$log" 2>&1
  grep -E '^\[TEST\]|^[[:space:]]+✗|^[[:space:]]+repro' "$log"   # per-test detail (human + agent)
  local summary; summary="$(grep -E '^\[L1\] passed=' "$log" | tail -1)"
  if [ -z "$summary" ]; then
    echo "[SUITE] L1 | ERROR | host never reported (boot/hang; see $log)"; INFRA_FAIL=1
    [ $FAILFAST -eq 1 ] && finish
    return
  fi
  local p f; p="$(sed -E 's/.*passed=([0-9]+).*/\1/' <<<"$summary")"; f="$(sed -E 's/.*failed=([0-9]+).*/\1/' <<<"$summary")"
  TOTAL_PASS=$((TOTAL_PASS + p)); TOTAL_FAIL=$((TOTAL_FAIL + f))
  if [ "$f" -eq 0 ]; then
    echo "[SUITE] L1 in-engine | PASS | $p passed"
  else
    echo "[SUITE] L1 in-engine | FAIL | $f failed, $p passed"
    [ -z "$FIRST_FAILURE" ] && FIRST_FAILURE="$(grep -E '^\[TEST\].*\| FAIL ' "$log" | head -1 | sed -E 's/^\[TEST\][[:space:]]+([^[:space:]]+).*/\1/')"
    [ $FAILFAST -eq 1 ] && finish
  fi
}

run_visual() {  # L2 golden-image tests: render each manifest scene via xvfb+lavapipe, diff vs the committed golden
  echo "== L2: visual golden tests (xvfb + lavapipe, ~30s/scene) =="
  local only=(); [ "$ONLY" != "*" ] && only=(--only "$ONLY")
  local log="$RESULTS/visual.log"
  GODOT="$GODOT" python3 tools/visual_tests.py "${only[@]}" | tee "$log"
  local summary; summary="$(grep -E '^\[VISUAL\] passed=' "$log" | tail -1)"
  if [ -z "$summary" ]; then
    echo "[SUITE] L2 visual | ERROR | runner never reported (see $log)"; INFRA_FAIL=1; return
  fi
  local p f i
  p="$(sed -E 's/.*passed=([0-9]+).*/\1/' <<<"$summary")"
  f="$(sed -E 's/.*failed=([0-9]+).*/\1/' <<<"$summary")"
  i="$(sed -E 's/.*infra=([0-9]+).*/\1/' <<<"$summary")"
  TOTAL_PASS=$((TOTAL_PASS + p)); TOTAL_FAIL=$((TOTAL_FAIL + f))
  if [ "$i" -gt 0 ]; then
    echo "[SUITE] L2 visual | ERROR | $i scene(s) failed to render"; INFRA_FAIL=1
  elif [ "$f" -eq 0 ]; then
    echo "[SUITE] L2 visual | PASS | $p passed"
  else
    echo "[SUITE] L2 visual | FAIL | $f failed, $p passed"
    [ -z "$FIRST_FAILURE" ] && FIRST_FAILURE="$(grep -E '^\[TEST\].*\| FAIL ' "$log" | head -1 | sed -E 's/^\[TEST\][[:space:]]+([^[:space:]]+).*/\1/')"
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

# Run the whole suite inside a group piped to tee, so the machine-readable grammar lands in
# run.log for gen_report.py. The left side of a pipe is a subshell, so finish()'s exit (and the
# failfast exits) terminate the run cleanly; PIPESTATUS[0] carries the real exit code back out.
{
  if [ $RUN_L0 -eq 1 ]; then
    echo "== L0: engine-free unit tests (dotnet test) =="
    for d in tests/*/; do compgen -G "${d}*.csproj" >/dev/null && run_suite "$d"; done
  fi
  if [ $RUN_L1 -eq 1 ]; then run_l1; fi
  if [ $RUN_VISUAL -eq 1 ]; then run_visual; fi
  finish
} 2>&1 | tee "$RESULTS/run.log"
CODE=${PIPESTATUS[0]}

if [ $REPORT -eq 1 ]; then
  if python3 tools/gen_report.py; then :; else echo "[REPORT] generation failed (non-fatal)"; fi
fi
exit "$CODE"
