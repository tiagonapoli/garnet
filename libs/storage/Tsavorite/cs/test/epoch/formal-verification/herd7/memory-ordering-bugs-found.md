# Memory-ordering issues found by the herd7 pass

Findings from checking the **actual instructions RyuJIT emits** for
`LightEpoch` against the vendors' own architecture memory models. Raw dumps are
in `jit/`, the reduction is accounted for in `REDUCTION.md`, and the protocol
each test encodes is described in `MODEL.md`.

Both dumps were taken with .NET 10.0.100, `DOTNET_TieredCompilation=0`
(FullOpts), `DOTNET_JitDisasmDiffable=1`:

- **x86-64** — Windows, RyuJIT `BLENDED_CODE for generic X64 + VEX`.
- **AArch64** — Ubuntu 24.04 on Azure `Standard_D8ps_v5` (Ampere Altra),
  RyuJIT `BLENDED_CODE for generic ARM64 on Unix`.

Reproduce with `docker run --rm garnet-lightepoch-herd`.

---

## x86-64

### X1 — The announce store can be buffered past the reclaimer's scan (BUG, fixed)

**Status:** present on `origin/main`; closed by the fix.
**Test:** `litmus/x86-announce-sb-main.litmus` → `Sometimes` (violated),
`litmus/x86-announce-sb-fixed.litmus` → `Never`.

`Acquire()` on `main` claims the slot with a locked RMW on the wrong word and
then announces with a plain store:

```asm
lock cmpxchg dword ptr [TID], edx      ; claim: full barrier, but on threadId
mov      rax, qword ptr [CUR]
mov      qword ptr [LCE], rax          ; the announce: PLAIN store
...
                                       ; nothing between here and the first
                                       ; load of the object being protected
```

x86-TSO lets that store sit in the store buffer while the same core's
subsequent loads complete. The reclaimer is already fenced on its side — the
`lock xadd` of `BumpCurrentEpoch` sits between its unlink store and its table
scan — but store buffering needs a barrier on *both* sides, so the reader's
missing half is enough. The reclaimer reads the slot as `0`, concludes no
thread is protected, and frees memory the reader is about to dereference.

The fix moves the CAS onto the announce word itself:

```asm
mov      rdi, qword ptr [CUR]
lock cmpxchg qword ptr [LCE], rdi      ; claim AND announce, one locked RMW
mov      dword ptr [TID], ecx          ; plain; the slot is already ours
```

The locked RMW count is unchanged; only the word being CASed differs. herd7
confirms the cycle is then forbidden under `x86tso.cat`.

This is the same defect that `../Litmus/QuarantineLitmus.cs` reproduces on real
x86 hardware, now confirmed against Intel's architectural model rather than
only observed empirically.

### X2 — The refresh path is *not* a bug on x86 (no action)

**Test:** `litmus/x86-refresh-mp-main.litmus` → `Never`.

`ProtectAndDrain` reads `CurrentEpoch` and then reads state the bumper
published before bumping. That is message passing, which is a load-load
question, and x86-TSO does not reorder loads. `main` is already safe here.

`Volatile.Read` compiles to a plain `mov` on x86, so the fixed and unfixed
`ProtectAndDrain` are **byte-identical** — 152 bytes of code each, instruction
for instruction. This is the codegen-level confirmation that the acquire load
costs nothing on x86.

### X3 — No issue found in `Release()` on x86

`Release()` emits its two plain stores in source order in both variants. See
the correction note at the end of this file.

Neither store-store nor Load→Store is reorderable under x86-TSO, so both the
slot-handover question (A3) and the premature-unpublish question (A4) below are
closed on x86 without any barrier. `litmus/x86-release-loadstore-main.litmus`
records the second of those as `Never`.

### X4 — The composed sequence

**Test:** `litmus/x86-composed-main.litmus` → `Sometimes` (violated),
`litmus/x86-composed-fixed.litmus` → `Never`.

Running the whole reader against the whole reclaimer, rather than one hazard
shape at a time, reproduces X1 and nothing else on x86, and the fixed sequence
admits no use-after-free at all. See "The whole sequence, composed" below.

---

## AArch64

### A1 — The same announce store-buffering bug (BUG, fixed)

**Status:** present on `origin/main`; closed by the fix.
**Test:** `litmus/arm64-announce-sb-main.litmus` → `Sometimes` (violated),
`litmus/arm64-announce-sb-fixed.litmus` → `Never`.

Identical in shape to X1. The AArch64 codegen makes the two words involved very
explicit, because the claim CAS changes width as well as target:

| | `main` | fixed |
|---|---|---|
| claim | `casal w2, w1, [TID]` (32-bit, threadId) | `casal x2, x21, [LCE]` (64-bit, localCurrentEpoch) |
| announce | `str x0, [LCE]` — plain | *(carried by the CAS)* |
| threadId publish | *(carried by the CAS)* | `str w1, [TID]` — plain |

### A2 — `CurrentEpoch` is read by an unordered `LDP` on the refresh path (BUG, ARM-only, fixed)

**Status:** present on `origin/main`; closed by the fix. **Cannot occur on
x86.**
**Test:** `litmus/arm64-refresh-mp-main.litmus` → `Sometimes` (violated),
`litmus/arm64-refresh-mp-fixed.litmus` → `Never`.

This is the finding that justified doing the AArch64 pass at all: it is a real
ordering hole that x86 hardware cannot expose, so no amount of stress testing
on x86 would ever have found it.

On `main`, RyuJIT merges the read of `CurrentEpoch` with the read of the
`tableAligned` pointer into a single load-pair:

```asm
ldp     x0, x2, [x19, #0x28]     ; tableAligned AND CurrentEpoch, one plain LDP
str     x2, [LCE]                ; re-announce
```

`LDP` is an ordinary, unordered load. Nothing prevents a later load — of state
the bumper published *before* it bumped the epoch — from being satisfied ahead
of it. The reader can therefore observe the new epoch together with stale
state.

With the fix the `LDP` disappears and the epoch read becomes an acquire load:

```asm
add     x3, x19, #48
ldapr   x3, [CUR]                ; Volatile.Read -> LDAPR (load-acquire RCpc)
str     x3, [LCE]                ; re-announce, still plain (see MODEL.md)
```

`LDAPR` is RCpc rather than RCsc, which is weaker than `LDAR` only with respect
to ordering against a prior `STLR`. For message passing it is sufficient, and
herd7 confirms the violation is forbidden under `aarch64.cat`.

### A3 — `Release()` must use a release store once the fix is in (constraint, satisfied)

**Status:** not a bug in any shipped variant; a requirement the fix introduces
and meets.
**Test:** `litmus/arm64-release-plainstore.litmus` → `Sometimes`
(counterfactual), `litmus/arm64-release-fixed.litmus` → `Never`.

Making the claim CAS target `localCurrentEpoch` also makes it the
slot-*ownership* word. `Release()` therefore has to clear `threadId` first and
hand the slot over last — and the handover must carry release ordering, or the
two clears can be observed out of order and the departing thread can erase the
`threadId` of the thread that just claimed the slot.

`Volatile.Write` emits exactly that:

```asm
str     wzr, [TID]               ; clear threadId FIRST, plain
stlr    xzr, [LCE]               ; hand the slot over, STLR
```

The counterfactual row shows the same code with a plain `STR` in place of the
`STLR` and is violated, which is what makes the `STLR` demonstrably
load-bearing rather than defensive. Note this is also why the store order is
*inverted* relative to `main`: on `main`, `threadId` is the ownership word, so
clearing `localCurrentEpoch` first is the correct order there.

### A4 — The dereference can be reordered past the slot clear (BUG, ARM-only, fixed)

**Status:** present on `origin/main`; closed by the fix. **Cannot occur on
x86.**
**Test:** `litmus/arm64-release-loadstore-main.litmus` → `Sometimes`
(violated), `litmus/arm64-release-loadstore-fixed.litmus` → `Never`,
`litmus/x86-release-loadstore-main.litmus` → `Never`.

This one was missed on the first pass, because all three earlier hazards are
about what *other* threads see of the reader's publication. This one is about
the reader outliving its own announcement.

The reader dereferences the object and then unpublishes its slot:

```asm
ldr     x2, [data]               ; the dereference, inside the critical section
str     xzr, [LCE]               ; Release: PLAIN store on main
```

AArch64 permits Load→Store reordering, and nothing here forbids it. The slot
clear can become visible to other cores before the dereference has been
satisfied. A reclaimer scanning in that window reads `0`, concludes nothing is
protected, frees the object — and only then does the reader's load return, from
memory that is no longer live.

The fix's `Volatile.Write` emits `STLR`, which is ordered after every preceding
access including the dereference, so the object cannot be observed as
unprotected until the reader is genuinely done with it.

Note what this means for A3. The release store was introduced because making
`localCurrentEpoch` the ownership word created a handover requirement. It turns
out to be doing a **second, independent job**: closing a use-after-free that
exists in `main` today and has nothing to do with the handover. Weakening it
back to a plain store would reopen A4 even if A3 were somehow addressed another
way.

x86-TSO preserves Load→Store, so the shape cannot arise there — the x86 row
records that, with the AArch64 row as the live control proving the encoding can
detect the hazard when the architecture permits it.

A4 was invisible to the TLA+ suite for two structural reasons: neither
`StoreBuffer.tla` nor `WeakMemory.tla` can express a load that has been issued
and not yet bound, and the critical section in those specs is not a memory
access at all — `CasAnnounceOneReader`'s `Dereference` step only flips a flag.
Both are now addressed by
[`CasAnnounceReleaseLoadStore.tla`](../tla/CasAnnounceReleaseLoadStore.tla),
which splits the dereference into issue and bind and reproduces the three rows
above: `tso`+plain HOLDS, `arm`+plain VIOLATED, `arm`+release HOLDS.

---

## The whole sequence, composed

**Tests:** `litmus/{x86,arm64}-composed-main.litmus` → `Sometimes` (violated),
`litmus/{x86,arm64}-composed-fixed.litmus` → `Never`.

Each finding above is a two- or three-instruction shape studied in isolation.
That is how they are understood, but it is not on its own an argument that the
*program* is correct: a decomposition can miss an interaction between shapes.

The composed tests run the entire reader — `Acquire` → `ProtectAndDrain` →
critical section → `Release` — against the entire reclaimer — unlink → bump →
scan → free — with every memory access of the reduced listing present, in
program order, and state the bad outcome directly as the reader's dereference
returning a value the reclaimer wrote after freeing.

Both `main` rows are violated; **both `fixed` rows are `Never`**. No execution
of the whole fixed sequence, under either architecture's own model, frees an
object under a reader that is still using it. That is the strongest statement
this folder makes, and it is the one that says the four-way decomposition above
did not miss anything.

### A false positive worth recording

The first composed encoding reported the *fixed* code as violated on AArch64.
It was wrong, and the way it was wrong is instructive.

That version had the reader dereference the object unconditionally. herd7 duly
found an execution in which the reclaimer unlinks, bumps, scans an empty table,
legitimately frees — and only *then* does the reader arrive, claim a slot, and
dereference. Real code cannot do that: a reader reaches an object by walking the
structure, and the object was removed from the structure before any of this
began, so no reader arriving afterwards ever holds a pointer to it.

Adding the guard the protocol actually has — test `unlinked`, dereference only
if the object still looks live — makes `fixed` `Never` while leaving both `main`
rows violated. The guard is documented in `REDUCTION.md` §4.1 as an addition,
because it is one: it is not in the dump, and the tests would be unsound
without it.

---

## Correction to an earlier claim in this investigation

An earlier round of disassembly work in this workstream recorded that RyuJIT
**reorders the two plain stores in `main`'s `Release()`**, emitting
`localCurrentEpoch = 0` before `threadId = 0` and thereby inverting source
order.

**That was a misreading, and it is retracted.** These fresh dumps show
`main`'s source order is already `localCurrentEpoch = 0; threadId = 0`
(`LightEpoch.cs` on `origin/main`), and the JIT preserves it on both x86 and
AArch64. The fixed variant's source order is the inverse, and that is likewise
preserved. No compiler store reordering was observed anywhere in these methods
on either architecture.

The argument for the `Volatile.Write` in `Release()` does not depend on that
claim — it rests on A3 above, which is about the *hardware* being permitted to
reorder the two stores, not the compiler.

---

## Summary

| # | Hazard | x86-64 | AArch64 | Closed by |
|---|---|---|---|---|
| X1 / A1 | announce vs reclaim scan (store buffering) | **violated on main** | **violated on main** | CAS on `localCurrentEpoch` |
| X2 / A2 | refresh vs bump (message passing) | safe on main | **violated on main** | `Volatile.Read` → `LDAPR` |
| X3 / A3 | release vs next claimer (store-store) | safe (TSO orders store-store) | requires a release store | `Volatile.Write` → `STLR` |
| X3 / A4 | dereference vs slot clear (Load→Store) | safe (TSO orders load-store) | **violated on main** | `Volatile.Write` → `STLR` |
| X4 | the whole sequence, composed | **violated on main** | **violated on main** | all of the above together |

Three distinct use-after-free defects are present on `main`: one on both
architectures, two reachable only on AArch64. All are closed by the fix, which
adds no barrier on x86 and two ordered accesses on AArch64. No issue was found
in the fixed variant on either architecture, either shape-by-shape or composed.
All seventeen litmus results match their expectations.
