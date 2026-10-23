# Executable Tsavorite verification foundations

This directory contains checked abstract safety proofs and bounded concurrency
models, not a proof of the complete C# engine. The
[roadmap](../FORMAL-VERIFICATION.md) describes the larger effort. The source
baseline is upstream `e154dcdbae18f62272ce09e400c9fb3b9bb48aec`.

## Run

Use PowerShell 7, Docker, and Lean **4.19.0** with `lake` on `PATH`. There are no
Lean package dependencies. `lean-toolchain` pins the release; `-LeanBin` can select
an isolated installation instead of a globally installed toolchain.

```powershell
.\Test-Formal.ps1 -LeanBin '<Lean installation>\bin'
.\Test-Formal.ps1 -LeanBin '<Lean installation>\bin' -IncludeDeep
```

The runner builds `Dockerfile.tla`, checks the TLA+ **1.7.4** jar's SHA-256,
translates PlusCal, parses all modules, builds Lean with warnings as errors, prints
the main theorem axioms, and executes positive and negative model configurations.
It requires actual invariant failures in negative controls, not just a nonzero
exit code. Parser errors, failed downloads, deadlocks, and incomplete searches do
not count as successful controls.

On the author's workstation, reuse the existing Docker helpers without installing
a JDK:

```powershell
.\Test-Formal.ps1 -TlaHelpers 'Q:\src\tla-helpers' -LeanBin '<Lean installation>\bin' -IncludeDeep
```

Logs go to a unique temporary directory printed by the runner, or to
`-OutputDirectory '<directory>'`. This includes the Lean build, axiom audit,
translator/parser logs, TLC state counts, and negative-control traces. Keep those
logs when reporting results. TLA+ translations are checked in; generated default
configurations and backups are ignored. Never use the translator's default
`Core.cfg`, `Epoch.cfg`, or `Checkpoint.cfg` as an evidence configuration.

The Java runtime base image is a major-version tag, not an immutable image digest.
The proof/checker versions are pinned, but the complete execution environment is
not bit-for-bit hermetic.

## Claims and source correspondence

| Component | Lean claim | TLC model | Source boundary and gaps |
| --- | --- | --- | --- |
| Core operations | `Core.reachable_refines`: every reachable store equals replay of its ghost command history. The inductive invariant ties each prepared proposal to the lock owner, current store, and ghost generation. `Core.read_agrees` derives read agreement. | Read, upsert, delete, and read-modify-write over two keys in one collision bucket; mutable publication and an append-like path with retry. | Maps to [read](../src/core/Index/Tsavorite/Implementation/InternalRead.cs), [upsert](../src/core/Index/Tsavorite/Implementation/InternalUpsert.cs), [read-modify-write](../src/core/Index/Tsavorite/Implementation/InternalRMW.cs), [delete](../src/core/Index/Tsavorite/Implementation/InternalDelete.cs), and [transient locking](../src/core/Index/Tsavorite/Implementation/Locking/TransientLocking.cs). No hash-chain representation, callback effects/exceptions, record lifetimes, or pending disk operations are proved. |
| Epoch reclamation | `Epoch.reachable_safe`: the abstract transition system inductively preserves announcement bounds, pointer tracking, access discipline, and no-protected-reclamation. `Epoch.protected_not_freed` projects lifetime safety; `Epoch.reclamation_safe` proves the eligibility rule excludes protected pointers. | Two readers, epoch announcements, retirement, arbitrary eligible reclamation, and pointer invalidation on refresh. Both access discipline and no-protected-reclamation are checked. | Maps to [LightEpoch](../src/core/Epochs/LightEpoch.cs) acquire/refresh/suspend/drain behavior. Preservation is proved for the abstract actions, not all C# callers. The model uses resource generations, not an allocator/page-buffer implementation. |
| Checkpoint/recovery | `Checkpoint.reachable_safe`: every committed or recovered snapshot in a reachable state belongs to durable artifacts. `Checkpoint.recovered_is_durable` projects the recovery obligation. | Two candidate versions, asynchronous capture/flush/commit, two crashes that discard pending state, and recovery from the committed marker. | Maps to [checkpoint tasks](../src/core/Index/Checkpointing/HybridLogCheckpointSMTask.cs), [metadata commit](../src/core/Index/Recovery/Checkpoint.cs), [manager contract](../src/core/Index/Recovery/ICheckpointManager.cs), and [recovery](../src/core/Index/Recovery/Recovery.cs). The model abstracts a durable snapshot as a single artifact. It does not model actual driver restart, phase/version coordination, fuzzy-record filtering, index/object logs, partial writes, device errors, or recovery-page eviction. |

### Core proof interpretation

The Lean `Step` relation includes acquire, prepare, publish, discard, and release.
Prepare saves the store observed under the single collision-bucket lock.
Publication consumes that proposal; release drops any unpublished proposal.
The ownership invariant prevents another client from publishing between prepare
and publish. The inductive proof establishes replay refinement for arbitrary
client/key/value types, arbitrary initial maps, and finite reachable traces;
read-modify-write uses an arbitrary pure total function.

The TLC model specializes values to `{0, 1, 2}` plus absence and uses a modulo-three
increment for read-modify-write. Reads check an independent reference map.
The reference update recomputes read-modify-write from the current abstract value;
the implementation update uses its prepared candidate. Removing exclusion can
therefore expose a lost update instead of updating two identical expressions.
Rejected append attempts do not modify either map. The one optional rejected
attempt conservatively introduces retry scheduling; it is not a claim that the
fixed-index locked C# path necessarily experiences that compare-and-swap failure.

The `generation` variable is a ghost publication counter, not the C# checkpoint
version or a replacement for the actual hash-entry compare-and-swap word. Lean
records reads as identity history entries; TLC increments its counter only for
writes. Their state spaces are not identical, and no equivalence theorem between
the formalisms is claimed. In-place and append publication have the same abstract
map effect; the proof does not verify the physical publication protocol.

The model serializes all operations in one collision bucket, including reads.
Shared-reader locking and concurrent operations on different buckets require
additional correspondence arguments; bounded model termination is not a claim
that all C# session schedules terminate.

Replay refinement and sampled read agreement are **not yet an invocation/response
history linearizability proof**. Returning statuses, outputs, exceptions, and
real-time call boundaries still need a refinement argument against C# sessions.
No .NET weak-memory correctness result follows from these sequentially consistent
abstract transitions.

### Epoch proof interpretation

The Lean model begins with no announcements, pointers, retirements, or freed
resources. Acquire announces an epoch; load can only obtain an unretired
generation. Refresh and suspend invalidate the pointer. Retirement timestamps a
generation and advances the epoch. Reclamation requires every active announcement
to exceed that timestamp. The proof derives access discipline and lifetime safety
by induction over arbitrary finite reachable traces and arbitrary client/resource
types. Freed generations are never reused as the same logical resource.

TLC additionally separates the reader's announcement from its pointer load and
retirement publication from the epoch bump. Lean combines retirement and bump in
one abstract transition. The theorem about that abstraction does not by itself
justify the finer-grained implementation; the bounded checks cover the stated
split-action configurations, not a general refinement proof. Physical buffer reuse,
drain-list capacity, epoch disposal/reset, and the .NET publication order remain
outside these claims.

### Durability proof interpretation

The Lean snapshot type is arbitrary; it may contain a map and version. Begin
selects a candidate, flush adds it to durable artifacts, and commit requires that
artifact. Crash removes volatile pending/recovered state without changing committed
metadata or durable artifacts; recover selects committed metadata. The invariant
is proved inductively over arbitrarily many such abstract transitions and crashes.

In TLC, version identifiers are never reused. Captured contents are immutable for
each version, so recovery checks the artifact against the selected capture even
if a later version has committed. Crashing during a new checkpoint preserves the
previous committed marker. A completion that mutates artifacts after a real
restart is not modeled: this is a persistence-ordering contract, not an executable
recovery implementation.

Flush means durable storage in this abstraction; asynchronous device completion
only justifies that transition when the device/manager contract establishes
durability. A process crash preserves those artifacts. Power loss, torn writes,
lost acknowledgments, corrupt metadata, and non-atomic commits are excluded.
The proof does not establish that a candidate is the correct checkpoint version;
the record/version-consistency proof remains a prerequisite for that claim.

## Configurations and negative controls

| Configuration | Bounds and checks |
| --- | --- |
| `CoreSingle` | One client, two keys, three values, two operations; safety and fair termination. |
| `CoreSmall` | Two clients, two colliding keys, three values, one operation each; safety and fair termination. |
| `CoreTwoOps` (`-IncludeDeep`) | Two clients, two keys, three values, two operations each; safety and fair termination. |
| `EpochSmall` | Two readers, three generations (two retirements), two protected operations each; safety, access discipline, fair termination. |
| `CheckpointSmall` | Two candidate versions, two possible captured values, two crashes; durable commit/recovery and fair termination. |
| `CoreBroken` | Removes lock exclusion; must violate `DataRefinesMap` through an inconsistent prepared mutation. |
| `EpochBroken` | Allows reclamation without announcement draining; must violate `NoProtectedReclamation`. |
| `CheckpointBroken` | Allows commit without flushing; must violate `CommittedIsDurable`. |

The processes are weakly fair. Termination also relies on finite operation/crash
budgets and the absence of unmodeled permanent I/O failures or stalled callbacks.
There is no fairness assumption that forces a crash to occur before a checkpoint;
all allowed schedules are explored. Intentional terminal states use the PlusCal
translator's termination stuttering action; built-in deadlock checking remains on.
Do not infer unbounded liveness from the finite checks.

Negative controls are model mutations, **not discovered C# vulnerabilities or
regression reproductions**. A real bug claim requires mapping a minimized trace
to an implementation execution and adding a focused source-level regression.

The [recorded results](RESULTS.md) list completed checks and their exact finite
bounds/state counts.

## Remaining work

The models and proofs establish useful foundations but do not complete any
roadmap milestone that requires source-level correspondence or end-to-end
checkpoint consistency. Next extend the operation contract to responses and
physical records, prove epoch access discipline for the actual C# caller actions, connect
checkpoint phase/version cuts to captured maps, and model actual recovery
selection, index rebuilding, fuzzy records, and memory pressure. The TLA+/Lean
correspondence, C# memory-ordering audit, and composition proofs remain explicit
obligations. Garnet replication remains out of scope.
