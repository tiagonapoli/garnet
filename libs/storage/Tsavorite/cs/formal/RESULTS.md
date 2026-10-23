# Checked evidence

Checked on October 22, 2026 local time (TLC logs use October 23 UTC).
The source mapping baseline is upstream
`e154dcdbae18f62272ce09e400c9fb3b9bb48aec`.
See [claim boundaries](README.md#claims-and-source-correspondence) before
interpreting these results.

## Lean

`lake build --wfail` and `lake env lean Tsavorite.lean` passed using Lean 4.19.0
(compiler commit `6caaee842e94`), including the strengthened inductive epoch proof.
There are no external Lean package dependencies or admitted proofs. The four
audited root theorems reported only the standard axioms `[propext, Quot.sound]`:

- `Tsavorite.Core.reachable_refines`
- `Tsavorite.Epoch.reclamation_safe`
- `Tsavorite.Epoch.reachable_safe`
- `Tsavorite.Checkpoint.reachable_safe`

Core and epoch reachability proofs quantify over arbitrary client/key/resource
types and finite reachable traces. Checkpoint durability quantifies over arbitrary
snapshot types and finite reachable traces. None is a theorem about the C# runtime
or an equivalence proof between the Lean and TLA+ definitions.

## Exhaustive model checking

All three PlusCal modules translated and passed SANY semantic checks. The pinned
TLA+ 1.7.4 jar reported TLC 2.19 (revision `5a47802`); its SHA-256 was checked by
the runner. Every positive check below exhausted its state queue, reported no
errors, and finished checking the temporal termination property.

| Configuration | Finite bound | Generated states | Distinct states | Result |
| --- | --- | ---: | ---: | --- |
| CoreSingle | 1 client, 2 keys, 3 values, 2 operations | 1,411 | 1,113 | Safety and fair termination passed |
| CoreSmall | 2 clients, 2 keys, 3 values, 1 operation each | 19,701 | 9,573 | Safety and fair termination passed |
| CoreTwoOps | 2 clients, 2 keys, 3 values, 2 operations each | 526,173 | 235,793 | Safety and fair termination passed |
| EpochSmall | 2 readers, 3 generations, 2 operations each | 41,146 | 11,952 | Access discipline, safety, and fair termination passed |
| CheckpointSmall | 2 versions, 2 captured values, 2 crashes | 1,298 | 792 | Durable commit/recovery and fair termination passed |

The core keys share one abstract collision-bucket lock. Absence is distinct from
the three values. The epoch bound includes two retirements. Fair termination is
conditional on the stated weak process fairness and finite budgets; it is not an
unbounded implementation-liveness proof.

## Negative controls

Each deliberate guard mutation failed with the expected invariant and an actual
counterexample trace, not a parser/tool failure:

| Configuration | Mutation | Detected invariant |
| --- | --- | --- |
| CoreBroken | Bypass exclusion so another prepared mutation can become stale | `DataRefinesMap` |
| EpochBroken | Reclaim without waiting for old announcements | `NoProtectedReclamation` |
| CheckpointBroken | Permit commit without durable flush | `CommittedIsDurable` |

These are checks that the specifications detect the intended failure class, not
reports that the unmodified C# engine contains those defects.

## Reproduction

From this directory, with PowerShell 7, Docker, and the pinned Lean toolchain:

```powershell
.\Test-Formal.ps1 -LeanBin '<Lean installation>\bin' -IncludeDeep
```

Both the standalone Docker runner and the workstation-helper path were exercised.
The complete default suite passed through the standalone runner with the
split-action epoch model. The deeper core check passed through the helper path;
the same checked-in core model/configuration is used by both paths. The final Lean
build separately checked the strengthened core and inductive epoch proofs.
The optional deeper liveness check is substantially more expensive than the
default suite. Logs include tool versions, axiom output, state counts, and
negative-control traces in the runner's printed output directory.
