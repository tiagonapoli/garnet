--------------------- MODULE CasAnnounceSymmetricPeersNoCas ---------------------
(***************************************************************************)
(* CAS-carries-epoch fix under the configuration Tsavorite actually runs:   *)
(* TWO SYMMETRIC PEERS, each of which both PROTECTS and RECLAIMS.           *)
(*                                                                         *)
(* The other specs in this directory split the roles: a dedicated reader    *)
(* and a dedicated reclaimer. Real Tsavorite has no such split. Every       *)
(* session thread runs                                                     *)
(*                                                                         *)
(*     Resume()            -> Acquire: claim slot, announce epoch          *)
(*     ProtectAndDrain()   -> re-announce, run drain actions if safe       *)
(*     ... critical section: dereference a record ...                      *)
(*     Suspend()           -> Release: free the slot                       *)
(*                                                                         *)
(* and any of them may ALSO retire an object (BumpCurrentEpoch) and later   *)
(* free it once the scan says it is safe. So a thread can be simultaneously *)
(* a reader that must be protected FROM reclamation, and the reclaimer      *)
(* deciding whether someone else is protected.                              *)
(*                                                                         *)
(* That symmetry matters for two reasons the split-role specs cannot show:  *)
(*                                                                         *)
(*   1. A peer runs the scan while it is ITSELF protected, so its own slot  *)
(*      is one of the entries it reads. If its own announce were not        *)
(*      globally visible it could compute a SafeToReclaimEpoch that ignores *)
(*      itself and free memory out from under its own critical section.     *)
(*                                                                         *)
(*   2. Each peer owns a SEPARATE slot (Tsavorite hashes threads to         *)
(*      distinct entries), so the scan is a genuine multi-entry minimum     *)
(*      rather than a read of one word. A slot reading 0 is taken to mean   *)
(*      "nobody is here" and is simply skipped — which is exactly the       *)
(*      assumption a delayed announce would break.                          *)
(*                                                                         *)
(* Expected: HOLDS under both "tso" and "arm".                              *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

P1 == "P1"
P2 == "P2"
Peers == {P1, P2}

ThreadIdOf   == [p \in Peers |-> IF p = P1 THEN 11 ELSE 22]
EpochFieldOf == [p \in Peers |-> IF p = P1 THEN "slotEpoch1" ELSE "slotEpoch2"]
TidFieldOf   == [p \in Peers |-> IF p = P1 THEN "slotTid1" ELSE "slotTid2"]

VARIABLES memory, storeBuffer, inCriticalSection, owns, announcedEpoch, pc, triggerEpoch, retired
vars == <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch, pc, triggerEpoch, retired>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

Init ==
    /\ memory = [ currentEpoch |-> 1,
                  slotEpoch1 |-> 0, slotEpoch2 |-> 0,
                  slotTid1 |-> 0, slotTid2 |-> 0,
                  objectUnlinked |-> FALSE, objectFreed |-> FALSE ]
    /\ storeBuffer = [ p \in Peers |-> <<>> ]
    /\ inCriticalSection = [ p \in Peers |-> FALSE ]
    /\ owns = [ p \in Peers |-> FALSE ]
    /\ announcedEpoch = [ p \in Peers |-> 0 ]
    /\ pc = [ p \in Peers |-> "ReadEpoch" ]
    /\ triggerEpoch = 0
    /\ retired = FALSE

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, pc, triggerEpoch, retired>>

(***************************************************************************)
(* Resume() / Acquire — the fix                                            *)
(***************************************************************************)

ReadEpoch(p) ==
    /\ pc[p] = "ReadEpoch"
    /\ announcedEpoch' = [announcedEpoch EXCEPT ![p] = Load(p, "currentEpoch")]
    /\ pc' = [pc EXCEPT ![p] = "Claim"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, triggerEpoch, retired>>

\* CONTROL: production Acquire. The CAS is on threadId -- the wrong word --
\* so it orders nothing that matters, and the announce that follows is a plain
\* store that may linger in this core's store buffer.
Claim(p) ==
    /\ pc[p] = "Claim"
    /\ LET m == SB!Fenced(p)
       IN IF m[TidFieldOf[p]] = 0
          THEN /\ memory' = [m EXCEPT ![TidFieldOf[p]] = ThreadIdOf[p]]
               /\ owns' = [owns EXCEPT ![p] = TRUE]
               /\ pc' = [pc EXCEPT ![p] = "PublishThreadId"]
          ELSE /\ memory' = m
               /\ owns' = owns
               /\ pc' = [pc EXCEPT ![p] = "ReadEpoch"]
    /\ storeBuffer' = SB!Drained(p)
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, retired>>

\* The announce: slot.localCurrentEpoch = CurrentEpoch, plain and unfenced.
PublishThreadId(p) ==
    /\ pc[p] = "PublishThreadId"
    /\ storeBuffer' = SB!Buffer(p, EpochFieldOf[p], announcedEpoch[p])
    /\ pc' = [pc EXCEPT ![p] = "ProtectAndDrain"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, retired>>

(***************************************************************************)
(* ProtectAndDrain() — plain re-announce, then optionally run the scan.     *)
(* The store is deliberately unfenced: it only moves the slot forward, so a *)
(* stale read by another peer's scan is conservative.                       *)
(***************************************************************************)

ProtectAndDrain(p) ==
    /\ pc[p] = "ProtectAndDrain"
    /\ storeBuffer' = SB!Buffer(p, EpochFieldOf[p], Load(p, "currentEpoch"))
    /\ pc' = [pc EXCEPT ![p] = "Body"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, retired>>

(***************************************************************************)
(* Protected body. A peer may either dereference the object, or act as the  *)
(* reclaimer — retiring it and/or running the scan — all while protected.  *)
(***************************************************************************)

ReadObject(p) ==
    /\ pc[p] = "Body"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![p] = ~ Load(p, "objectUnlinked")]
    /\ pc' = [pc EXCEPT ![p] = "Dereference"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, retired>>

Dereference(p) ==
    /\ pc[p] = "Dereference"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![p] = FALSE]
    /\ pc' = [pc EXCEPT ![p] = "ClearThreadId"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, retired>>

\* Unlink + BumpCurrentEpoch, performed by a peer while it is protected.
\* Only one object is retired in this model, to bound the state space.
Retire(p) ==
    /\ pc[p] = "Body"
    /\ ~ retired
    /\ LET m == SB!Fenced(p)
           unlinked == [m EXCEPT !.objectUnlinked = TRUE]
       IN /\ memory' = [unlinked EXCEPT !.currentEpoch = unlinked.currentEpoch + 1]
          /\ triggerEpoch' = unlinked.currentEpoch
    /\ storeBuffer' = SB!Drained(p)
    /\ retired' = TRUE
    /\ pc' = [pc EXCEPT ![p] = "Body"]
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch>>

\* ComputeNewSafeToReclaimEpoch: scan EVERY slot and take the minimum over the
\* occupied ones. A slot holding 0 is read as "no thread here" and skipped —
\* the assumption the fix must keep true. Note the scanning peer's own slot is
\* among those read.
Scan(p) ==
    /\ pc[p] = "Body"
    /\ retired
    /\ ~ memory.objectFreed
    /\ LET e1 == memory.slotEpoch1
           e2 == memory.slotEpoch2
           withSlot(acc, e) == IF e > 0 THEN Min(acc, e) ELSE acc
           oldestOngoingCall == withSlot(withSlot(memory.currentEpoch, e1), e2)
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN memory' = [memory EXCEPT !.objectFreed = TRUE]
          ELSE UNCHANGED memory
    /\ UNCHANGED <<storeBuffer, inCriticalSection, owns, announcedEpoch, pc, triggerEpoch, retired>>

(***************************************************************************)
(* Suspend() / Release                                                     *)
(***************************************************************************)

ClearThreadId(p) ==
    /\ pc[p] = "ClearThreadId"
    /\ storeBuffer' = SB!Buffer(p, TidFieldOf[p], 0)
    /\ pc' = [pc EXCEPT ![p] = "ReleaseSlot"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, retired>>

\* Volatile.Write(slot.localCurrentEpoch, 0) — release store.
ReleaseSlot(p) ==
    /\ pc[p] = "ReleaseSlot"
    /\ memory' = SB!FencedStore(p, EpochFieldOf[p], 0)
    /\ storeBuffer' = SB!Drained(p)
    /\ owns' = [owns EXCEPT ![p] = FALSE]
    /\ pc' = [pc EXCEPT ![p] = "ReadEpoch"]
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, retired>>

Next ==
    \/ \E p \in Peers :
         \/ ReadEpoch(p) \/ Claim(p) \/ PublishThreadId(p) \/ ProtectAndDrain(p)
         \/ ReadObject(p) \/ Dereference(p) \/ Retire(p) \/ Scan(p)
         \/ ClearThreadId(p) \/ ReleaseSlot(p)
    \/ (\E p \in Peers : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* Invariants                                                              *)
(***************************************************************************)

\* No peer holds a reference to the object after it has been freed. This
\* covers the self-reclamation case too: a peer that frees while itself in a
\* critical section violates this.
NoUseAfterFree == ~ (memory.objectFreed /\ (\E p \in Peers : inCriticalSection[p]))

\* Distinct peers must never believe they own the same slot.
SlotsDisjoint ==
    ~ (owns[P1] /\ owns[P2] /\ EpochFieldOf[P1] = EpochFieldOf[P2])

\* A protected peer's announce must be globally visible, so no scan can read
\* its slot as "empty" while it is inside its critical section.
AnnounceVisibleWhileProtected ==
    \A p \in Peers :
        (owns[p] /\ inCriticalSection[p]) => memory[EpochFieldOf[p]] # 0
=============================================================================
