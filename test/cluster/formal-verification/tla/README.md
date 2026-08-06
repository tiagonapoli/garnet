# Formal verification — cluster config-transition barrier

TLA+ models of the barrier that makes `CLUSTER SETSLOT` (and the other config
transitions) safe:

```
ClusterManager.currentConfig = newConfig     // Interlocked.CompareExchange
await BumpAndWaitForEpochTransitionAsync()   // ClusterProvider
reply +OK
```

The contract being checked is: **once `+OK` has gone back to the client, no
in-flight session may serve a command from a pre-transition `ClusterConfig`
snapshot.** Slot ownership, `MOVED`/`ASK` redirects and migration safety all
rest on it.

`StoreBuffer.tla` is the x86-TSO substrate — one private FIFO store buffer per
core, `"tso"` drains it in order (only StoreLoad is relaxed), `"arm"` lets any
pending store drain first (StoreStore relaxed too). It is a copy of the module
used by the Tsavorite `LightEpoch` specs; the two mechanisms are unrelated but
share the announce-then-scan shape.

## Running

Requires Java and `tla2tools.jar`:

```bash
java -cp tla2tools.jar tlc2.TLC -config MainTso.cfg GarnetEpochAnnounce.tla
```

## Results

| Module | Config | Models | Result |
|---|---|---|---|
| `GarnetEpochAnnounce` | `MainTso` | announce is a plain store (pre-fix) | **VIOLATED** |
| `GarnetEpochAnnounce` | `FixedTso` | announce is a locked RMW | holds |
| `GarnetEpochAnnounce` | `FixedArm` | announce is a locked RMW, StoreStore relaxed | holds |
| `GarnetEpochPeerBumper` | `PeerTso` | release/re-acquire window | **VIOLATED** |
| `GarnetEpochPeerBumper` | `PeerSC` | sanity: no store is ever buffered | holds |

## `GarnetEpochAnnounce` — the announce barrier (fixed)

The session and the config changer form a store-buffer (SB) litmus test:

```
session : STORE _localCurrentEpoch ; LOAD  currentConfig
changer : STORE currentConfig      ; LOAD  _localCurrentEpoch
```

x86-TSO forbids both sides reading the stale value only when **both** fence
between their store and their load. The changer already fenced; the session did
not, because `AcquireCurrentEpoch` was a plain store to a plain `long`. TLC's
counterexample under `"tso"` keeps the announce in the session's store buffer
for the whole behaviour, so the scan reads the slot as `0` = idle, reports the
transition complete, and the session then serves from the old config.

The violation disappears under sequential consistency, so this was a pure
memory-ordering defect. `AcquireCurrentEpoch` now announces with
`Interlocked.Exchange`, which is what `FixedTso`/`FixedArm` check.

`Volatile.Write` would **not** be sufficient: a release store gives no
StoreLoad ordering and does not drain the store buffer.

## `GarnetEpochPeerBumper` — the release window (open)

`UnsafeBumpAndWaitForEpochTransitionAsync` releases the caller's epoch so it
does not wait on itself, and `ClusterSlotVerify.CanOperateOnKey` /
`WaitForSlotToStabalize` release around their yield loops. Every access in this
module is a locked RMW — `PeerSC` confirms no store is ever buffered — so the
violation it finds is in the protocol, not in the memory model:

```
victimLocal=1, snapshot=OLD  | changer: config=NEW, epoch 1->2
victimLocal=0   (release)    |
victimLocal=2   (re-acquire) | scan sees 2, not behind 2 -> break -> +OK
                             | victim serves from snapshot=OLD   *** violation
```

Re-acquiring publishes the *latest* epoch, which tells every concurrent scanner
"this session has caught up" — while the immutable `ClusterConfig` reference the
session loaded before the window is untouched. Releasing to `0` is a second
route to the same state, since `0` is read as "not in a batch".

Note the release is load-bearing for liveness: making the scan skip the caller
instead deadlocks two concurrent bumpers, each waiting on the other's stale
slot.

The fix is per-caller — re-read `CurrentConfig` after every re-acquire, as
`WaitForSlotToStabalize` already does and `CanOperateOnKey` does not — and is
tracked separately. This module is checked in as the specification of that bug.
