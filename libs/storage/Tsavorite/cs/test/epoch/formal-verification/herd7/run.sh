#!/usr/bin/env bash
# Run the herd7 litmus tests for LightEpoch and report each result against its
# expectation. Requires herd7 on PATH (see Dockerfile).
#
# Pass a substring to run only the matching rows, e.g.
#   ./run.sh arm64-refresh
#
# Almost every test is half of a control/fix pair: the control shows the hazard
# is architecturally permitted by the code we used to emit, the fix shows it is
# forbidden by the code we emit now. A result of "Never" on its own proves very
# little -- a mis-encoded test is also Never -- so the pairing is the argument,
# not either file alone. To see a pair as a diff and run both halves together:
#   ./run.sh --pair arm64-announce-sb
#   ./run.sh --pairs            # list every pair and its one-line delta
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
LITMUS="$HERE/litmus"
failures=0
matched=0
SELECTED=""

# The control/fix pairs, and the single instruction that separates each one.
# Kept here rather than in prose in the .litmus headers so there is one place
# that says what the suite is actually comparing.
PAIRS=(
  "x86-announce-sb            | x86-announce-sb-main            | x86-announce-sb-fixed            | XCHG targets tid and a plain MOV publishes the announce  ->  XCHG targets lce and carries it | x1--x86-64"
  "arm64-announce-sb          | arm64-announce-sb-main          | arm64-announce-sb-fixed          | CASAL targets tid and a plain STR publishes the announce ->  CASAL targets lce and carries it | a1--aarch64"
  "x86-refresh-mp             | x86-refresh-mp-main             | x86-refresh-mp-fixed             | none: Volatile.Read is a plain MOV on x86, so the two instruction streams are identical | x2--x86-64"
  "arm64-refresh-mp           | arm64-refresh-mp-main           | arm64-refresh-mp-fixed           | LDR of CurrentEpoch  ->  LDAPR | a2--aarch64"
  "arm64-release              | arm64-release-plainstore        | arm64-release-fixed              | STR XZR clears the slot  ->  STLR XZR (control is a counterfactual, not code we ever emitted) | a3--aarch64"
  "arm64-release-loadstore    | arm64-release-loadstore-main    | arm64-release-loadstore-fixed    | STR XZR clears the slot  ->  STLR XZR | a4--aarch64"
  "x86-composed               | x86-composed-main               | x86-composed-fixed               | the whole sequence; on x86 only the announce change is visible, the other two are no-ops | x4--x86-64"
  "arm64-composed             | arm64-composed-main             | arm64-composed-fixed             | the whole sequence: announce onto the claim CASAL, LDAPR refresh, STLR unpublish, all at once | the-whole-sequence-composed"
)
# x86-release-loadstore-main is deliberately unpaired: x86-TSO preserves
# Load->Store, so the hazard cannot arise there and there is nothing to fix.

field() { echo "$1" | awk -F'|' -v n="$2" '{ gsub(/^ +| +$/, "", $n); print $n }'; }

list_pairs() {
  echo "Control/fix pairs. The delta column is the whole difference between them."
  echo ""
  for entry in "${PAIRS[@]}"; do
    printf '  %-26s %s\n' "$(field "$entry" 1)" "$(field "$entry" 4)"
  done
  echo ""
  echo "  x86-release-loadstore      (unpaired: x86-TSO preserves Load->Store, nothing to fix)"
  echo ""
  echo "Each pair is written up in memory-ordering-bugs-found.md; ./run.sh --pair <name>"
  echo "prints the exact section."
}

show_pair() {
  local want="$1" entry name control fix delta doc found=0
  for entry in "${PAIRS[@]}"; do
    name="$(field "$entry" 1)"
    [[ "$name" == "$want" ]] || continue
    found=1
    control="$(field "$entry" 2)"
    fix="$(field "$entry" 3)"
    delta="$(field "$entry" 4)"
    doc="$(field "$entry" 5)"

    echo "############################################################"
    echo "# $name"
    echo "# control : $control"
    echo "# fix     : $fix"
    echo "# delta   : $delta"
    echo "# why     : memory-ordering-bugs-found.md#$doc"
    echo "############################################################"
    echo ""
    diff -u "$LITMUS/$control.litmus" "$LITMUS/$fix.litmus"
    echo ""
    # Exact names, so a pair whose name is a prefix of another pair's
    # (arm64-release vs arm64-release-loadstore) does not drag it in.
    SELECTED="$control $fix"
  done
  if [[ $found -eq 0 ]]; then
    echo "No such pair: '$want'. Known pairs:" >&2
    list_pairs >&2
    exit 1
  fi
}

case "${1:-}" in
  --pairs)
    list_pairs
    exit 0
    ;;
  --pair)
    [[ $# -ge 2 ]] || { echo "usage: ./run.sh --pair <name>   (see ./run.sh --pairs)" >&2; exit 1; }
    show_pair "$2"
    # Fall through to run both halves, so the diff is followed by its results.
    FILTER=""
    ;;
  *)
    FILTER="${1:-}"
    ;;
esac

# run <test> <Never|Sometimes> <description>
#
# "Never"     = herd7 found no execution satisfying the test's `exists` clause,
#               i.e. the hazard is forbidden by the architecture's own model.
# "Sometimes" = herd7 found at least one, i.e. the hazard is architecturally
#               permitted. For a *-main row that is the bug; for the
#               counterfactual row it is the point being demonstrated.
run() {
  local name="$1" expected="$2" description="$3" output status observed

  if [[ -n "$SELECTED" ]]; then
    [[ " $SELECTED " == *" $name "* ]] || return 0
  else
    [[ -z "$FILTER" || "$name" == *"$FILTER"* ]] || return 0
  fi
  matched=$((matched + 1))

  echo ""
  echo "############################################################"
  echo "# $name"
  echo "# expected: $expected ($description)"
  echo "############################################################"

  output="$(mktemp)"
  if herd7 "$LITMUS/$name.litmus" >"$output" 2>&1; then status=0; else status=$?; fi
  cat "$output"

  observed="$(awk '/^Observation/ { print $3 }' "$output")"
  if [[ $status -eq 0 && "$observed" == "$expected" ]]; then
    echo "# ---- PASS: $expected ----"
  else
    echo "# ---- FAIL: expected $expected, observed '${observed:-<none>}' (herd7 exit $status) ----"
    failures=$((failures + 1))
  fi
  rm -f "$output"
}

note() { [[ -n "$FILTER" || -n "$SELECTED" ]] || echo "$@"; }

note "===================== LightEpoch herd7 litmus matrix ====================="
note "# Every test below is reduced from real RyuJIT output captured on x86-64"
note "# and AArch64 hardware; see jit/ for the raw dumps, REDUCTION.md for what"
note "# was removed and why, and MODEL.md for what each test means."
note "#"

note ""
note "--- Hazard 1: the announce (Acquire) vs the reclaimer's scan -- store buffering ---"
run x86-announce-sb-main       Sometimes "plain announce store is not ordered before the load of the unlink flag"
run x86-announce-sb-fixed      Never     "the claim RMW carries the announce, closing P0's half of the SB cycle"
run arm64-announce-sb-main     Sometimes "same hole as on x86; AArch64 does not make it any narrower"
run arm64-announce-sb-fixed    Never     "CASAL on localCurrentEpoch orders the announce before the flag load"

note ""
note "--- Hazard 2: the refresh (ProtectAndDrain) vs the bumper -- message passing ---"
run x86-refresh-mp-main        Never     "x86-TSO does not reorder load-load, so main is already safe here"
run x86-refresh-mp-fixed       Never     "Volatile.Read is a plain MOV on x86: identical code, identical result"
run arm64-refresh-mp-main      Sometimes "plain load of CurrentEpoch lets the later data load be hoisted"
run arm64-refresh-mp-fixed     Never     "LDAPR orders every subsequent load after the epoch read"

note ""
note "--- Hazard 3: unpublishing the slot (Release) vs the next claimer ---"
note "# Only meaningful once localCurrentEpoch is the ownership word, i.e. after the"
note "# fix. The first row is a counterfactual: it is NOT code we emit."
run arm64-release-plainstore   Sometimes "counterfactual: a plain store here would let the tid clear wipe the new owner"
run arm64-release-fixed        Never     "STLR keeps the tid clear ordered before the slot is handed over"

note ""
note "--- Hazard 4: the critical section vs Release -- Load->Store ---"
note "# The reader's own dereference against its own slot clear. Distinct from"
note "# hazard 3: that one is about the NEXT owner of the slot, this one is about"
note "# the reader outliving its own announcement."
run arm64-release-loadstore-main  Sometimes "plain slot clear can be observed before the dereference is satisfied"
run arm64-release-loadstore-fixed Never     "STLR is ordered after every preceding load, including the dereference"
run x86-release-loadstore-main    Never     "x86-TSO preserves Load->Store, so this shape cannot arise there"

note ""
note "--- The whole sequence, composed ---"
note "# Acquire -> ProtectAndDrain -> critical section -> Release, against a full"
note "# reclaimer, rather than one hazard shape at a time. These are what say the"
note "# decomposition above did not miss an interaction between the shapes."
run x86-composed-main          Sometimes "the announce is still buffered past the dereference"
run x86-composed-fixed         Never     "no execution of the whole fixed sequence frees under a live reader"
run arm64-composed-main        Sometimes "announce buffering and the early slot clear are both open"
run arm64-composed-fixed       Never     "no execution of the whole fixed sequence frees under a live reader"

echo ""
if [[ $matched -eq 0 ]]; then
  echo "No tests matched filter '$FILTER'."
  exit 1
fi
if [[ $failures -eq 0 ]]; then
  echo "All $matched herd7 results matched their expectations."
else
  echo "$failures of $matched herd7 results did NOT match their expectations."
fi
exit $(( failures > 0 ? 1 : 0 ))
