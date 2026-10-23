# Tsavorite formal verification roadmap

**Status: proposed work; no models or proofs are implemented by this document.**

This roadmap covers the C# Tsavorite key-value engine: concurrent operations,
epoch-protected memory, checkpointing, and recovery. It was mapped against Garnet
upstream `main` at `e154dcdbae18f62272ce09e400c9fb3b9bb48aec`. Revalidate the source
mapping when implementation changes; the links below resolve within this checkout.

Garnet replication and append-only-log replay, cluster protocols, RESP commands,
and the C++ engine are excluded. The standalone `TsavoriteLog` API needs its own
specification; sharing allocator code does not make a key-value proof cover it.

## What the evidence will mean

Use TLA+ to expose interleavings and temporal failures in explicit finite models,
preferably written in PlusCal. Use Lean 4 to prove invariant preservation and
refinement of the corresponding abstract transition systems for arbitrary finite
populations, where the proof actually supports that generality.

A successful TLC run covers its recorded bounds, configuration, and assumptions,
not all executions. A checked Lean theorem covers its definitions and hypotheses,
not automatically the TLA+ specification or C# implementation. Initially, mapping
C# to the formal transitions is a reviewed argument, not a machine-checked
translation. Report those three evidence levels separately.

Do not label a subsystem "verified" without naming its source revision,
configuration, checked properties, assumptions, and outstanding refinement gaps.

## Verification boundary

Start with a fixed hash index, byte values, one store, no read cache, no
revivification, deterministic total callbacks, and ordinary session operations.
Keep a finite key/value domain in TLC; do not silently apply that bound to Lean.
Treat successful read, upsert, read-modify-write, delete, and not-found results as
observable responses. Pending is not completion: include its continuation and
response before checking a history. Model retry and cancellation separately.

Specify callback contracts before proving operations: successful mutation,
declined mutation, and exceptions cannot all be abstracted as a successful write.
The first core proof may exclude callback exceptions, but must list that exclusion.
Separate volatile operation completion from checkpoint durability; an acknowledged
write need not survive a crash unless the selected durability contract requires it.

Start concurrency models with sequentially consistent abstract actions. Audit each
mapped C# access for atomicity, visibility, ordering, and lifetime. In particular,
an `Interlocked` operation, a volatile access, a plain field assignment, and an
entire locked section are not interchangeable. Until that audit is discharged,
the model does not establish correctness under the .NET memory model.

For crash models, distinguish volatile state, issued writes, completed device
writes, and committed checkpoint metadata. Define the device/manager persistence
contract rather than assuming task completion or metadata-method return implies
power-loss durability. Initially assume completed durable writes remain intact,
allow incomplete writes/metadata to be unavailable, and crash at each modeled
publication boundary. Torn writes, corruption, arbitrary device failures, and
custom checkpoint managers require separate models or explicit assumptions.

## Source-to-property matrix

All rows are proposed obligations, not claims of established correctness.

| Target and source | Property and abstraction obligation | Existing test anchors |
| --- | --- | --- |
| [Read](src/core/Index/Tsavorite/Implementation/InternalRead.cs), [upsert](src/core/Index/Tsavorite/Implementation/InternalUpsert.cs), [read-modify-write](src/core/Index/Tsavorite/Implementation/InternalRMW.cs), [delete](src/core/Index/Tsavorite/Implementation/InternalDelete.cs) | Completed histories refine a sequential map and respect real-time order. Identify linearization separately for in-place mutation, new-record publication, tombstones, and not-found; do not assume all operations linearize at compare-and-swap. Failed publication and retries must not create duplicate abstract effects. | [BasicLockTests](test/BasicLockTests.cs), [record-operation tests](test/test.recordops/) |
| [Transient locking](src/core/Index/Tsavorite/Implementation/Locking/TransientLocking.cs), [pending continuations](src/core/Index/Tsavorite/Implementation/ContinuePending.cs) | Lock ownership and release match on every modeled exit; pending I/O revalidates its record/index assumptions before producing a response. Callback contracts are part of the obligation. | [BasicLockTests](test/BasicLockTests.cs), [session tests](test/test.session/) |
| [LightEpoch](src/core/Epochs/LightEpoch.cs), [allocator](src/core/Allocator/AllocatorBase.cs) | A reclamation action cannot free a page still reachable through a valid old-epoch access. Model acquire, refresh, suspend, publication, drain eligibility, callback execution, and page reuse separately. Refresh can relinquish old protection without suspending. | [ProtectionTests](test/test.epoch/ProtectionTests.cs), [DrainTests](test/test.epoch/DrainTests.cs), [scan failure tests](test/test.hlog/ScanIteratorEpochFailureTests.cs) |
| [Allocator address transitions](src/core/Allocator/AllocatorBase.cs) | Identify the lifecycle points where address ordering holds and prove page-level "no eviction before required flush and reader drain." Do not elevate an apparent field-order chain to an invariant without checking intermediate publication, reset, recovery, and null-device modes. | [Epoch tests](test/test.epoch/), [snapshot eviction tests](test/test.recovery/ObjectRecoverySnapshotEvictionTests.cs) |
| [StateMachineDriver](src/core/Index/Checkpointing/StateMachineDriver.cs), [phase/version representation](src/core/Index/Checkpointing/StateTransitions.cs), [hybrid-log machine](src/core/Index/Checkpointing/HybridLogCheckpointSM.cs), [full machine](src/core/Index/Checkpointing/FullCheckpointSM.cs) | Published phase/version, transition-in epoch drain, old-version transaction drain, and phase-task completion are distinct events. No transition may consume a prerequisite that has not completed; concurrent operations obey the checkpoint version boundary. | [StateMachineDriverTests](test/StateMachineDriverTests.cs) |
| [Fold-over task](src/core/Index/Checkpointing/FoldOverSMTask.cs), [snapshot task](src/core/Index/Checkpointing/SnapshotCheckpointSMTask.cs), [common task](src/core/Index/Checkpointing/HybridLogCheckpointSMTask.cs), [metadata commit](src/core/Index/Recovery/Checkpoint.cs) | Each committed checkpoint identifies a recoverable version under the declared persistence contract. Snapshot and fold-over have different flush/address obligations. Failure/abort must not advertise successful durable completion or strand later checkpoint waiters. | [StateMachineDriverTests](test/StateMachineDriverTests.cs), [CheckpointManagerTests](test/test.recovery/CheckpointManagerTests.cs), [IndexCheckpointIoTests](test/test.recovery/IndexCheckpointIoTests.cs) |
| [Recovery](src/core/Index/Recovery/Recovery.cs), [index recovery](src/core/Index/Recovery/IndexRecovery.cs), [manager contract](src/core/Index/Recovery/ICheckpointManager.cs) | Recovered observations equal the selected checkpoint's abstract version, including fuzzy-region filtering and the chosen `undoNextVersion` policy. Metadata selection, index reconstruction, object-log dependencies, and recovery-time eviction must not invent data or omit required checkpoint effects. | [SimpleRecoveryTest](test/test.recovery/SimpleRecoveryTest.cs), [RecoveryCheckTests](test/test.recovery/RecoveryCheckTests.cs), [ObjectRecoverySnapshotEvictionTests](test/test.recovery/ObjectRecoverySnapshotEvictionTests.cs) |

Tests anchor implementation behavior and future regression cases; their presence
does not establish a formal property. For example, `StateMachineDriverTests`
compares live post-checkpoint values with recovered old-version values. It is not
an exhaustive crash or interleaving exploration.

## Milestones and acceptance gates

### 1. Shared contracts and a core-operation pilot

Define a sequential map, invocation/response histories, record/index state,
operation statuses, and a concrete-to-abstract observation function. Give stable
names to transitions used by both formalisms and record their C# method/operation
mapping. Include two keys sharing a hash tag/bucket so collisions are not abstracted
away accidentally.

Begin the pilot with read and upsert, then add delete and read-modify-write before
claiming the baseline core scope. Model successful and failed publication, in-place
updates, lock acquisition/release, and retries. Select linearization points from
the actual source path and justify invisible steps as stuttering.

**Exit gate:** TLC exhausts a recorded configuration with two clients, two
colliding keys, three values, and a per-client operation budget of two; Lean proves
initial-state validity, invariant preservation for each transition, and refinement
to the map for the explicitly supported operations. Also run a single-client
configuration to catch initialization/termination errors. Deliberately removing
the publication/ownership guard must violate a named property in a separate
negative-control configuration. Any unfinished operation paths remain listed as
unsupported, not hidden behind a general theorem name.

### 2. Epoch reclamation and log lifecycle

Build on the core model with announced epochs, deferred actions, page residency,
read-only/flush/close transitions, and reuse generations. Model callback execution
on any eligible thread, including synchronous execution by the bumping thread.
Refreshing protection must invalidate assumptions that depend on an older epoch.

**Exit gate:** check two readers, one writer, two reusable page slots, and at least
two retire/reuse cycles. Prove in Lean that a valid protected access cannot
reference a reclaimed generation under the stated access discipline. Include a
negative control that omits the drain barrier. Record address invariants with their
lifecycle/observation points instead of assuming simultaneously sampled fields.
Reset and allocator-specific object lifetimes remain separate obligations until
their transitions are modeled.

### 3. Checkpoint version and persistence protocol

Compose the core and epoch contracts with the checkpoint phase machine. Represent
phase publication, epoch transition-in, old-version transaction counts, asynchronous
flushes, metadata commit, and completion notification separately. First cover
hybrid-log fold-over, then snapshot, then full checkpoints with index persistence.
Include fault/cancellation/abort paths and the ability to start a later checkpoint.

**Exit gate:** TLC checks each supported checkpoint mode with two operation clients,
two successive checkpoints, both version parities, and delayed/out-of-order flush
completions where allowed by the source contract. Lean proves that a successful
commit denotes the intended version and cannot precede its durability dependencies.
Keep a small pure state-machine proof distinct from the harder proof that the
record operations and version boundary actually produce a consistent checkpoint.
An early-metadata-commit mutation must fail a persistence property.

### 4. Crash and recovery

Add a crash action that discards volatile state and restarts from the explicitly
modeled durable artifacts. Define valid metadata and checkpoint selection, not an
unqualified "latest" order over checkpoint tokens. Model index rebuilding,
snapshot/main-log boundaries, fuzzy records, tombstones, and repeated recovery.
Use a checkpoint-version observation relation rather than the live map at crash
time. State which operations overlapping the checkpoint may be included.

**Exit gate:** explore a crash before/after every modeled flush and metadata
publication step, two checkpoint candidates, and a second crash during recovery.
Lean proves that each supported recovery path refines the committed version and
that restart/recovery preserves that relation. Negative controls must detect
selecting incomplete metadata and evicting a required recovery page too early.
Extend to object-log references and recovery with a smaller memory budget before
claiming support for those configurations.

### 5. Coverage expansion and implementation correspondence

After the baseline gates, extend one feature at a time: pending disk reads and
updates, read cache, revivification, index growth, compaction/truncation, streaming
snapshots, object values and callbacks, and transaction/version coordination beyond
the initial checkpoint abstraction. Each extension needs its own assumptions,
counterexample configuration, Lean obligations, and source/test mapping.

Do not claim complete engine verification while these paths are omitted. For
composed claims, prove that each component's guarantees satisfy the others'
assumptions; isolated passing models are insufficient. Reconcile TLA+ and Lean
transition definitions explicitly, including a table of correspondence and
representative shared traces. Proving that correspondence is a separate obligation;
matching names and traces alone do not prove equivalence.

## Model, proof, and regression discipline

For each later milestone, store reviewed specifications and proof sources under a
dedicated `formal/` directory beside this document: `tla/`, `lean/`, and a concise
evidence/source map. Add executable tooling only with the first executable
milestone, not this documentation-only change.

Pin Lean and TLA+ tool versions and dependencies. Define separate fast bounded
checks and deeper configurations; record constants, fairness, state counts,
termination, source/tool revisions, and the exact commands used. A timeout,
simulation, or interrupted search is not an exhaustive pass. Do not use `sorry`,
`admit`, or new unproved axioms to satisfy a Lean acceptance gate; audit theorem
axioms and distinguish standard logical foundations from domain assumptions.

Prefer PlusCal for concurrent algorithms. Keep each modeled atomic operation at
one label and keep non-atomic source sequences split. Use a parallel assignment
when return values must capture pre-state. Preserve hand-authored TLC configurations
under names the PlusCal translator does not overwrite. Bound each client's
operations explicitly, allow clients to reach `Done`, and check eventual completion
under stated scheduler, epoch-participant, and I/O fairness assumptions. Do not
disable deadlock checking merely to hide a stuck bounded model; distinguish expected
terminal states, genuine deadlock, and starvation in retry loops.

For every counterexample, first determine whether it is a source defect, a model
defect, or an unmet assumption. Minimize the trace and map its steps back to source.
Where it demonstrates an implementation bug, add a focused deterministic NUnit
regression using synchronization rather than narrow wall-clock timing. A fix needs
both the model/proof rerun and the affected implementation tests. Model-only
negative controls should stay separate from passing production configurations.

Future continuous integration should fail on parse errors, incomplete checks,
proof holes, and failed properties, and retain failure traces as artifacts.
Only gate the configurations actually maintained; label larger exploratory runs
separately. Changes to mapped C# paths require reviewing the model correspondence,
even when the proof files themselves still compile.

## First pull request acceptance

The roadmap-only PR contains this document and its README entry, with valid source
links and a clean documentation-only diff. It introduces no runtime behavior,
dependencies, CI jobs, models, or proofs. Builds, tests, TLC, and Lean runs are not
evidence for this PR because none of those implementations are added here.
Subsequent PRs implement milestones with their own checked evidence and explicit
remaining gaps.
