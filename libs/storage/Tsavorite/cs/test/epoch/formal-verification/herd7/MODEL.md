# What the herd7 tests actually say

This file explains the logic of the reduced assembly: which shared words exist,
what each thread does to them, and what outcome would constitute a bug. Read it
before `litmus/`; read `REDUCTION.md` for how the reduced code was derived from
the raw JIT dumps.

## The tests come in pairs

Every hazard below is tested twice: once against the instruction stream we used
to emit, and once against the one we emit now. Only the pair is an argument.
`Never` on its own is equally consistent with "the architecture forbids this"
and "the test is mis-encoded and forbids everything", so the control — which
must come back `Sometimes` — is what makes the fix's `Never` mean anything.

| Pair | Control | Fix | The whole difference |
|---|---|---|---|
| `x86-announce-sb` | `-main` | `-fixed` | `XCHG` targets `tid`, plain `MOV` publishes the announce → `XCHG` targets `lce` and carries it |
| `arm64-announce-sb` | `-main` | `-fixed` | `CASAL` targets `tid`, plain `STR` publishes the announce → `CASAL` targets `lce` and carries it |
| `x86-refresh-mp` | `-main` | `-fixed` | *none* — `Volatile.Read` is a plain `MOV` on x86, so the two streams are identical |
| `arm64-refresh-mp` | `-main` | `-fixed` | `LDR` of `CurrentEpoch` → `LDAPR` |
| `arm64-release` | `-plainstore` | `-fixed` | `STR XZR` → `STLR XZR`; the control is a counterfactual, not code that ever shipped |
| `arm64-release-loadstore` | `-main` | `-fixed` | `STR XZR` → `STLR XZR` |
| `x86-composed` | `-main` | `-fixed` | the whole sequence; on x86 only the announce change is visible, the other two are no-ops |
| `arm64-composed` | `-main` | `-fixed` | the whole sequence: announce onto the claim `CASAL`, `LDAPR` refresh, and `STLR` unpublish, all at once |

`x86-release-loadstore-main` is deliberately unpaired: x86-TSO preserves
Load→Store, so the hazard cannot arise and there is nothing to fix.

`./run.sh --pairs` prints this table, and `./run.sh --pair <name>` diffs the two
files and runs both halves, which is the fastest way to see that the delta is
the single instruction claimed above and nothing else.

## The shared state

`LightEpoch` keeps a cache-line-aligned table of 64-byte `Entry` structs. Only
the first two fields matter here:

| Symbol | Field | Type | Offset |
|---|---|---|---|
| `lce` | `tableAligned[entry].localCurrentEpoch` | `long` | Entry + 0x00 |
| `tid` | `tableAligned[entry].threadId` | `int` | Entry + 0x08 |
| `cur` | `LightEpoch.CurrentEpoch` | `long` | this + 0x30 |

Two further locations model the caller's object lifetime, exactly as the TLA+
specs in `../tla/` do:

| Symbol | Meaning |
|---|---|
| `unlinked` | the reclaimer has removed the object from the structure; a reader that still sees `0` believes the object is live and will dereference it |
| `freed` | the reclaimer has concluded it is safe to reclaim and has released the memory |

## The protocol

A reader runs `Resume()` = `Acquire()` + `ProtectAndDrain()`, does its work,
then `Suspend()` = `Release()`.

```
Reader                                  Reclaimer
------                                  ---------
Acquire:                                unlink the object        -> W unlinked
  read cur                              bump the epoch           -> RMW cur
  claim a slot                          scan the table           -> R lce
  announce the epoch      -> W lce      if no slot blocks it,
  publish threadId        -> W tid         free the object       -> W freed
ProtectAndDrain:
  read cur                -> R cur
  re-announce             -> W lce
critical section:
  test unlinked           -> R unlinked
  dereference
Release:
  clear threadId          -> W tid
  clear the slot          -> W lce
```

The safety property is the one the TLA+ specs call `NoUseAfterFree`: the
reclaimer must never set `freed` while the reader is inside the critical
section.

Note where the reclaimer's fence already is. `BumpCurrentEpoch` is
`Interlocked.Increment`, which compiles to `lock xadd` on x86 and `LDADDAL` on
AArch64. It sits *between* the unlink store and the scan load, so **the
reclaimer's half of the ordering is already correct in every variant**. Every
question below is about the reader's half.

## Hazard 1 — the announce vs the scan (store buffering)

`litmus/{x86,arm64}-announce-sb-{main,fixed}.litmus`

The reader publishes `lce` and then loads `unlinked`. The reclaimer publishes
`unlinked` (then fences) and then loads `lce`. That is the store-buffering
shape: each thread stores to one location and loads the other.

The bad outcome is that **both** loads miss:

```
exists (reader sees unlinked=0  /\  reclaimer sees lce=0)
```

The reader concludes the object is live and dereferences it; the reclaimer
concludes no thread is protected, computes a `SafeToReclaimEpoch` that covers
the trigger epoch, and frees it. Use-after-free.

Store buffering is *not* closed by release/acquire — it needs a full barrier on
each side. The reclaimer has one. On `main` the reader does not: its claim is a
locked RMW on `tid`, but the announce is a **separate plain store to `lce`
after it**, with nothing between that store and the load of `unlinked`.

The fix makes the claim CAS write `lce` itself, so the announce *is* the locked
RMW and the reader's store-to-load pair is fenced. No barrier is added — the
CAS was already there; only the word it targets changed.

This hazard is architecture-independent, and both x86-TSO and AArch64 permit
it. That matches the runtime evidence: `../Litmus/QuarantineLitmus.cs`
reproduces it on ordinary x86 hardware.

## Hazard 2 — the refresh vs the bump (message passing)

`litmus/{x86,arm64}-refresh-mp-{main,fixed}.litmus`

The reclaimer publishes state and then bumps the epoch. A refreshing reader
reads the epoch and then reads that state. The bad outcome is the classic
message-passing violation:

```
exists (reader sees the new epoch  /\  reader sees the stale state)
```

This is a load-load question, so the answer is architecture-dependent:

- **x86-TSO never reorders load-load**, so `main` is already safe here, and
  `Volatile.Read` compiles to a plain `mov`. The x86 pair of tests is
  deliberately byte-identical for that reason: it documents that the fix
  neither helps nor costs anything on x86.
- **AArch64 does permit it.** On `main` the JIT merges the `CurrentEpoch` read
  into an `LDP` with the table pointer — an ordinary unordered pair load — and
  nothing stops the later data load from being satisfied first. `Volatile.Read`
  changes it to `LDAPR`, which orders every subsequent load after it.

Note that the announce store in `ProtectAndDrain` stays plain in both variants.
That is deliberate and safe: at refresh time the slot is already non-zero, and
the refresh only ever moves it *forward*. A scan that misses a delayed refresh
reads the older, smaller epoch, which yields a smaller `SafeToReclaimEpoch` —
the conservative direction. Only the `0 -> epoch` transition is
ordering-sensitive, and that one is carried by the claim CAS.

## Hazard 3 — unpublishing the slot vs the next claimer

`litmus/arm64-release-{plainstore,fixed}.litmus`

This hazard **exists only because of the fix**. Once `lce` is the word the claim
CAS targets, `lce` — not `tid` — is the slot-ownership word, so `Release()` must
clear `tid` first and hand the slot over by clearing `lce` last. If the `lce`
clear were an ordinary store, the two could be observed out of order: another
thread could win the claim CAS on `lce` and write its own `tid`, only for the
departing thread's `tid` clear to land afterwards and erase it.

```
exists (lce = claimed  /\  tid = 0)
```

`Volatile.Write` emits `STLR` on AArch64, which keeps the `tid` clear ordered
before the handover. The `plainstore` row is a **counterfactual** — it is not
code any variant emits — included to show the `STLR` is load-bearing rather
than decorative.

## Hazard 4 — the critical section vs unpublishing the slot (Load→Store)

`litmus/arm64-release-loadstore-{main,fixed}.litmus`,
`litmus/x86-release-loadstore-main.litmus`

Hazard 3 asks what the *next* owner of the slot sees. This one asks what the
departing reader itself is still doing. The reader dereferences the object and
then, in `Release()`, clears its slot:

```
LDR X2,[data]      ; the dereference, inside the critical section
STR XZR,[lce]      ; Release: the slot is now free  -- PLAIN on main
```

Nothing in that pair is a barrier, and AArch64 permits Load→Store reordering.
The slot clear can therefore become visible to other cores before the
dereference has been satisfied. A reclaimer that scans in that window sees `0`,
concludes nobody is protected, and frees the object — and only then does the
reader's load get its value.

```
exists (the dereference returns the poison)
```

x86-TSO preserves Load→Store, so the shape cannot arise there; the x86 row is
included to record that dissociation, with the AArch64 row as its live control.

On `main` the slot clear is a plain `STR`, so this is open. The fix's
`Volatile.Write` emits `STLR`, which is ordered after *every* preceding access
including the dereference, so it closes this as well as hazard 3. That is worth
stating explicitly: the release store was introduced to make the slot handover
safe, and it turns out to be doing a second, independent job.

## The whole sequence, composed

`litmus/{x86,arm64}-composed-{main,fixed}.litmus`

The four hazards above are each a two- or three-instruction shape studied in
isolation. That is how they are understood, but it is not by itself an argument
that the *program* is correct: a decomposition can miss an interaction between
the shapes, and it can also be unfaithful in a way that happens not to show up
one shape at a time.

The composed tests run the entire reader against the entire reclaimer:

```
P0:  Acquire            read cur; claim; announce
     ProtectAndDrain    re-read cur; re-announce
     critical section   test unlinked; dereference only if it still looks live
     Release            clear tid; clear the slot

P1:  unlink; bump (LDADDAL); scan the slot; free if it is clear
```

with every memory access of the reduced listing present, in program order. The
bad outcome is stated directly as the thing we actually care about — the
reader's dereference returning a value the reclaimer wrote *after* freeing:

```
exists (0:X5=99)
```

Both `main` rows are `Sometimes` and both `fixed` rows are `Never`. The `fixed`
rows are the substantive ones: they say that no execution of the whole
sequence, on either architecture's real model, frees an object under a reader
that is still using it.

One detail in these tests is load-bearing and easy to get wrong. The reader's
dereference is **guarded** by its test of `unlinked`. A reader only ever reaches
an object through the structure, so a reader that observes the object already
unlinked never held a pointer to it and cannot be a victim. Drop that guard and
the test reports a violation for the *fixed* code — an execution in which the
reclaimer legitimately frees while nothing is protected, and a later reader
dereferences an object it was never given. That is a bug in the encoding, not in
`LightEpoch`, and it is exactly the kind of thing composed tests are prone to.

## Relationship to the other evidence in this folder

| Layer | What it establishes | Where |
|---|---|---|
| TLA+ | the *algorithm* is correct under an abstract store-buffer / weak-memory model, exhaustively over all interleavings | `../tla/` |
| herd7 | the *instructions the JIT actually emits* are correct under the vendors' own architecture models, for x86-64 and AArch64 | here |
| Litmus stress test | the bug is observable on real x86 hardware, and the fix suppresses it | `../Litmus/` |

herd7 fills the gap the other two leave: the TLA+ specs model memory ordering
by hand and say nothing about codegen, and the stress test only runs on the
hardware you happen to have. herd7 checks real emitted AArch64 instructions
against Arm's own model without needing an Arm machine to run on.

The converse limitation is worth stating plainly: herd7 validates the emitted
code against the *architecture's* memory model. The .NET memory model is a
separate, stronger contract, and a future JIT is free to emit different
instructions. These tests are evidence about the code that ships today, which
is why the raw dumps are committed alongside them.
