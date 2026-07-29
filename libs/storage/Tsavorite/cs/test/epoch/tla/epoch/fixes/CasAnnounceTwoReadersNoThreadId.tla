--------------------- MODULE CasAnnounceTwoReadersNoThreadId ---------------------
(***************************************************************************)
(* Two readers CONTENDING FOR THE SAME SLOT, plus a reclaimer, with the    *)
(* threadId field REMOVED ENTIRELY.                                        *)
(*                                                                         *)
(* This module answers a design question about the CAS-announce fix: once   *)
(* the epoch word (localCurrentEpoch) both owns the slot and publishes the  *)
(* epoch, is the separate threadId field still load-bearing for CORRECTNESS *)
(* (safety + slot ownership), or is it only diagnostic?                    *)
(*                                                                         *)
(*   CasAnnounceTwoReaders     keeps threadId as an ownership tag written   *)
(*                             after the claim and checked by              *)
(*                             ThisInstanceProtected(); needs ThreadIdIntact *)
(*                             and a release store in Release() to protect  *)
(*                             that tag from a reuse race.                  *)
(*                                                                         *)
(*   THIS MODULE              deletes threadId. The slot is owned purely by  *)
(*                             slotEpoch: 0 == free, non-zero == owned and   *)
(*                             announced. PublishThreadId / ClearThreadId    *)
(*                             disappear. The reader flow is                *)
(*                             ReadEpoch -> Claim(CAS) -> ReadObject ->      *)
(*                             Dereference -> ReleaseSlot(release store).    *)
(*                                                                         *)
(* If NoUseAfterFree and SlotExclusive still HOLD here, then threadId is    *)
(* NOT required for the reclamation-safety invariant nor for slot ownership *)
(* under the CAS fix — it is diagnostic only. The live control that proves  *)
(* this harness can still see breakage is                                  *)
(* CasAnnounceTwoReadersNoThreadIdNoCas.tla, which weakens the claim to a   *)
(* plain buffered store and VIOLATES.                                      *)
(*                                                                         *)
(* PROPERTIES                                                              *)
(*                                                                         *)
(*   NoUseAfterFree   no reader is inside its critical section when the     *)
(*                    reclaimer frees. The safety property that matters.    *)
(*                                                                         *)
(*   SlotExclusive    two readers never own the slot simultaneously.        *)
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

\* Plain read of CurrentEpoch taken BEFORE the claim, so the reclaimer may
\* bump it before the CAS lands and the announced value may be stale. Stale
\* means OLDER, which only lowers SafeToReclaimEpoch: the conservative side.
ReadEpoch(r) ==
    /\ readerPc[r] = "ReadEpoch"
    /\ announcedEpoch' = [announcedEpoch EXCEPT ![r] = Load(r, "currentEpoch")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Claim"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, triggerEpoch, reclaimerPc>>

\* Interlocked.CompareExchange(ref slot.localCurrentEpoch, e, 0).
\* A locked RMW: it drains this core's store buffer and operates on the
\* globally visible value. On failure the thread retries. The epoch word is
\* the sole ownership marker now — success of this CAS IS ownership.
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

\* Volatile.Write(ref slot.localCurrentEpoch, 0) — a RELEASE store that
\* publishes the slot as free. With threadId gone this is the only teardown
\* store, so there is no second store to order it against.
ReleaseSlot(r) ==
    /\ readerPc[r] = "ReleaseSlot"
    /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(r)
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Done"]
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

\* One shared slot, so the scan is over a single entry. A slot holding 0 is
\* read as "no thread is here", which is precisely the assumption the fix has
\* to keep true in the presence of reuse.
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

SlotExclusive == ~ (owns[R1] /\ owns[R2])
=============================================================================
