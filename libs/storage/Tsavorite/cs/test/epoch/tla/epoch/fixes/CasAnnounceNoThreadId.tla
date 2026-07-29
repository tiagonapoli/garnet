------------------------- MODULE CasAnnounceNoThreadId -------------------------
(***************************************************************************)
(* The CAS-carries-epoch fix with the threadId FIELD DELETED ENTIRELY.      *)
(*                                                                         *)
(* Once the claim is CAS(slot.localCurrentEpoch, 0 -> epoch), the epoch     *)
(* word is the ownership word: 0 means both "free to claim" and "no thread  *)
(* here". Entry.threadId no longer participates in the claim, so the        *)
(* natural question is whether it can simply be removed. This module is     *)
(* CasAnnounceTwoReaders.tla with slotThreadId, PublishThreadId and         *)
(* ClearThreadId deleted, and with the readers LOOPING rather than          *)
(* finishing, so the single shared slot is handed back and forth many times *)
(* rather than once.                                                        *)
(*                                                                         *)
(* Ownership is then carried by exactly two things:                        *)
(*                                                                         *)
(*   1. the globally visible slot word (slotEpoch # 0), maintained by a     *)
(*      locked RMW on claim and a release store on release; and            *)
(*   2. a THREAD-PRIVATE token, `owns[r]`, which models                     *)
(*      Metadata.Entries[instanceId] -- the thread-static entry index that  *)
(*      Acquire() sets and Release() resets to kInvalidIndex.               *)
(*                                                                         *)
(* The claim that this module checks is that (1) and (2) are together       *)
(* sufficient: no reader ever writes to a slot it does not own, and no      *)
(* reader's announce is ever invisible to a concurrent scan.                *)
(*                                                                         *)
(* Live control: CasAnnounceNoThreadIdStaleIndex.tla is this spec with ONE  *)
(* action added -- a departing reader that issues a second Release through  *)
(* a thread-private token it failed to invalidate. That control VIOLATES,   *)
(* so a HOLDS here is not an artifact of a model that cannot see the        *)
(* hazard. See also CasAnnounceTwoReadersNoCas.tla, the control for the     *)
(* announce itself.                                                        *)
(*                                                                         *)
(* Expected: HOLDS under both "tso" and "arm".                              *)
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
\* kInvalidIndex. The private token is dropped in the same step, which is the
\* property the whole removal argument rests on: after this the thread has no
\* way to name the slot, so it cannot write to it even though its threadId is
\* no longer there to disqualify it. The reader then loops and may re-acquire,
\* possibly the very slot its peer now holds.
ReleaseSlot(r) ==
    /\ readerPc[r] = "ReleaseSlot"
    /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(r)
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

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
         \/ Dereference(r) \/ ReleaseSlot(r)
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
