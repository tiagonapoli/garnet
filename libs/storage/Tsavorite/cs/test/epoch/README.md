# Garnet.LightEpoch tests

Unit tests for `LightEpoch`, the TLA+ models that justify its memory ordering, and
herd7 checks of the machine code the JIT emits for it.

## Unit tests

Protection state transitions, the reclamation frontier, drain-list semantics,
per-instance isolation, entry-table slot handout, and the user-word API.

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

`LitmusTests` also lives here, but it only wraps the standalone hardware stress
harness in [`playground/LightEpochLitmus`](../../../../../../playground/LightEpochLitmus),
which is where that story is documented. It is `[Explicit]`, so it is excluded from
the command above and has to be asked for:

```
dotnet test ... --filter "TestCategory=Litmus"
```

## TLA+ models

`formal-verification/tla/` holds the CAS-carries-the-announce specs. Every spec is checked under two
store-buffer memory models (`tso` = x86-TSO, `arm` = additionally relaxed
StoreStore) and, for the refresh path, under `MODULE WeakMemory`, which gives each
processor its own per-field view and so exposes load-side reordering that a
store-buffer model cannot.

Each configuration that is expected to hold is paired with a control that removes
the fix and must be violated, so a passing run also proves the specs can still
detect the bug they report absent.

`CasAnnounceReleaseLoadStore.tla` is the exception to the "two store-buffer
models" description above, and worth reading for what it says about the limits
of the rest. Both `StoreBuffer` and `WeakMemory` bind a load's value at its
program point, so neither can express a load that is still in flight when a
later store becomes visible — and in the other specs the critical section is not
a memory access at all, so there would be nothing to reorder even if they could.
That blind spot hid a real ARM-only use-after-free (`Release()`'s slot clear
passing the reader's own dereference) until the herd7 suite found it. This spec
closes it by splitting the dereference into an issue step and a bind step.

Run everything in Docker:

```
docker build -t garnet-lightepoch-tla libs/storage/Tsavorite/cs/test/epoch/formal-verification/tla
docker run --rm garnet-lightepoch-tla
```

The container exits non-zero if any spec result differs from its expectation.

To run outside Docker you need Java and `tla2tools.jar`:

```
TLA_TOOLS=/path/to/tla2tools.jar bash libs/storage/Tsavorite/cs/test/epoch/formal-verification/tla/run.sh
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

`formal-verification/herd7/` takes the actual RyuJIT output for `LightEpoch` on x86-64 and AArch64,
reduces it to the instructions that carry the ordering, and checks those against
x86-TSO and `aarch64.cat`:

```
docker build -t garnet-lightepoch-herd libs/storage/Tsavorite/cs/test/epoch/formal-verification/herd7
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
`formal-verification/herd7/memory-ordering-bugs-found.md`.
