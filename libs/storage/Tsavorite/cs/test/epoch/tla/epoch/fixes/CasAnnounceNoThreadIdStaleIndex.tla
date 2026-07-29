--------------------- MODULE CasAnnounceNoThreadIdStaleIndex ---------------------
(***************************************************************************)
(* CONTROL for CasAnnounceNoThreadId.tla. Identical in every respect except *)
(* one added action: a departing reader issues a SECOND Release through a    *)
(* thread-private entry index it failed to invalidate.                      *)
(*                                                                         *)
(* This is the precise scenario in which deleting Entry.threadId costs      *)
(* something. Production guards it with                                     *)
(*                                                                         *)
(*     ThisInstanceProtected() ==                                          *)
(*         entry != kInvalidIndex && table[entry].threadId == myThreadId    *)
(*                                                                         *)
(* and callers such as TrySuspend()/ResumeIfNotProtected() gate Release()   *)
(* on it. The `entry != kInvalidIndex` half is thread-private and cannot    *)
(* detect a stale index that outlived the slot -- for instance a            *)
(* Metadata.Entries[instanceId] left set when a LightEpoch was disposed     *)
(* while protected and its instanceId was then handed to a new instance by  *)
(* SelectInstance(). The `threadId == myThreadId` half is what catches      *)
(* that, because the new owner of the slot has a different threadId.        *)
(*                                                                         *)
(* Delete threadId and only the thread-private half survives, so the stale  *)
(* Release goes through and wipes a live reader's announce. The scan then   *)
(* reads the slot as empty and frees under it.                              *)
(*                                                                         *)
(* Expected: VIOLATED under both "tso" and "arm". Its purpose is to prove   *)
(* that CasAnnounceNoThreadId's HOLDS is a real result and not an artifact  *)
(* of a model too coarse to express a stale-owner write.                   *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

R1 == "R1"
R2 == "R2"
Reclaimer == "Reclaimer"
Readers == {R1, R2}
Threads == {R1, R2, Reclaimer}

VARIABLES memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc
vars == <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

Init ==
    /\ memory = [ currentEpoch |-> 1, slotEpoch |-> 0,
                  objectUnlinked |-> FALSE, objectFreed |-> FALSE ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ inCriticalSection = [ r \in Readers |-> FALSE ]
    /\ owns = [ r \in Readers |-> FALSE ]
    /\ announcedEpoch = [ r \in Readers |-> 0 ]
    /\ readerPc = [ r \in Readers |-> "ReadEpoch" ]
    /\ triggerEpoch = 0
    /\ reclaimerPc = "Unlink"

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc>>

(***************************************************************************)
(* Reader                                                                  *)
(***************************************************************************)

ReadEpoch(r) ==
    /\ readerPc[r] = "ReadEpoch"
    /\ announcedEpoch' = [announcedEpoch EXCEPT ![r] = Load(r, "currentEpoch")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Claim"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, triggerEpoch, reclaimerPc>>

\* Interlocked.CompareExchange(ref slot.localCurrentEpoch, e, 0). The success
\* of this locked RMW is now the ENTIRE ownership decision: there is no
\* threadId to CAS and none to publish afterwards. `owns` is set here, which
\* models Acquire() writing the reserved index into Metadata.Entries.
Claim(r) ==
    /\ readerPc[r] = "Claim"
    /\ LET m == SB!Fenced(r)
       IN IF m.slotEpoch = 0
          THEN /\ memory' = [m EXCEPT !.slotEpoch = announcedEpoch[r]]
               /\ owns' = [owns EXCEPT ![r] = TRUE]
               /\ readerPc' = [readerPc EXCEPT ![r] = "ReadObject"]
          ELSE /\ memory' = m
               /\ owns' = owns
               /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ storeBuffer' = SB!Drained(r)
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

ReadObject(r) ==
    /\ readerPc[r] = "ReadObject"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = ~ Load(r, "objectUnlinked")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Dereference"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

Dereference(r) ==
    /\ readerPc[r] = "Dereference"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReleaseSlot"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Volatile.Write(ref slot.localCurrentEpoch, 0), then Metadata.Entries[..] =
\* kInvalidIndex. The ONLY difference from CasAnnounceNoThreadId is the next
\* pc: this reader still holds a way to name the slot.
ReleaseSlot(r) ==
    /\ readerPc[r] = "ReleaseSlot"
    /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(r)
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "StaleRelease"]
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

\* A Release() reached through a stale entry index. With threadId present,
\* ThisInstanceProtected() reads table[entry].threadId, finds the NEW owner's
\* id (or 0), returns FALSE, and this store never happens. With threadId
\* deleted there is nothing left to consult, so the store lands on whatever
\* the slot now holds.
StaleRelease(r) ==
    /\ readerPc[r] = "StaleRelease"
    /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(r)
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

(***************************************************************************)
(* Reclaimer — unchanged from production                                   *)
(***************************************************************************)

Unlink ==
    /\ reclaimerPc = "Unlink"
    /\ storeBuffer' = SB!Buffer(Reclaimer, "objectUnlinked", TRUE)
    /\ reclaimerPc' = "BumpCurrentEpoch"
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch>>

BumpCurrentEpoch ==
    /\ reclaimerPc = "BumpCurrentEpoch"
    /\ LET fencedMemory == SB!Fenced(Reclaimer)
       IN /\ memory' = [fencedMemory EXCEPT !.currentEpoch = fencedMemory.currentEpoch + 1]
          /\ triggerEpoch' = fencedMemory.currentEpoch
    /\ storeBuffer' = SB!Drained(Reclaimer)
    /\ reclaimerPc' = "ComputeNewSafeToReclaimEpoch"
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, readerPc>>

\* The scan reads ONLY the epoch word. It never read threadId in production
\* either (see ComputeNewSafeToReclaimEpoch in LightEpoch.cs), which is why
\* deleting the field cannot by itself change what the scan concludes.
ComputeNewSafeToReclaimEpoch ==
    /\ reclaimerPc = "ComputeNewSafeToReclaimEpoch"
    /\ LET slot              == memory.slotEpoch
           oldestOngoingCall == IF slot > 0 THEN Min(memory.currentEpoch, slot) ELSE memory.currentEpoch
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN /\ memory' = [memory EXCEPT !.objectFreed = TRUE]
               /\ reclaimerPc' = "Done"
          ELSE /\ UNCHANGED <<memory, reclaimerPc>>
    /\ UNCHANGED <<storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch>>

Next ==
    \/ \E r \in Readers :
         \/ ReadEpoch(r) \/ Claim(r) \/ ReadObject(r)
         \/ Dereference(r) \/ ReleaseSlot(r) \/ StaleRelease(r)
    \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* Invariants                                                              *)
(***************************************************************************)

NoUseAfterFree == ~ (memory.objectFreed /\ (\E r \in Readers : inCriticalSection[r]))

\* Two readers never own the slot simultaneously. With threadId gone this is
\* carried entirely by the claim CAS.
SlotExclusive == ~ (owns[R1] /\ owns[R2])

\* A reader that believes it owns the slot must still find its announce in
\* the slot. This is the property threadId was implicitly protecting in
\* CasAnnounceTwoReaders (ThreadIdIntact); with the field gone, the epoch
\* word has to carry it on its own.
OwnedSlotNotWiped == \A r \in Readers : owns[r] => memory.slotEpoch # 0

\* No scan may read a protected reader's slot as empty.
AnnounceVisibleWhileProtected ==
    \A r \in Readers : (owns[r] /\ inCriticalSection[r]) => memory.slotEpoch # 0
=============================================================================
