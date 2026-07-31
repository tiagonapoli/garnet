# Garnet.LightEpoch tests

Unit tests for `LightEpoch` plus a hardware litmus soak harness that runs the
real use-after-free race.

## Unit tests

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

## Litmus tests

`Litmus/` holds a hardware stress harness that runs the real race: a reader
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

