---------------------- MODULE GarnetEpochAnnounce ----------------------
(***************************************************************************)
(* Garnet cluster config-transition barrier.                               *)
(*                                                                         *)
(* FencedAnnounce = FALSE reproduces the pre-fix code and is retained as    *)
(* the regression witness; FencedAnnounce = TRUE is the code as it now      *)
(* stands. See formal-verification/tla/README.md for the result matrix.     *)
(*                                                                         *)
(* CODE UNDER TEST                                                         *)
(*                                                                         *)
(*   ClusterSession.cs  (pre-fix -- the announce is a PLAIN store)          *)
(*     void AcquireCurrentEpoch() => _localCurrentEpoch = clusterProvider.GarnetCurrentEpoch; *)
(*     void ReleaseCurrentEpoch() => _localCurrentEpoch = 0;               *)
(*                                                                         *)
(*   ClusterSession.cs  (fixed -- the announce is a locked RMW)             *)
(*     void AcquireCurrentEpoch() =>                                        *)
(*         Interlocked.Exchange(ref _localCurrentEpoch, clusterProvider.GarnetCurrentEpoch); *)
(*     void ReleaseCurrentEpoch() => Volatile.Write(ref _localCurrentEpoch, 0); *)
(*                                                                         *)
(*     async Task UnsafeBumpAndWaitForEpochTransitionAsync() {             *)
(*         ReleaseCurrentEpoch();                                          *)
(*         await clusterProvider.BumpAndWaitForEpochTransitionAsync();     *)
(*         AcquireCurrentEpoch();                                          *)
(*     }                                                                   *)
(*                                                                         *)
(*   ClusterProvider.cs                                                    *)
(*     void BumpCurrentEpoch() => Interlocked.Increment(ref GarnetCurrentEpoch); *)
(*                                                                         *)
(*     async Task<bool> BumpAndWaitForEpochTransitionAsync() {             *)
(*         BumpCurrentEpoch();                                             *)
(*         var currentEpoch = GarnetCurrentEpoch;                          *)
(*         foreach (server) while (true) { retry:                          *)
(*             await Task.Yield();                                         *)
(*             foreach (s in ActiveClusterSessions()) {                    *)
(*                 var entryEpoch = s.LocalCurrentEpoch;                   *)
(*                 if (entryEpoch != 0 && entryEpoch < currentEpoch) goto retry; *)
(*             }                                                           *)
(*             break; }                                                    *)
(*         return true; }                                                  *)
(*                                                                         *)
(*   RespServerSession.TryConsumeMessages                                  *)
(*         clusterSession?.AcquireCurrentEpoch();   // start of batch      *)
(*         ProcessMessages(...);                    // reads CurrentConfig *)
(*     finally clusterSession?.ReleaseCurrentEpoch();                      *)
(*                                                                         *)
(*   ClusterManager.cs                                                     *)
(*     currentConfig is published with Interlocked.CompareExchange and     *)
(*     read with a PLAIN reference load. The config object is immutable,   *)
(*     so a session that has loaded the reference keeps a stale SNAPSHOT   *)
(*     for the rest of its batch.                                          *)
(*                                                                         *)
(* THE CONTRACT                                                            *)
(*   CLUSTER SETSLOT writes the new config, then calls                     *)
(*   UnsafeBumpAndWaitForEpochTransitionAsync, then answers +OK. The +OK   *)
(*   is supposed to mean: no in-flight session will serve another command  *)
(*   using the pre-change config. Slot ownership, MOVED/ASK redirects and  *)
(*   migration safety all rest on that.                                    *)
(*                                                                         *)
(* THE SHAPE OF THE HAZARD                                                 *)
(*   Worker : STORE _localCurrentEpoch ; LOAD currentConfig                *)
(*   Changer: STORE currentConfig      ; LOAD _localCurrentEpoch           *)
(*                                                                         *)
(*   That is the SB (store buffer) litmus test. x86-TSO forbids it only    *)
(*   when BOTH sides fence between their store and their load. The Changer *)
(*   fences (Interlocked.CompareExchange, Interlocked.Increment). The      *)
(*   Worker has NO fence: AcquireCurrentEpoch is a plain store to a plain  *)
(*   long field. So the announce may still be sitting in the Worker's      *)
(*   store buffer when the scan reads the slot as 0 = "inactive".          *)
(*                                                                         *)
(* Expected: VIOLATED under "tso" (hence also under "arm").                *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

(***************************************************************************)
(* FencedAnnounce = FALSE  models main as written: AcquireCurrentEpoch is a *)
(*                         plain store to a plain long field.               *)
(* FencedAnnounce = TRUE   models the candidate fix: announce with a locked *)
(*                         RMW, e.g.                                        *)
(*                             Interlocked.Exchange(ref _localCurrentEpoch, *)
(*                                 clusterProvider.GarnetCurrentEpoch);     *)
(*                         (a plain store followed by                       *)
(*                          Interlocked.MemoryBarrier() is equivalent here; *)
(*                          Volatile.Write is NOT -- a release store gives  *)
(*                          no StoreLoad ordering and does not drain the    *)
(*                          store buffer.)                                  *)
(***************************************************************************)
CONSTANT FencedAnnounce

Worker  == "Worker"
Changer == "Changer"
Threads == { Worker, Changer }

OldConfig == 0
NewConfig == 1

VARIABLES memory, storeBuffer,
          workerPc, workerEpochTmp, workerSnapshot,
          changerPc, triggerEpoch, okSent,
          servedStaleAfterOk

vars == << memory, storeBuffer,
           workerPc, workerEpochTmp, workerSnapshot,
           changerPc, triggerEpoch, okSent,
           servedStaleAfterOk >>

SB      == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)

(***************************************************************************)
(* memory                                                                  *)
(*   currentEpoch  ClusterProvider.GarnetCurrentEpoch, starts at 1          *)
(*   workerLocal   the Worker session's _localCurrentEpoch                  *)
(*   changerLocal  the Changer session's _localCurrentEpoch. It is 0 for    *)
(*                 the whole model because UnsafeBump released it before    *)
(*                 the wait; keeping it as a field documents that the scan  *)
(*                 does look at the caller's own slot.                      *)
(*   config        ClusterManager.currentConfig, as a version number        *)
(***************************************************************************)
Init ==
    /\ memory = [ currentEpoch |-> 1, workerLocal |-> 0,
                  changerLocal |-> 0, config |-> OldConfig ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ workerPc = "AcquireLoad"
    /\ workerEpochTmp = 0
    /\ workerSnapshot = OldConfig
    /\ changerPc = "PublishConfig"
    /\ triggerEpoch = 0
    /\ okSent = FALSE
    /\ servedStaleAfterOk = FALSE

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED << workerPc, workerEpochTmp, workerSnapshot,
                    changerPc, triggerEpoch, okSent, servedStaleAfterOk >>

(***************************************************************************)
(* Worker: an ordinary cluster session serving one network batch.          *)
(***************************************************************************)

\* AcquireCurrentEpoch, first half: plain load of GarnetCurrentEpoch.
AcquireLoad ==
    /\ workerPc = "AcquireLoad"
    /\ workerEpochTmp' = Load(Worker, "currentEpoch")
    /\ workerPc' = "AcquireStore"
    /\ UNCHANGED << memory, storeBuffer, workerSnapshot,
                    changerPc, triggerEpoch, okSent, servedStaleAfterOk >>

\* AcquireCurrentEpoch, second half: the announce. On main this is a PLAIN
\* store to _localCurrentEpoch and carries no barrier of any kind.
AcquireStore ==
    /\ workerPc = "AcquireStore"
    /\ IF FencedAnnounce
       THEN /\ memory' = SB!FencedStore(Worker, "workerLocal", workerEpochTmp)
            /\ storeBuffer' = SB!Drained(Worker)
       ELSE /\ storeBuffer' = SB!Buffer(Worker, "workerLocal", workerEpochTmp)
            /\ UNCHANGED memory
    /\ workerPc' = "ReadConfig"
    /\ UNCHANGED << workerEpochTmp, workerSnapshot,
                    changerPc, triggerEpoch, okSent, servedStaleAfterOk >>

\* ProcessMessages: load clusterManager.CurrentConfig once and keep the
\* immutable snapshot for the rest of the batch. On TSO this load may be
\* satisfied while the announce above is still buffered.
ReadConfig ==
    /\ workerPc = "ReadConfig"
    /\ workerSnapshot' = Load(Worker, "config")
    /\ workerPc' = "Serve"
    /\ UNCHANGED << memory, storeBuffer, workerEpochTmp,
                    changerPc, triggerEpoch, okSent, servedStaleAfterOk >>

\* Serve a command from that snapshot: slot ownership check, MOVED/ASK, etc.
Serve ==
    /\ workerPc = "Serve"
    /\ servedStaleAfterOk' = (servedStaleAfterOk \/ (okSent /\ workerSnapshot = OldConfig))
    /\ workerPc' = "Release"
    /\ UNCHANGED << memory, storeBuffer, workerEpochTmp, workerSnapshot,
                    changerPc, triggerEpoch, okSent >>

\* ReleaseCurrentEpoch in the finally block: plain store of 0.
Release ==
    /\ workerPc = "Release"
    /\ storeBuffer' = SB!Buffer(Worker, "workerLocal", 0)
    /\ workerPc' = "Done"
    /\ UNCHANGED << memory, workerEpochTmp, workerSnapshot,
                    changerPc, triggerEpoch, okSent, servedStaleAfterOk >>

(***************************************************************************)
(* Changer: the session running CLUSTER SETSLOT.                           *)
(***************************************************************************)

\* ClusterManager.TryPrepareSlotForMigration &c: Interlocked.CompareExchange
\* on the currentConfig reference. A locked RMW, so a full barrier.
PublishConfig ==
    /\ changerPc = "PublishConfig"
    /\ memory' = SB!FencedStore(Changer, "config", NewConfig)
    /\ storeBuffer' = SB!Drained(Changer)
    /\ changerPc' = "Bump"
    /\ UNCHANGED << workerPc, workerEpochTmp, workerSnapshot,
                    triggerEpoch, okSent, servedStaleAfterOk >>

\* UnsafeBumpAndWaitForEpochTransitionAsync: ReleaseCurrentEpoch (already
\* modelled by changerLocal = 0) then Interlocked.Increment, then the plain
\* re-read `var currentEpoch = GarnetCurrentEpoch` which, being after the
\* locked RMW, sees the value this thread just wrote.
Bump ==
    /\ changerPc = "Bump"
    /\ LET m == SB!Fenced(Changer)
       IN  /\ memory' = [m EXCEPT !.currentEpoch = m.currentEpoch + 1]
           /\ triggerEpoch' = m.currentEpoch + 1
    /\ storeBuffer' = SB!Drained(Changer)
    /\ changerPc' = "Scan"
    /\ UNCHANGED << workerPc, workerEpochTmp, workerSnapshot,
                    okSent, servedStaleAfterOk >>

\* The wait loop. Plain loads of every active session's LocalCurrentEpoch.
\* A slot reading 0 is treated as "this session is not in a batch".
Scan ==
    /\ changerPc = "Scan"
    /\ LET w == Load(Changer, "workerLocal")
           c == Load(Changer, "changerLocal")
           Behind(e) == e # 0 /\ e < triggerEpoch
       IN  IF Behind(w) \/ Behind(c)
           THEN changerPc' = "Scan"          \* goto retry
           ELSE changerPc' = "SendOk"        \* break; return true
    /\ UNCHANGED << memory, storeBuffer, workerPc, workerEpochTmp,
                    workerSnapshot, triggerEpoch, okSent, servedStaleAfterOk >>

\* +OK goes back to the client. The transition is now claimed complete.
SendOk ==
    /\ changerPc = "SendOk"
    /\ okSent' = TRUE
    /\ changerPc' = "Done"
    /\ UNCHANGED << memory, storeBuffer, workerPc, workerEpochTmp,
                    workerSnapshot, triggerEpoch, servedStaleAfterOk >>

Next ==
    \/ AcquireLoad \/ AcquireStore \/ ReadConfig \/ Serve \/ Release
    \/ PublishConfig \/ Bump \/ Scan \/ SendOk
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* THE CONTRACT, as an invariant.                                          *)
(*                                                                         *)
(* Once CLUSTER SETSLOT has answered +OK, no session may serve a command   *)
(* from a pre-transition config snapshot.                                  *)
(***************************************************************************)
TransitionBarrierHolds == ~ servedStaleAfterOk

(***************************************************************************)
(* The mechanism, stated directly. A session that is still inside its batch *)
(* must not be invisible to a scan that has already declared the transition *)
(* complete. This isolates the missing barrier from what the session then   *)
(* does with the config, and it is the property the fix has to buy.         *)
(***************************************************************************)
AnnounceVisibleWhenTransitionCompletes ==
    (okSent /\ workerPc \in {"ReadConfig", "Serve"}) => memory.workerLocal # 0
========================================================================
