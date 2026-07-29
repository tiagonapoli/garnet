# Garnet.LightEpoch tests

Unit tests for `LightEpoch` plus the TLA+ models that justify its memory ordering.

## Unit tests

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

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

## What the models establish

| Property | Specs |
| --- | --- |
| Claiming the slot with `CAS(localCurrentEpoch, 0 -> epoch)` closes the store-buffering window against `ComputeNewSafeToReclaimEpoch` | `CasAnnounceOneReader`, `CasAnnounceTwoReaders`, `CasAnnounceSymmetricPeers` |
| A plain store in `Release()` is not enough once StoreStore is relaxed — the unpublish can wipe the next owner's announce | `CasAnnounceTwoReadersPlainRelease` |
| `Entry.threadId` no longer participates in slot ownership once the CAS carries the announce | `CasAnnounceNoThreadId*` |
| The refresh announce in `ProtectAndDrain` is a load-side message-passing hazard; an acquire load of `CurrentEpoch` is necessary and sufficient, and a release store is not enough | `CasAnnounceResumeRefreshWeak` (`*_armlb.cfg`) |
| The acquire announce and the refresh announce fail in different shapes, so the CAS cannot be weakened to a release store | `CasAnnounceResumeRefreshWeak_acqplain`, `_acqrelease` |
