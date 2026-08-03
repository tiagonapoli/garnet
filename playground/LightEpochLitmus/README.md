# LightEpoch store-buffer litmus harness

Runs the real `LightEpoch` use-after-free race on hardware: a reader announces its
epoch and dereferences a page while a reclaimer retires that same page. Standalone
executable, `LightEpochLitmus`.

> A *litmus test* is a minimal program built to expose one specific reordering.
> Everything the outcome does not depend on is stripped away, so a result is
> attributable to one hardware behaviour and nothing else.

## What it asserts

No syscall in the race loop: pages come from a pool allocated once, and "freeing"
stamps a poison sentinel. A reader that sees poison in a page it was protecting is a
use-after-free.

A green run only means something because of two checks:

- **Vacuity guards.** The run must have sampled the race window and actually
  reclaimed. A run that never raced cannot fail.
- **Self-test control.** A forced failure that must be detected. A control that
  passes silently means the detector is blind, which voids the verdict beside it.

## The pieces

- **`QuarantineLitmus`** — the race: reader/reclaimer loop, page pool, poison stamp,
  violation counter. Generic over the epoch so the JIT specialises it; an indirection
  inside the window could hide the reordering.
- **`TwoThreadBarrier`** — lines the reader and reclaimer up each pass. Without it
  they drift apart and the window is never sampled.
- **`EpochUnderTest`** — the `IEpochUnderTest` seam plus `FixedEpoch`/`BuggyEpoch`, so
  one binary runs either algorithm.
- **`Platform`** — page allocation and thread-to-core pinning.

## Running it

```
dotnet run --project playground/LightEpochLitmus -- --seconds 600 --json result.json
```

The control runs first and the harness refuses to continue unless the detector
reports it. `--help` lists every option. Exit codes: `0` pass, `1` violation, `2`
inconclusive, `3` unsupported host, `64` bad arguments. `0` versus `2` is the point —
an inconclusive run is not a pass.

In Docker, with the repository root as the build context:

```
docker build -f playground/LightEpochLitmus/Dockerfile -t garnet-lightepoch-litmus .
docker run --rm garnet-lightepoch-litmus --seconds 3600 --iterations 8 --json -
```

It needs at least 4 logical processors and pins with `sched_setaffinity`, so do not
narrow `--cpuset-cpus` below that. The core layout is fixed, so two instances pin to
the same processors and contend — do not run copies side by side.

## Comparing against the unfixed algorithm

`BuggyLightEpoch.cs` is a frozen copy of `LightEpoch` as it stood before this PR.
`--buggy` runs against it, so both algorithms can be compared on the same machine in
the same session:

```
dotnet run -c Release --project playground/LightEpochLitmus -- --buggy --seconds 30
dotnet run -c Release --project playground/LightEpochLitmus -- --seconds 30
```

The first is expected to exit `1` with violations, the second `0`. A `--buggy` run
that comes back clean means the machine is not producing the window at all, and the
clean result beside it proves nothing.

## Do not run this under emulation

QEMU — `--platform linux/arm64` on an x86 host, say — does not reproduce the emulated
architecture's memory ordering, so the run comes back clean whatever the code does.
Verified, not assumed: before the guard existed, an `arm64` image on an x86-64 host
reported `PASS` and exit `0`.

The control does not protect you here. It recycles pages unconditionally rather than
relying on a reordering, so it fires under emulation just as on real hardware — in
that same run it reported 1,156 violations while the stress run reported none.

So the tool detects emulation and downgrades a pass to inconclusive. Detection is
one-sided: a positive is reliable, a negative is not. The signal is the MIDR — every
real AArch64 implementer is registered and non-zero (`0x41` ARM, `0x50` Ampere, `0x51`
Qualcomm, `0x61` Apple), while QEMU's TCG synthesises `0x00`. `--allow-emulation`
overrides it, and is only appropriate for exercising the harness itself. For an
architecture other than the host's, build and run on a native machine.

## Measured power, and its limits

These are stress tests, not deterministic gates. Against a deliberately unfixed epoch
on a 20-logical-processor x86-64 host, successive 30 s runs reported 44, 33, 52, 111
and 36 violations; with the CAS-carried announce every run reports 0. So a single 30 s
run reliably catches this regression here, but the counts are small enough that it
remains a probabilistic signal — raise `--seconds` when that matters.

Three settings were each measured to be the difference between detecting the bug and
detecting nothing:

- **`DerefWords`.** The reclaimer only stamps the page from the drain callback,
  several epoch operations after the unlink. A reader walking 64 words has already
  left the page by then.
- **Nothing between the barrier and `Resume()`.** One extra volatile load in the
  reader loop delays it past the window.
- **Counters on separate cache lines.** Sharing a line, the same runs reported 10,
  16, 22 and 22 — about a third of the detection power.
