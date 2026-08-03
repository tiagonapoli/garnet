# LightEpoch Store-Buffer litmus harness

A hardware stress harness that runs the real `LightEpoch` use-after-free race: a
reader announces its epoch and dereferences a page while a reclaimer retires that
same page. Ships as a standalone executable, `LightEpochLitmus`.

> *Litmus test* is the memory-model term (Alglave & Maranget's `herd7`, the ARM and
> Intel architecture manuals) for a minimal program built to expose one specific
> reordering — everything the outcome does not depend on is stripped away, so a result
> is attributable to one hardware behaviour and nothing else. This is the runtime
> counterpart of the `herd7` tests in the companion repo: same shape, run on silicon
> instead of a model.

## What it asserts

No syscall in the race loop: pages come from a pool allocated once, and "freeing"
stamps a poison sentinel. A reader that observes poison in a page it was protecting
is a use-after-free by the algorithm's own definition.

Two things make a green run mean something, and both are asserted:

- **Vacuity guards.** Each run asserts the race window was actually sampled and the
  epoch really did reclaim. A run that never raced cannot fail.
- **Self-test control.** A forced failure that must be detected. If the control
  passes silently the detector is blind, and the clean verdict beside it is void.

## The pieces

- **`QuarantineLitmus`** — the race: reader/reclaimer loop, page pool, poison stamp,
  violation counter. Generic over the epoch so the JIT specialises it and the reader
  keeps direct calls; an indirection inside the window could hide the reordering.
- **`TwoThreadBarrier`** — the two-thread barrier that lines the reader and reclaimer
  up each pass. Without it they drift apart and the window is never sampled.
- **`EpochUnderTest`** — the `IEpochUnderTest` seam and the `FixedEpoch`/`BuggyEpoch`
  adapters, which let one binary run either algorithm.
- **`Platform`** — page allocation and thread-to-core pinning.

## Comparing against the unfixed algorithm

`BuggyLightEpoch.cs` is a frozen copy of `LightEpoch` as it stood before this PR.
`--buggy` runs the harness against it, so both algorithms can be compared on the
same machine in the same session:

```
dotnet run -c Release --project playground/LightEpochLitmus -- --buggy --seconds 30
dotnet run -c Release --project playground/LightEpochLitmus -- --seconds 30
```

The first is expected to exit `1` with a non-zero violation count, the second `0`. A
`--buggy` run that comes back clean means the machine is not producing the window at
all, and the clean result beside it proves nothing.

## Running it

```
dotnet run --project playground/LightEpochLitmus -- --seconds 600 --json result.json
```

It runs the forced-failure control first and refuses to continue unless the detector
reports it, then runs the stress loop. `--help` lists every option. Exit codes: `0` pass, `1`
violation, `2` inconclusive (blind detector, nothing sampled, nothing reclaimed, or
emulation), `3` unsupported host, `64` bad arguments. The distinction between `0` and
`2` is the point — an inconclusive run is not a pass.

In Docker, with the repository root as the build context:

```
docker build -f playground/LightEpochLitmus/Dockerfile -t garnet-lightepoch-litmus .
docker run --rm garnet-lightepoch-litmus --seconds 3600 --iterations 8 --json -
```

The container needs at least 4 logical processors and pins threads with
`sched_setaffinity`, so do not restrict it below that with `--cpuset-cpus`. The core
layout is fixed, so two instances on one machine pin to the same processors and
contend — do not run copies side by side.

## Do not run this under emulation

Building with `--platform linux/arm64` on an x86 host runs under QEMU, and an
emulator does not reproduce the emulated architecture's memory ordering: the
reorderings this exists to catch cannot occur, so the run comes back clean whatever
the code does. Verified, not assumed — before the guard existed, an `arm64` image on
an x86-64 host reported `PASS` and exit `0`.

The self-test control does **not** protect you here: it recycles pages
unconditionally rather than relying on a reordering, so it fires under emulation just
as on real hardware. In that same run it reported 1,156 violations while the stress run
reported none.

So the tool detects emulation directly and downgrades a pass to inconclusive.
Detection is heuristic and one-sided — a positive is reliable, a negative is not. The
signal that catches the Docker case is the MIDR: every real AArch64 implementer is
registered and non-zero (`0x41` ARM, `0x50` Ampere, `0x51` Qualcomm, `0x61` Apple),
while QEMU's TCG synthesises `0x00`. `--allow-emulation` overrides it, and is only
appropriate for exercising the harness itself.

**For an architecture other than the host's, build and run on a native machine of
that architecture.** Nothing else produces evidence.

## Measured power, and its limits

These are stress tests, not deterministic gates. Against a deliberately unfixed epoch
on a 20-logical-processor x86-64 host, successive 30 s runs reported 44, 33, 52, 111
and 36 violations; with the CAS-carried announce every run reports 0. So a single
30 s run reliably catches this regression here, but the counts are small enough that
it remains a probabilistic signal — raise `--seconds` when that matters.

The configuration is not arbitrary. Three settings were each measured to be the
difference between detecting the bug and detecting nothing:

- **`DerefWords`.** The reclaimer only stamps the page from the drain callback,
  several epoch operations after the unlink. A reader walking 64 words has already
  left the page by then, and the run reports nothing however long it lasts.
- **Nothing between the barrier and `Resume()`.** One extra volatile load in the
  reader loop delays it past the window, after which the announce always drains out
  of the store buffer before the reclaimer scans.
- **Counters on separate cache lines.** Sharing a line, the same runs reported 10,
  16, 22 and 22 — about a third of the detection power. The contended line delays the
  reader into exactly the "arriving late" regime above.
