# LightEpoch Store-Buffer litmus harness

A hardware stress harness that runs the real `LightEpoch` use-after-free race: a
reader announces its epoch and dereferences a page while a reclaimer retires that
same page. It ships as a standalone executable, `LightEpochLitmus`.

It lives in `playground/` rather than under `test/` because it is a tool, not part
of the test gate: it pins threads to specific logical processors and saturates them
for minutes at a time, which no shared CI machine should be asked to absorb.

## What it asserts

`QuarantineLitmus` needs no syscall in the race loop: pages come from a pool
allocated once and "freeing" stamps a poison sentinel. A reader that observes poison
in a page it was protecting is a use-after-free by the algorithm's own definition.

Two things make a green run mean something, and both are asserted:

- **Vacuity guards.** Each run asserts the race window was actually sampled and that
  the epoch really did reclaim. A run that never raced cannot fail.
- **Self-test control.** A forced failure that must be detected. If the control ever
  passes silently, the detector is blind and the clean verdict beside it is void.

## Running it

```
dotnet run --project playground/LightEpochLitmus -- --seconds 600 --json result.json
```

It runs the forced-failure control first and refuses to continue unless the
detector reports it, then soaks. `--help` lists every option. The exit code is the
result: `0` pass, `1` violation, `2` inconclusive (blind detector, nothing sampled,
nothing reclaimed, or emulation), `3` unsupported host, `64` bad arguments. The
distinction between `0` and `2` is the point — an inconclusive run is not a pass.

In Docker, with the repository root as the build context:

```
docker build -f playground/LightEpochLitmus/Dockerfile -t garnet-lightepoch-litmus .
docker run --rm garnet-lightepoch-litmus --seconds 3600 --iterations 8 --json -
```

The container needs at least 4 logical processors and pins threads with
`sched_setaffinity`, so do not restrict it below that with `--cpuset-cpus`.

Because the core layout is fixed, two instances on one machine pin to the same
processors and contend, which distorts the microsecond window the result depends
on — so do not run two copies side by side.

## Do not run this under emulation

Building the image with `--platform linux/arm64` on an x86 host runs it under QEMU,
and an emulator does not reproduce the emulated architecture's memory ordering: the
reorderings this harness exists to catch cannot occur, so the run comes back clean
whatever the code does. This was verified, not assumed — before the guard below
existed, an `arm64` image on an x86-64 host reported `PASS` and exit `0`.

The self-test control does **not** protect you here. It recycles pages
unconditionally rather than relying on a reordering, so it fires under emulation
exactly as it does on real hardware; in that same run it reported 1,156 violations
while the soak reported none.

So the tool detects emulation directly and downgrades a pass to inconclusive.
Detection is heuristic and one-sided — a positive is reliable, a negative is not a
guarantee. The signal that catches the Docker case is the MIDR: every real AArch64
implementer is registered and non-zero (`0x41` ARM, `0x50` Ampere, `0x51` Qualcomm,
`0x61` Apple), while QEMU's TCG synthesises `0x00`. `--allow-emulation` overrides
it, and is only ever appropriate for exercising the harness itself.

**For an architecture other than the host's, build and run on a native machine of
that architecture.** Nothing else produces evidence.

## Measured power, and its limits

These are soak tests, not deterministic gates. Against a deliberately unfixed epoch
(announce reverted to a plain store behind a `threadId` CAS) on a 20-logical-processor
x86-64 host, successive 30 s runs reported 44, 33, 52, 111 and 36 violations; with the
CAS-carried announce every run reports 0. So a single 30 s run reliably catches this
regression here, but the counts are small enough that it is still a probabilistic
signal — `--seconds` is worth raising when that matters.

The configuration is not arbitrary. Three settings were each measured to be the
difference between detecting the bug and detecting nothing at all, or close to it:

- **`DerefWords`.** The reclaimer only stamps the page from the drain callback,
  several epoch operations after the unlink. A reader walking 64 words has already
  left the page by then, and the run reports nothing however long it lasts.
- **Nothing between the barrier and `Resume()`.** A single extra volatile load in the
  reader loop delays it past the window, after which the announce always drains out
  of the store buffer before the reclaimer scans.
- **Counters on separate cache lines.** With the reader's and reclaimer's counters
  sharing a line, the same runs reported 10, 16, 22 and 22 — roughly a third of the
  detection power. The contended line delays the reader into exactly the "arriving
  late" regime described above.

