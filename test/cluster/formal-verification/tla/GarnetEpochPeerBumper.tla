-------------------- MODULE GarnetEpochPeerBumper --------------------
(***************************************************************************)
(* The SECOND defect, which is in the ALGORITHM and not in the memory       *)
(* model: the release window opened by                                      *)
(*                                                                         *)
(*     UnsafeBumpAndWaitForEpochTransitionAsync()                          *)
(*     {                                                                   *)
(*         ReleaseCurrentEpoch();                    // slot -> 0          *)
(*         await BumpAndWaitForEpochTransitionAsync();                     *)
(*         AcquireCurrentEpoch();                    // slot -> epoch      *)
(*     }                                                                   *)
(*                                                                         *)
(* The release is there so the caller does not wait on itself. But the      *)
(* scan in BumpAndWaitForEpochTransitionAsync treats slot = 0 as "this      *)
(* session is not inside a batch", and that is now false: the caller IS     *)
(* inside a batch, and it may already be holding a ClusterConfig reference  *)
(* it loaded before the release. A CONCURRENT config changer scanning at    *)
(* that moment reads 0, concludes the transition is complete, and answers   *)
(* +OK -- while the released session is still going to act on its           *)
(* pre-transition snapshot.                                                *)
(*                                                                         *)
(* Two production shapes open exactly this window mid-batch:                *)
(*                                                                         *)
(*   ClusterSession.UnsafeBumpAndWaitForEpochTransitionAsync   (above)      *)
(*                                                                         *)
(*   ClusterSlotVerify.CanOperateOnKey                                      *)
(*       while (!migrationManager.CanAccessKey(key, slot, readOnly)) {      *)
(*           ReleaseCurrentEpoch();                                         *)
(*           Thread.Yield();                                                *)
(*           AcquireCurrentEpoch();                                         *)
(*       }                                                                  *)
(*       return Exists(key);   // uses `slot` decided from the OLD config   *)
(*                                                                         *)
(* This module models the second one, because it is the clearer victim:     *)
(* a session that read the config, parked itself in a release/re-acquire    *)
(* loop, and then went on to serve from the snapshot it took before.        *)
(*                                                                         *)
(* CRUCIALLY, every access here is FENCED (locked RMW). The store buffer is *)
(* drained at every step, so the model is sequentially consistent. Any      *)
(* violation TLC finds is therefore NOT attributable to x86-TSO -- it is    *)
(* the protocol.                                                           *)
(*                                                                         *)
(* Expected: VIOLATED under "tso" (and under any memory model).             *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

Victim  == "Victim"
Changer == "Changer"
Threads == { Victim, Changer }

OldConfig == 0
NewConfig == 1

VARIABLES memory, storeBuffer,
          victimPc, victimSnapshot,
          changerPc, triggerEpoch, okSent,
          servedStaleAfterOk

vars == << memory, storeBuffer,
           victimPc, victimSnapshot,
           changerPc, triggerEpoch, okSent,
           servedStaleAfterOk >>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)

\* Every store in this module is a locked RMW: publish and drain in one step.
Fence(p, f, v) == SB!FencedStore(p, f, v)

Init ==
    /\ memory = [ currentEpoch |-> 1, victimLocal |-> 0,
                  changerLocal |-> 0, config |-> OldConfig ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ victimPc = "Acquire"
    /\ victimSnapshot = OldConfig
    /\ changerPc = "PublishConfig"
    /\ triggerEpoch = 0
    /\ okSent = FALSE
    /\ servedStaleAfterOk = FALSE

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED << victimPc, victimSnapshot, changerPc,
                    triggerEpoch, okSent, servedStaleAfterOk >>

(***************************************************************************)
(* Victim: an ordinary session that hits the release/re-acquire window.     *)
(***************************************************************************)

\* AcquireCurrentEpoch at the top of TryConsumeMessages, fenced.
Acquire ==
    /\ victimPc = "Acquire"
    /\ memory' = Fence(Victim, "victimLocal", Load(Victim, "currentEpoch"))
    /\ storeBuffer' = SB!Drained(Victim)
    /\ victimPc' = "ReadConfig"
    /\ UNCHANGED << victimSnapshot, changerPc, triggerEpoch,
                    okSent, servedStaleAfterOk >>

\* Load clusterManager.CurrentConfig. ClusterConfig is immutable, so this
\* reference is the snapshot the rest of the batch reasons from.
ReadConfig ==
    /\ victimPc = "ReadConfig"
    /\ victimSnapshot' = Load(Victim, "config")
    /\ victimPc' = "ReleaseInWindow"
    /\ UNCHANGED << memory, storeBuffer, changerPc, triggerEpoch,
                    okSent, servedStaleAfterOk >>

\* ReleaseCurrentEpoch inside CanOperateOnKey / UnsafeBumpAndWait...
\* The session is still mid-batch and still holds victimSnapshot, but its
\* slot now reads 0, which every scanner interprets as "not in a batch".
ReleaseInWindow ==
    /\ victimPc = "ReleaseInWindow"
    /\ memory' = Fence(Victim, "victimLocal", 0)
    /\ storeBuffer' = SB!Drained(Victim)
    /\ victimPc' = "Reacquire"
    /\ UNCHANGED << victimSnapshot, changerPc, triggerEpoch,
                    okSent, servedStaleAfterOk >>

\* AcquireCurrentEpoch on the way out of the window.
Reacquire ==
    /\ victimPc = "Reacquire"
    /\ memory' = Fence(Victim, "victimLocal", Load(Victim, "currentEpoch"))
    /\ storeBuffer' = SB!Drained(Victim)
    /\ victimPc' = "Serve"
    /\ UNCHANGED << victimSnapshot, changerPc, triggerEpoch,
                    okSent, servedStaleAfterOk >>

\* Serve from the snapshot taken before the window -- `return Exists(key)`
\* against the slot decided from the pre-transition config.
Serve ==
    /\ victimPc = "Serve"
    /\ servedStaleAfterOk' = (servedStaleAfterOk \/ (okSent /\ victimSnapshot = OldConfig))
    /\ victimPc' = "Release"
    /\ UNCHANGED << memory, storeBuffer, victimSnapshot,
                    changerPc, triggerEpoch, okSent >>

Release ==
    /\ victimPc = "Release"
    /\ memory' = Fence(Victim, "victimLocal", 0)
    /\ storeBuffer' = SB!Drained(Victim)
    /\ victimPc' = "Done"
    /\ UNCHANGED << victimSnapshot, changerPc, triggerEpoch,
                    okSent, servedStaleAfterOk >>

(***************************************************************************)
(* Changer: CLUSTER SETSLOT, unchanged from production.                    *)
(***************************************************************************)

PublishConfig ==
    /\ changerPc = "PublishConfig"
    /\ memory' = Fence(Changer, "config", NewConfig)
    /\ storeBuffer' = SB!Drained(Changer)
    /\ changerPc' = "Bump"
    /\ UNCHANGED << victimPc, victimSnapshot, triggerEpoch,
                    okSent, servedStaleAfterOk >>

Bump ==
    /\ changerPc = "Bump"
    /\ LET m == SB!Fenced(Changer)
       IN  /\ memory' = [m EXCEPT !.currentEpoch = m.currentEpoch + 1]
           /\ triggerEpoch' = m.currentEpoch + 1
    /\ storeBuffer' = SB!Drained(Changer)
    /\ changerPc' = "Scan"
    /\ UNCHANGED << victimPc, victimSnapshot, okSent, servedStaleAfterOk >>

Scan ==
    /\ changerPc = "Scan"
    /\ LET v == Load(Changer, "victimLocal")
           c == Load(Changer, "changerLocal")
           Behind(e) == e # 0 /\ e < triggerEpoch
       IN  IF Behind(v) \/ Behind(c)
           THEN changerPc' = "Scan"
           ELSE changerPc' = "SendOk"
    /\ UNCHANGED << memory, storeBuffer, victimPc, victimSnapshot,
                    triggerEpoch, okSent, servedStaleAfterOk >>

SendOk ==
    /\ changerPc = "SendOk"
    /\ okSent' = TRUE
    /\ changerPc' = "Done"
    /\ UNCHANGED << memory, storeBuffer, victimPc, victimSnapshot,
                    triggerEpoch, servedStaleAfterOk >>

Next ==
    \/ Acquire \/ ReadConfig \/ ReleaseInWindow \/ Reacquire \/ Serve \/ Release
    \/ PublishConfig \/ Bump \/ Scan \/ SendOk
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

TransitionBarrierHolds == ~ servedStaleAfterOk

(***************************************************************************)
(* Sanity: the model really is sequentially consistent, so nothing here can *)
(* be blamed on the store buffer.                                          *)
(***************************************************************************)
NoBufferedStores == \A p \in Threads : storeBuffer[p] = <<>>
======================================================================
