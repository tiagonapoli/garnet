# Garnet.LightEpoch tests

Unit tests for `LightEpoch`, hardware litmus soak tests, the TLA+ models that
justify its memory ordering, and herd7 checks of the machine code the JIT emits
for it.

## Unit tests

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

## Litmus tests

`Litmus/` holds a hardware stress harness that runs the real race: a reader
announces its epoch and dereferences a page while a reclaimer retires that same
page. `LightEpochLitmusTests` drives it for 30 s, under the `Litmus`
category so a fast suite can skip it:

```
dotnet test ... --filter "TestCategory!=Litmus"     # skip
LE_LITMUS_SECONDS=300 dotnet test ... --filter "TestCategory=Litmus"   # longer soak
```

`QuarantineLitmus` needs no syscall in the race loop: pages come from a pool
allocated once and "freeing" stamps a poison sentinel. A reader
that observes poison in a page it was protecting is a use-after-free by the
algorithm's own definition.

Two things make a green run mean something, and both are asserted:

- **Vacuity guards.** Each test asserts the race window was actually sampled and
  that the epoch really did reclaim. A run that never raced cannot fail.
- **Self-test control.** A paired test forces the failure and
  asserts it *is* detected. If the control ever passes silently, the detector is blind
  and the clean verdict beside it is void.

### Measured power, and its limits

These are soak tests, not deterministic gates. Against a deliberately unfixed epoch
(announce reverted to a plain store, refresh reverted to a plain load) on a
20-logical-processor x86-64 host, successive 30 s runs reported 1641, 11, 8, 5, 3, 1
and 0 violations; with the CAS-carried announce every run reports 0. So a single run
catches the regression most of the time but not always, and the run length is worth
raising via `LE_LITMUS_SECONDS` when that matters.

The configuration is not arbitrary. Two settings were each measured to be the
difference between detecting the bug and detecting nothing at all:

- **`DerefWords`.** The reclaimer only stamps the page from the drain callback,
  several epoch operations after the unlink. A reader walking 64 words has already
  left the page by then, and the run reports nothing however long it lasts.
- **Nothing between the barrier and `Resume()`.** A single extra volatile load in the
  reader loop delays it past the window, after which the announce always drains out
  of the store buffer before the reclaimer scans.

The TLA+ suite below, not these tests, is what actually establishes correctness.

## TLA+ models

`tla/` holds the CAS-carries-the-announce specs. Every spec is checked under two
store-buffer memory models (`tso` = x86-TSO, `arm` = additionally relaxed
StoreStore) and, for the refresh path, under `MODULE WeakMemory`, which gives each
processor its own per-field view and so exposes load-side reordering that a
store-buffer model cannot.

Each configuration that is expected to hold is paired with a control that removes
the fix and must be violated, so a passing run also proves the specs can still
detect the bug they report absent.

Run everything in Docker:

```
docker build -t garnet-lightepoch-tla libs/storage/Tsavorite/cs/test/epoch/tla
docker run --rm garnet-lightepoch-tla
```

The container exits non-zero if any spec result differs from its expectation.

To run outside Docker you need Java and `tla2tools.jar`:

```
TLA_TOOLS=/path/to/tla2tools.jar bash libs/storage/Tsavorite/cs/test/epoch/tla/run.sh
```

Both forms take an optional substring that selects which rows to run:

```
docker run --rm garnet-lightepoch-tla CasAnnounceResumeRefreshWeak_acqload_armlb
```

TLC accepts exactly one `-config` per run, so `run.sh` holds the matrix — spec,
constants, invariants and expected result — as a table and expands each row into
a throwaway `.cfg` as it goes. Set `LE_KEEP_CFG=1` to leave those files on disk,
which is what you want to open one in the TLA+ Toolbox.

## What the models establish

| Property | Rows |
| --- | --- |
| Claiming the slot with `CAS(localCurrentEpoch, 0 -> epoch)` closes the store-buffering window against `ComputeNewSafeToReclaimEpoch` | `CasAnnounceOneReader`, `CasAnnounceTwoReaders`, `CasAnnounceSymmetricPeers` |
| A plain store in `Release()` is not enough once StoreStore is relaxed — the unpublish can wipe the next owner's announce | `CasAnnounceTwoReaders_plainrelease_*` |
| `Entry.threadId` no longer participates in slot ownership once the CAS carries the announce | `CasAnnounceNoThreadId`, `CasAnnounceTwoReaders_nothreadid_*` |
| The refresh announce in `ProtectAndDrain` is a load-side message-passing hazard; an acquire load of `CurrentEpoch` is necessary and sufficient, and a release store is not enough | `CasAnnounceResumeRefreshWeak` (the `armlb` rows) |
| The acquire announce and the refresh announce fail in different shapes, so the CAS cannot be weakened to a release store | `CasAnnounceResumeRefreshWeak_acqplain_armlb`, `_acqrelease_armlb` |

Each spec is parameterised by constants, so one module covers both the fixed
algorithm and its controls: `AcquireOrder` selects how the claim is published,
`ReleaseOrder` how the slot is unpublished, and `UseThreadId` / `StaleIndex`
switch the two ownership controls. `run.sh` lists every row with its expected
verdict.

## herd7 checks of the emitted code

`herd/` takes the actual RyuJIT output for `LightEpoch` on x86-64 and AArch64,
reduces it to the instructions that carry the ordering, and checks those against
x86-TSO and `aarch64.cat`:

```
docker build -t garnet-lightepoch-herd libs/storage/Tsavorite/cs/test/epoch/herd
docker run --rm garnet-lightepoch-herd
```

This is what covers AArch64, which neither the TLA+ specs (they model memory
ordering by hand and say nothing about codegen) nor the litmus soak (it runs on
whatever machine you have) can speak to. It found two ordering holes that cannot
occur on x86 at all: on `main` the refresh path's read of `CurrentEpoch` is
merged into a plain `LDP`, leaving the following data load free to be satisfied
first; and the plain slot clear in `Release()` can be observed before the
reader's own dereference has been satisfied. It also runs the whole
`Acquire` → `ProtectAndDrain` → critical section → `Release` sequence against a
full reclaimer, not just the individual hazard shapes. See
`herd/memory-ordering-bugs-found.md`.
