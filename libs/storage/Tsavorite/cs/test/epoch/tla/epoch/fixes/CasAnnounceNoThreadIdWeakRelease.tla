------------------------- MODULE CasAnnounceNoThreadIdWeakRelease -------------------------
(***************************************************************************)
(* CasAnnounceNoThreadId.tla with the unpublish weakened from a full        *)
(* barrier to a RELEASE STORE that may linger in the store buffer.          *)
(*                                                                         *)
(* Why this module exists. In CasAnnounceNoThreadId the release of the slot *)
(* is modelled as SB!FencedStore + SB!Drained, i.e. as a full barrier. That *)
(* is stronger than the code, which uses Volatile.Write -- release only, an *)
(* `stlr` on AArch64, which does NOT drain the store buffer. In the version *)
(* of the fix that still carries threadId, that over-approximation is       *)
(* harmless because the two plain slotThreadId stores remain buffered and   *)
(* so the store-order relaxation still has something to act on. Deleting    *)
(* threadId deletes those two stores -- and with them the ONLY unfenced     *)
(* reader stores in the whole algorithm, which is why the "tso" and "arm"   *)
(* runs of CasAnnounceNoThreadId explore an identical state space.          *)
(*                                                                         *)
(* That identity is the result, not a defect: the fix works precisely       *)
(* because it leaves the relaxation nothing to reorder. But it also means   *)
(* the "arm" verdict there is not independent evidence, so this module      *)
(* restores a buffered reader store -- the release itself -- and re-asks    *)
(* the question under a model that can actually delay it.                   *)
(*                                                                         *)
(* Expected: HOLDS under both "tso" and "arm". A release that lands late    *)
(* makes the slot look OCCUPIED for longer, which is conservative: it can   *)
(* delay reclamation but cannot free under a live reader.                   *)
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

\* Volatile.Write(ref slot.localCurrentEpoch, 0) modelled honestly: a release
\* store goes into the store buffer and becomes visible whenever it drains.
\* The private token is dropped in the same step, so the departing thread can
\* no longer name the slot even while its clearing store is still in flight.
ReleaseSlot(r) ==
    /\ readerPc[r] = "ReleaseSlot"
    /\ storeBuffer' = SB!Buffer(r, "slotEpoch", 0)
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<memory, inCriticalSection, announcedEpoch, triggerEpoch, reclaimerPc>>

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
