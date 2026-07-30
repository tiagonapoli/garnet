# What was removed from the JIT output, and why

`jit/*.asm` is verbatim RyuJIT output. `jit/reduced/*.reduced.asm` is what is
left after removing everything herd7 cannot model or that cannot affect the
outcome. This file accounts for every category of removal, so the reduction can
be audited without re-deriving it.

The guiding rule: **an instruction survives only if it accesses a location that
is shared between the two modelled threads.** For LightEpoch that is exactly
three words — `localCurrentEpoch`, `threadId` and `CurrentEpoch` — plus the two
flags that model the caller's object lifetime (`unlinked`, `freed`).

---

## 1. Removals that cannot change the result

### 1.1 Frame setup and teardown

```
x86    push r15 / push r14 / ... / sub rsp, 32   ... add rsp, 32 / pop ... / ret
arm64  stp fp, lr, [sp, #-0x20]! / str x19, [sp, #0x18] / mov fp, sp   ... ret lr
```

Stack traffic to a frame no other thread can name. herd7 models shared memory
only; a private stack slot has no observers.

### 1.2 Runtime and GC scaffolding

```
x86    test byte ptr [(reloc)], 1          ; class-init check
       call CORINFO_HELP_GET_GCSTATIC_BASE
       mov  rcx, qword ptr GS:[0x0058]     ; TEB -> thread statics
       cmp  byte ptr [rbx], bl             ; null check
arm64  bl   CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR
       ldrsb wzr, [x22]                    ; null check
```

None of this touches the epoch words. Note in particular that **no GC write
barrier appears anywhere in these methods**: every field involved
(`localCurrentEpoch`, `threadId`, `CurrentEpoch`, `SafeToReclaimEpoch`) is a
primitive `long`/`int` inside a `[StructLayout(LayoutKind.Explicit)]` struct
reached through a raw pointer, so there is no `CORINFO_HELP_ASSIGN_REF` to
strip. That is why "remove the .NET-isms" is a small, mechanical job here rather
than a judgement call.

### 1.3 Thread-private state

```
x86    mov ecx, dword ptr [rbp+0x10]       ; Metadata.threadId  (thread static)
       movzx rax, word ptr [rbp+0x14]      ; Metadata.Entries   (thread static)
arm64  ldr  w1, [x21, #0x10] / ldrh w0, [x21, #0x14]
```

`Metadata` is `[ThreadStatic]`. Each modelled thread has its own copy, so these
reads and writes are unobservable across threads. Where the *value* matters
(the thread id written into the slot) the reduced listing keeps a symbolic
`[Metadata.threadId]` read, and the litmus tests substitute a constant.

### 1.4 Address arithmetic

```
x86    movsxd rcx, dword ptr [rsi] / shl rcx, 6 / lea rsi, [rax+4*rcx+8]
arm64  sbfiz  x0, x0, #6, #32 / add x1, x1, x0
```

Computes `&tableAligned[entry]`. In the litmus tests the entry index is fixed,
so the whole computation collapses into the symbolic location `lce` or `tid`.
Retaining it would only add register traffic herd7 would have to enumerate.

### 1.5 The Murmur3 hash and the slot-probe loop

```
x86    shr eax,16 / xor eax,ecx / imul ecx,eax,<k> / ... / div edx:eax, ecx
arm64  the same sequence in w-registers
```

`ReserveEntryForThread` hashes the managed thread id to pick a starting slot,
then probes linearly until a free one is found. This is thread-private
arithmetic followed by a loop with a data-dependent trip count — herd7 requires
bounded, explicitly unrolled control flow, and an unbounded probe loop would
make the state space infinite.

The reduction keeps **one** probe (`cmp`/`cbnz` against the slot) followed by
**one** claim attempt, and drops the retry edge. This is sound for what the
tests ask: they model the thread that *wins* the claim, and the losing paths
re-enter the same code with a different slot address, which cannot make a
forbidden outcome allowed.

### 1.6 Calls

```
x86    call [Tsavorite.core.LightEpoch:Drain(long):this]
       call [System.Threading.SemaphoreSlim:Release(int):int:this]
       call System.Environment:get_CurrentManagedThreadId():int
arm64  blr  x2
```

herd7 has no call/return. `Drain` and `SuspendResume` are conditional slow
paths guarded by `drainCount`/`waiterCount`; the litmus tests model the fast
path where those counters are zero, which is the path that runs on every
protected operation. The one effect of `Drain` that matters — freeing the
object — is modelled directly as the `freed`/`unlinked` flags.

### 1.7 Control-flow-only volatile reads

```
arm64  add x0, x19, #68 / ldapr w0, [x0] / cmp w0, #0 / ble ...   ; drainCount
       add x0, x19, #64 / ldapr w0, [x0]                          ; waiterCount
```

`drainCount` and `waiterCount` are read only to decide whether to take a slow
path. They are dropped along with the slow paths in §1.6. Dropping an *acquire*
load is the conservative direction: it can only make more reorderings legal, so
it cannot manufacture a spurious "Never".

### 1.8 Diffable-mode placeholders

`DOTNET_JitDisasmDiffable=1` replaces every address and large immediate with
`0xD1FFAB1E` so the dumps are byte-stable across runs and reviewable in a diff.
Those `movz/movk` triples and `mov rcx, 0xD1FFAB1E` sequences are address
materialisation and are dropped with §1.4.

---

## 2. Substitutions — where the litmus test is not literally the dump

These are the only places where the reduced code is not a subset of the real
code. Each one is a limitation of herd7's instruction coverage, not a modelling
choice, and each is in the safe direction.

| Real instruction | In the litmus test | Why, and why it is sound |
|---|---|---|
| `lock cmpxchg dword ptr [tid], edx` (x86) | `XCHG [tid],EDX` | herd7's X86 backend rejects `CMPXCHG` (`I_CMPXCHG not implemented`) and rejects a `LOCK` prefix on it. `XCHG` with a memory operand is implicitly locked and is modelled as a full barrier, exactly as `LOCK CMPXCHG` is. The tests only ever model the thread that *wins* the CAS, and for the winner an unconditional exchange performs the same write with the same ordering. |
| `lock cmpxchg qword ptr [lce], rdi` (x86) | `XCHG [lce],EDX` | As above. |
| `lock xadd qword ptr [cur], rbx` (x86) | `XCHG [cur],EDX` | Only the *ordering* contributed by the bump matters (it drains the reclaimer's store buffer between the unlink and the scan), not the arithmetic. Both are locked RMWs, so both are full barriers. The epoch value is supplied as a constant. |
| `ldp x1, x0, [x19, #0x28]` (arm64) | `LDR X2,[X0]` | The `LDP` loads the `tableAligned` pointer and `CurrentEpoch` in one instruction. Only `CurrentEpoch` is shared with the other modelled thread. `LDP` and `LDR` are both plain, unordered loads, so splitting it changes nothing about ordering — it only drops an access to a location nobody writes. |
| `ldaddal x3, x4, [cur]` (arm64) | kept as `LDADDAL` | No substitution; herd7 models it directly. |
| `casal`, `stlr`, `ldapr` (arm64) | kept verbatim | No substitution; herd7 models all three directly. |

## 3. Collapses — many instructions to one

| Real code | In the litmus test | Why |
|---|---|---|
| `ComputeNewSafeToReclaimEpoch`: a `kTableSize`-iteration loop taking the min of every non-zero slot, then `SafeToReclaimEpoch = oldest - 1` | a single `LDR`/`MOV` of one slot, with the comparison folded into the `exists` clause | The tests have one reader, so exactly one slot is non-zero and the min over the table is that slot. The `- 1` and the `triggerEpoch <=` comparison are pure thread-local arithmetic on already-loaded values; expressing the outcome as a condition on the loaded value is equivalent and keeps the state space small. |
| `Drain`'s scan of `drainList` and the `Interlocked.CompareExchange` that claims a trigger slot | the `freed` outcome, expressed as the `exists` clause | The drain list is a separate data structure with its own (already interlocked) protocol. What the epoch fix has to guarantee is only *whether the scan concludes it is safe to free*, which is decided entirely by the slot read. |
| the linear probe over table entries | one probe + one claim | See §1.5. |

## 4. Additions — locations that are not LightEpoch fields

The two flags `unlinked` and `freed` do not exist in `LightEpoch`. They stand
for the caller's object lifetime: `unlinked` is set by the reclaimer before it
bumps the epoch, and the reader tests it to decide whether it is safe to
dereference. They are modelled exactly as `tla/epoch/fixes/*.tla` models them,
so the herd7 tests and the TLA+ specs are checking the same protocol at two
different levels of abstraction. Without them there is nothing to state a
use-after-free *about*: the epoch words alone are just integers.

## 5. What was deliberately *not* removed

- Both plain stores in `Release()`, in the order the JIT emits them. The order
  is load-bearing after the fix (see `arm64-release-*.litmus`).
- The `ProtectAndDrain` re-announce, even though it is a plain store in both
  variants. It is kept precisely to show that leaving it plain does not reopen
  the hole the claim CAS closes.
- The plain `ldr`/`mov` that loads `CurrentEpoch` as the CAS operand in
  `Acquire`. It is genuinely unordered, and keeping it lets herd7 confirm that
  the following CASAL is what makes it safe.
