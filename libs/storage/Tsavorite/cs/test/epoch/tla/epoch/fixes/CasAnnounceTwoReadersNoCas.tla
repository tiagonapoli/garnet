------------------------ MODULE CasAnnounceTwoReadersNoCas ------------------------
(***************************************************************************)
(* NEGATIVE CONTROL for the claim CAS itself.                              *)
(*                                                                         *)
(* Identical to MODULE CasAnnounceTwoReaders except that Claim is a PLAIN,  *)
(* non-atomic, buffered store: the reader tests the slot and then writes it *)
(* without a locked RMW, so nothing drains its store buffer and nothing     *)
(* orders the announce before the reader's later load.                      *)
(*                                                                         *)
(* Expected: VIOLATED. This is the whole point of the fix — if the model    *)
(* still reported "no error" with the CAS removed, it would be proving      *)
(* nothing about the CAS being what closes the window.                      *)
(***************************************************************************)
(*                                                                         *)
(* The single-reader specs can only exercise the announce->scan race. They  *)
(* cannot see anything that arises from SLOT REUSE, because a slot that is  *)
(* never handed from one thread to another is never reused. That matters    *)
(* here specifically, because the proposed fix CHANGES WHICH FIELD OWNS THE *)
(* SLOT:                                                                   *)
(*                                                                         *)
(*   today     CAS(slot.threadId,          0 -> myThreadId)  claims        *)
(*             slot.localCurrentEpoch = e                    announces      *)
(*                                                                         *)
(*   proposed  e = CurrentEpoch                              (may be stale) *)
(*             CAS(slot.localCurrentEpoch, 0 -> e)           claims AND     *)
(*                                                           announces      *)
(*             slot.threadId = myThreadId                    diagnostic     *)
(*                                                                         *)
(* So localCurrentEpoch now does double duty: it is both the ownership word *)
(* and the published epoch. The value 0 means BOTH "no thread here" (to the *)
(* reclaimer's scan) and "free to claim" (to a competing acquirer). This    *)
(* module exists to check that overloading against a second reader.        *)
(*                                                                         *)
(* Both readers deliberately share ONE slot, which is the worst case: it is *)
(* the state Tsavorite reaches when two threads hash to the same entry, and *)
(* it forces the CAS-failure/retry path on every contended acquire.        *)
(*                                                                         *)
(* PROPERTIES                                                              *)
(*                                                                         *)
(*   NoUseAfterFree   no reader is inside its critical section when the     *)
(*                    reclaimer frees. The safety property that matters.    *)
(*                                                                         *)
(*   SlotExclusive    two readers never own the slot simultaneously. If     *)
(*                    this broke, the second claimant would overwrite the   *)
(*                    first's announce and the reclaimer would compute a    *)
(*                    safe-to-reclaim epoch that ignores a live reader.     *)
(*                                                                         *)
(*   ThreadIdIntact   a departing reader's plain `threadId = 0` store never *)
(*                    lands after the next owner has published its own      *)
(*                    threadId. This is the property that justifies the     *)
(*                    release store in Release(); see                       *)
(*                    CasAnnounceTwoReadersPlainRelease.tla, which is the   *)
(*                    identical spec with that one store weakened.          *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

R1 == "R1"
R2 == "R2"
Reclaimer == "Reclaimer"
Readers == {R1, R2}
Threads == {R1, R2, Reclaimer}

\* Distinct, non-zero: 0 is reserved to mean "no thread".
ThreadIdOf == [r \in Readers |-> IF r = R1 THEN 11 ELSE 22]

VARIABLES memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc
vars == <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

Init ==
    /\ memory = [ currentEpoch |-> 1, slotEpoch |-> 0, slotThreadId |-> 0,
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

\* NEGATIVE CONTROL: plain test-then-set, no locked RMW. The store is buffered
\* and the buffer is not drained, so the announce can sit invisible to the
\* reclaimer's scan while this reader proceeds into its critical section.
Claim(r) ==
    /\ readerPc[r] = "Claim"
    /\ IF Load(r, "slotEpoch") = 0
       THEN /\ storeBuffer' = SB!Buffer(r, "slotEpoch", announcedEpoch[r])
            /\ owns' = [owns EXCEPT ![r] = TRUE]
            /\ readerPc' = [readerPc EXCEPT ![r] = "PublishThreadId"]
       ELSE /\ UNCHANGED <<storeBuffer, owns>>
            /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<memory, inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Plain store. threadId is diagnostic once the epoch word owns the slot.
PublishThreadId(r) ==
    /\ readerPc[r] = "PublishThreadId"
    /\ storeBuffer' = SB!Buffer(r, "slotThreadId", ThreadIdOf[r])
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadObject"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

ReadObject(r) ==
    /\ readerPc[r] = "ReadObject"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = ~ Load(r, "objectUnlinked")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Dereference"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

Dereference(r) ==
    /\ readerPc[r] = "Dereference"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ClearThreadId"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Plain store, cleared BEFORE the slot is published as free.
ClearThreadId(r) ==
    /\ readerPc[r] = "ClearThreadId"
    /\ storeBuffer' = SB!Buffer(r, "slotThreadId", 0)
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReleaseSlot"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Volatile.Write(ref slot.localCurrentEpoch, 0) — a RELEASE store: every
\* earlier store by this thread is visible before it. That is what stops the
\* ClearThreadId above from landing after the next owner has claimed.
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
         \/ ReadEpoch(r) \/ Claim(r) \/ PublishThreadId(r) \/ ReadObject(r)
         \/ Dereference(r) \/ ClearThreadId(r) \/ ReleaseSlot(r)
    \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* Invariants                                                              *)
(***************************************************************************)

NoUseAfterFree == ~ (memory.objectFreed /\ (\E r \in Readers : inCriticalSection[r]))

SlotExclusive == ~ (owns[R1] /\ owns[R2])

\* True between PublishThreadId and ClearThreadId. Guarded on the reader's own
\* buffer being empty so we only judge the slot once this reader's own publish
\* has actually become globally visible.
Published(r) == readerPc[r] \in {"ReadObject", "Dereference"}

ThreadIdIntact ==
    \A r \in Readers :
        (owns[r] /\ Published(r) /\ storeBuffer[r] = <<>>) => memory.slotThreadId = ThreadIdOf[r]
=============================================================================
