# Garnet.LightEpoch tests

Unit tests for `LightEpoch` plus a hardware litmus soak harness that runs the
real use-after-free race, which also ships as a standalone tool.

## Unit tests

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

## Litmus tests

`litmus/` holds a hardware stress harness that runs the real race: a reader
announces its epoch and dereferences a page while a reclaimer retires that same
page. `LitmusTests` drives it for 30 s, under the `Litmus`
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

### Running it as a tool

`litmus/` is its own executable, `Garnet.LightEpoch.litmus`, and the NUnit tests
above are a thin wrapper over it. Use the executable when you want a soak longer
than a test run should take, a machine-readable result, or a run on a machine that
has no SDK on it:

```
dotnet run --project libs/storage/Tsavorite/cs/test/epoch/litmus -f net8.0 -- --seconds 600 --json result.json
```

It runs the forced-failure control first and refuses to continue unless the
detector reports it, then soaks. `--help` lists every option. The exit code is the
result: `0` pass, `1` violation, `2` inconclusive (blind detector, nothing sampled,
nothing reclaimed, or emulation), `3` unsupported host, `64` bad arguments. The
distinction between `0` and `2` is the point — an inconclusive run is not a pass.

In Docker, with the repository root as the build context:

```
docker build -f libs/storage/Tsavorite/cs/test/epoch/litmus/Dockerfile -t garnet-lightepoch-litmus .
docker run --rm garnet-lightepoch-litmus --seconds 3600 --iterations 8 --json -
```

The container needs at least 4 logical processors and pins threads with
`sched_setaffinity`, so do not restrict it below that with `--cpuset-cpus`.

### Do not run this under emulation

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

