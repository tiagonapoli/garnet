#!/usr/bin/env bash
# Run the herd7 litmus tests for LightEpoch and report each result against its
# expectation. Requires herd7 on PATH (see Dockerfile).
#
# Pass a substring to run only the matching rows, e.g.
#   ./run.sh arm64-refresh
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
LITMUS="$HERE/litmus"
failures=0
matched=0
FILTER="${1:-}"

# run <test> <Never|Sometimes> <description>
#
# "Never"     = herd7 found no execution satisfying the test's `exists` clause,
#               i.e. the hazard is forbidden by the architecture's own model.
# "Sometimes" = herd7 found at least one, i.e. the hazard is architecturally
#               permitted. For a *-main row that is the bug; for the
#               counterfactual row it is the point being demonstrated.
run() {
  local name="$1" expected="$2" description="$3" output status observed

  [[ -z "$FILTER" || "$name" == *"$FILTER"* ]] || return 0
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

note() { [[ -n "$FILTER" ]] || echo "$@"; }

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
