#!/usr/bin/env bash
# Robotic manager (phase 5 of docs/TESTING_PROPOSAL.md): run the FULL test suite (L0 + L1 + visual
# goldens) against fresh origin/main in a dedicated clone, and on failure print a ready-to-post
# report: the [SUMMARY] line, the first failing test + its repro, and the commit blame range since
# the last green run.
#
# NOT wired to any cron on purpose -- a human enables it. Ready-to-enable snippet (crontab -e):
#   17 9 * * * $HOME/projects/unturned-godot/tools/nightly_tests.sh >> $HOME/nightly-tests.log 2>&1
# (09:17 UTC = overnight US-Eastern. The report block between the [NIGHTLY] markers is what a
#  human/bot posts to the channel on failure.)
#
# State: a dedicated clone + the last-good sha live under $UG_NIGHTLY_DIR
# (default ~/.cache/unturned-godot-nightly) -- the dev working tree is never touched.
# Exit codes mirror test.sh: 0 green, 1 test failure, 2 infrastructure failure.
set -uo pipefail

REPO_URL="${UG_NIGHTLY_REPO:-https://github.com/strawberry-cow38/unturned-godot.git}"
BASE="${UG_NIGHTLY_DIR:-$HOME/.cache/unturned-godot-nightly}"
CLONE="$BASE/repo"
LASTGOOD_FILE="$BASE/lastgood"
LOG="$BASE/nightly-$(date +%Y%m%d-%H%M).log"
mkdir -p "$BASE"

say() { echo "[NIGHTLY] $*"; }

# --- fresh main in the dedicated clone ---
if [ ! -d "$CLONE/.git" ]; then
  say "first run: cloning $REPO_URL"
  git clone --quiet "$REPO_URL" "$CLONE" || { say "ABORT: clone failed"; exit 2; }
fi
cd "$CLONE"
git fetch --quiet origin main            || { say "ABORT: fetch failed"; exit 2; }
git reset --hard --quiet origin/main     || { say "ABORT: reset failed"; exit 2; }
git clean -fdq
HEAD_SHA="$(git rev-parse --short HEAD)"
LASTGOOD="$(cat "$LASTGOOD_FILE" 2>/dev/null || true)"
say "testing origin/main @ $HEAD_SHA (last good: ${LASTGOOD:-unknown})"

# --- the one command ---
./test.sh --all >"$LOG" 2>&1
CODE=$?

SUMMARY="$(grep -E '^\[SUMMARY\]' "$LOG" | tail -1)"
if [ $CODE -eq 0 ]; then
  echo "$HEAD_SHA" >"$LASTGOOD_FILE"
  say "GREEN @ $HEAD_SHA -- $SUMMARY"
  exit 0
fi

# --- failure report (the block a human/bot posts) ---
say "-------- REPORT BEGIN --------"
say "${CODE}: $([ $CODE -eq 2 ] && echo INFRASTRUCTURE FAILURE || echo TEST FAILURES) on origin/main @ $HEAD_SHA"
echo "${SUMMARY:-[SUMMARY] missing -- runner died early, see $LOG}"
# the first failing test with its detail + repro lines
grep -E -m1 -A3 '^\[(TEST|SUITE)\].*\| (FAIL|ERROR) ' "$LOG" | sed 's/^/  /'
if [ -n "$LASTGOOD" ] && git cat-file -e "$LASTGOOD" 2>/dev/null; then
  say "blame range (lastgood $LASTGOOD..$HEAD_SHA):"
  git log --oneline "$LASTGOOD..HEAD" | sed 's/^/  /'
else
  say "no last-good sha recorded -- first failing run, no blame range"
fi
say "full log: $LOG"
say "-------- REPORT END --------"
exit $CODE
