------------------------ MODULE CasAnnounceOneReader ------------------------
(***************************************************************************)
(* CAS-carries-epoch fix, minimal configuration: ONE reader, ONE reclaimer. *)
(*                                                                         *)
(* This is the direct counterpart to LightEpoch.tla (which exhibits the     *)
(* bug) and FixedLightEpochWithMemoryBarrier.tla (the naive fix). Same      *)
(* scenario, same invariant, so the three are directly comparable.          *)
(*                                                                         *)
(* THE BUG THIS MUST NOT REPRODUCE                                          *)
(*   Production Acquire() claims the slot with a CAS on threadId and then   *)
(*   announces the epoch with a PLAIN store:                                *)
(*                                                                         *)
(*       CAS(slot.threadId, 0 -> myThreadId)   <- barrier, but wrong word   *)
(*       slot.localCurrentEpoch = CurrentEpoch <- plain, can sit in the SB   *)
(*       ... load object ...                                                *)
(*                                                                         *)
(*   The announce can still be in this core's store buffer when the         *)
(*   reclaimer scans, so the scan reads 0 = "no thread here" and frees      *)
(*   memory the reader is about to dereference.                             *)
(*                                                                         *)
(* THE FIX MODELLED HERE                                                    *)
(*   Claim the slot WITH the announce, by CASing the epoch word itself:     *)
(*                                                                         *)
(*       CAS(slot.localCurrentEpoch, 0 -> epoch)  <- claim AND announce      *)
(*       slot.threadId = myThreadId               <- plain, follows          *)
(*                                                                         *)
(*   The announce is now part of a locked RMW, so it is globally visible    *)
(*   before any load in the protected region — with no ADDED barrier, since  *)
(*   the CAS was already there. Only the word being CASed changed.          *)
(*                                                                         *)
(* This module also models ProtectAndDrain (Tsavorite's per-operation       *)
(* refresh), which the fix deliberately leaves as a PLAIN unfenced store.   *)
(* That is safe because it only ever moves the slot FORWARD: a scan that    *)
(* observes the stale, older value computes a SMALLER SafeToReclaimEpoch    *)
(* and therefore reclaims less. Only the 0 -> epoch transition is           *)
(* ordering-sensitive, and that one is now carried by the CAS.              *)
(*                                                                         *)
(* Expected: HOLDS under both "tso" and "arm".                              *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model

Reader == "Reader"
Reclaimer == "Reclaimer"
Threads == {Reader, Reclaimer}

MyThreadId == 11

VARIABLES memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc
vars == <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

Init ==
    /\ memory = [ currentEpoch |-> 1, slotEpoch |-> 0, slotThreadId |-> 0,
                  objectUnlinked |-> FALSE, objectFreed |-> FALSE ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ inCriticalSection = FALSE
    /\ owns = FALSE
    /\ announcedEpoch = 0
    /\ readerPc = "ReadEpoch"
    /\ triggerEpoch = 0
    /\ reclaimerPc = "Unlink"

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch, reclaimerPc>>

(***************************************************************************)
(* Reader — Tsavorite's sequence: Acquire, ProtectAndDrain, CS, Suspend.   *)
(***************************************************************************)

\* Read CurrentEpoch BEFORE the claim. A stale (older) read is safe: it can only
\* lower oldestOngoingCall, hence SafeToReclaimEpoch — the conservative side.
ReadEpoch ==
    /\ readerPc = "ReadEpoch"
    /\ announcedEpoch' = Load(Reader, "currentEpoch")
    /\ readerPc' = "Claim"
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, triggerEpoch, reclaimerPc>>

\* Interlocked.CompareExchange(ref slot.localCurrentEpoch, epoch, 0):
\* claim and announce in one locked RMW.
Claim ==
    /\ readerPc = "Claim"
    /\ LET m == SB!Fenced(Reader)
       IN IF m.slotEpoch = 0
          THEN /\ memory' = [m EXCEPT !.slotEpoch = announcedEpoch]
               /\ owns' = TRUE
               /\ readerPc' = "PublishThreadId"
          ELSE /\ memory' = m
               /\ owns' = owns
               /\ readerPc' = "ReadEpoch"
    /\ storeBuffer' = SB!Drained(Reader)
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Plain store. Not decorative: ThisInstanceProtected() compares slot.threadId
\* against the thread's own id.
PublishThreadId ==
    /\ readerPc = "PublishThreadId"
    /\ storeBuffer' = SB!Buffer(Reader, "slotThreadId", MyThreadId)
    /\ readerPc' = "ProtectAndDrain"
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

\* ProtectAndDrain: re-announce the current epoch. PLAIN and unfenced by design.
\* The slot is already non-zero, and this only moves it forward, so a stale read
\* by the scan is conservative.
ProtectAndDrain ==
    /\ readerPc = "ProtectAndDrain"
    /\ storeBuffer' = SB!Buffer(Reader, "slotEpoch", Load(Reader, "currentEpoch"))
    /\ readerPc' = "ReadObject"
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

ReadObject ==
    /\ readerPc = "ReadObject"
    /\ inCriticalSection' = ~ Load(Reader, "objectUnlinked")
    /\ readerPc' = "Dereference"
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

Dereference ==
    /\ readerPc = "Dereference"
    /\ inCriticalSection' = FALSE
    /\ readerPc' = "ClearThreadId"
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

ClearThreadId ==
    /\ readerPc = "ClearThreadId"
    /\ storeBuffer' = SB!Buffer(Reader, "slotThreadId", 0)
    /\ readerPc' = "ReleaseSlot"
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, triggerEpoch, reclaimerPc>>

\* Volatile.Write(ref slot.localCurrentEpoch, 0): a RELEASE store, so the
\* ClearThreadId above cannot land after the next owner has claimed the slot.
ReleaseSlot ==
    /\ readerPc = "ReleaseSlot"
    /\ memory' = SB!FencedStore(Reader, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(Reader)
    /\ owns' = FALSE
    /\ readerPc' = "Done"
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

ComputeNewSafeToReclaimEpoch ==
    /\ reclaimerPc = "ComputeNewSafeToReclaimEpoch"
    /\ LET slot               == memory.slotEpoch
           oldestOngoingCall  == IF slot > 0 THEN Min(memory.currentEpoch, slot) ELSE memory.currentEpoch
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN /\ memory' = [memory EXCEPT !.objectFreed = TRUE]
               /\ reclaimerPc' = "Done"
          ELSE /\ UNCHANGED <<memory, reclaimerPc>>
    /\ UNCHANGED <<storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc, triggerEpoch>>

Next ==
    \/ ReadEpoch \/ Claim \/ PublishThreadId \/ ProtectAndDrain
    \/ ReadObject \/ Dereference \/ ClearThreadId \/ ReleaseSlot
    \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* The property that matters: memory is never freed while the reader holds  *)
(* a reference to it.                                                       *)
(***************************************************************************)
NoUseAfterFree == ~ (memory.objectFreed /\ inCriticalSection)

\* A protected reader must be visible as protected. This is the direct
\* statement of what the CAS buys: once the claim succeeds, the announce is
\* already global, so the scan can never read this slot as 0.
AnnounceVisibleWhileProtected == (owns /\ inCriticalSection) => memory.slotEpoch # 0
=============================================================================
