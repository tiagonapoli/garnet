--------------------- MODULE CasAnnounceReleaseLoadStore ---------------------
(***************************************************************************)
(* Load->Store: the reader's own dereference against its own unpublish.     *)
(*                                                                         *)
(* WHY THIS SPEC EXISTS                                                    *)
(*                                                                         *)
(* Every other spec in this folder asks what OTHER threads observe of the   *)
(* reader's publication. This one asks whether the reader is still using    *)
(* the object when it announces that it is not.                            *)
(*                                                                         *)
(*     ldr  x2, [data]          <- the dereference, inside the CS          *)
(*     str  xzr, [slotEpoch]    <- Release: PLAIN store on main            *)
(*                                                                         *)
(* AArch64 permits Load->Store reordering. The slot clear can be observed   *)
(* by other cores before the dereference has been satisfied, so a reclaimer *)
(* scanning in that window reads 0, concludes nothing is protected, and     *)
(* frees the object while the reader's load is still in flight.            *)
(*                                                                         *)
(* WHY THE OTHER SPECS CANNOT SEE IT                                       *)
(*                                                                         *)
(* Two independent reasons, both structural:                               *)
(*                                                                         *)
(*  1. MODULE StoreBuffer and MODULE WeakMemory both bind a load's value at *)
(*     its program point -- Load(p, f) reads the current memory or view and *)
(*     returns immediately. A store can be delayed (StoreLoad), stores can  *)
(*     be reordered among themselves (StoreStore, "arm"), and a load can    *)
(*     return a stale value (LoadLoad, "armlb" view lag). But there is no   *)
(*     notion of a load that has been ISSUED and not yet BOUND, so a load   *)
(*     can never be delayed past a later store. StoreBuffer's own header    *)
(*     says so: under "tso", "LoadStore order is preserved", and "arm"      *)
(*     "relaxes store visibility order and nothing else".                   *)
(*                                                                         *)
(*  2. In the other specs the critical section is not a memory access at    *)
(*     all. CasAnnounceOneReader's Dereference step only flips              *)
(*     inCriticalSection to FALSE and leaves memory and storeBuffer         *)
(*     UNCHANGED, so the window NoUseAfterFree watches has already closed   *)
(*     by the time Release runs. Even in a substrate that modelled          *)
(*     Load->Store there would be nothing left to reorder.                  *)
(*                                                                         *)
(* This module fixes (2) by making the dereference a real load, and (1) by  *)
(* splitting that load into an ISSUE step and a BIND step. The release      *)
(* store's ordering strength is then exactly the question of whether the    *)
(* bind must happen before the store.                                      *)
(*                                                                         *)
(* HOW Load->Store IS MODELLED                                             *)
(*                                                                         *)
(*   IssueDereference   the reader commits to dereferencing; the load is    *)
(*                      now in flight and its value is not yet determined.  *)
(*   BindDereference    the load is satisfied and takes whatever value is   *)
(*                      globally visible at that moment.                    *)
(*                                                                         *)
(* ReleaseSlot is enabled with the load still in flight only when the       *)
(* architecture permits Load->Store AND the store is plain. That is the     *)
(* whole content of the relaxation, stated once:                            *)
(*                                                                         *)
(*   Model = "tso"            x86-TSO preserves Load->Store: never enabled. *)
(*   ReleaseOrder = "release" a release store orders every preceding access *)
(*                            before itself: never enabled.                 *)
(*   otherwise                enabled -- the store may be observed first.   *)
(*                                                                         *)
(* SCOPE. This is a targeted model of one reordering, not a general         *)
(* treatment of in-flight loads: only the dereference is split, because it  *)
(* is the only load in the protocol that has a store after it in the same   *)
(* thread. A HOLDS here means "robust against Load->Store at the            *)
(* dereference", nothing wider.                                            *)
(*                                                                         *)
(* THE ALGORITHM MODELLED is the fix -- CAS carries the announce -- in      *)
(* every configuration. Only the Release store's ordering varies, so any    *)
(* violation reported here is attributable to that store alone.            *)
(*                                                                         *)
(* Expected:                                                               *)
(*   tso     + plain    HOLDS     (x86 needs no barrier here)              *)
(*   arm     + plain    VIOLATED  (the bug, and the liveness control)      *)
(*   arm     + release  HOLDS     (Volatile.Write -> STLR closes it)       *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANTS Model,        \* "tso" | "arm"
          ReleaseOrder  \* "plain" | "release"

Reader == "Reader"
Reclaimer == "Reclaimer"
Threads == {Reader, Reclaimer}

MyThreadId == 11
Unbound == "unbound"

VARIABLES memory, storeBuffer, sawObjectLive, owns, announcedEpoch,
          readerPc, triggerEpoch, reclaimerPc, derefInFlight, derefSaw
vars == <<memory, storeBuffer, sawObjectLive, owns, announcedEpoch,
          readerPc, triggerEpoch, reclaimerPc, derefInFlight, derefSaw>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

\* x86-TSO preserves Load->Store; the "arm" store-order relaxation is used here
\* as the marker for an architecture that does not.
LoadStoreRelaxed == Model # "tso"

\* A release store waits for every preceding access, including a load that has
\* been issued and not yet bound.
ReleaseStoreOrdersPriorLoads == ReleaseOrder = "release"

\* Whether the unpublish may execute while the dereference is still in flight.
MayUnpublishEarly == LoadStoreRelaxed /\ ~ReleaseStoreOrdersPriorLoads

Init ==
    /\ memory = [ currentEpoch |-> 1, slotEpoch |-> 0, slotThreadId |-> 0,
                  objectUnlinked |-> FALSE, objectFreed |-> FALSE ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ sawObjectLive = FALSE
    /\ owns = FALSE
    /\ announcedEpoch = 0
    /\ readerPc = "ReadEpoch"
    /\ triggerEpoch = 0
    /\ reclaimerPc = "Unlink"
    /\ derefInFlight = FALSE
    /\ derefSaw = Unbound

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED <<sawObjectLive, owns, announcedEpoch, readerPc, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

(***************************************************************************)
(* Reader                                                                  *)
(***************************************************************************)

ReadEpoch ==
    /\ readerPc = "ReadEpoch"
    /\ announcedEpoch' = Load(Reader, "currentEpoch")
    /\ readerPc' = "Claim"
    /\ UNCHANGED <<memory, storeBuffer, sawObjectLive, owns, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

\* Interlocked.CompareExchange(ref slot.localCurrentEpoch, epoch, 0):
\* claim and announce in one locked RMW. Unchanged in every configuration.
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
    /\ UNCHANGED <<sawObjectLive, announcedEpoch, triggerEpoch, reclaimerPc,
                   derefInFlight, derefSaw>>

PublishThreadId ==
    /\ readerPc = "PublishThreadId"
    /\ storeBuffer' = SB!Buffer(Reader, "slotThreadId", MyThreadId)
    /\ readerPc' = "ProtectAndDrain"
    /\ UNCHANGED <<memory, sawObjectLive, owns, announcedEpoch, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

ProtectAndDrain ==
    /\ readerPc = "ProtectAndDrain"
    /\ storeBuffer' = SB!Buffer(Reader, "slotEpoch", Load(Reader, "currentEpoch"))
    /\ readerPc' = "ReadObject"
    /\ UNCHANGED <<memory, sawObjectLive, owns, announcedEpoch, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

\* The reader reaches the object through the structure. If it observes the
\* object already unlinked it never held a pointer to it and does not
\* dereference. Dropping this guard makes the spec report violations that the
\* protocol makes unreachable -- the same false positive the herd7 composed
\* tests hit (see ../../../herd7/memory-ordering-bugs-found.md).
ReadObject ==
    /\ readerPc = "ReadObject"
    /\ sawObjectLive' = ~ Load(Reader, "objectUnlinked")
    /\ readerPc' = "IssueDereference"
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

\* The dereference is issued. Its value is NOT determined here: that is what
\* makes it a load in flight rather than an instantaneous read.
IssueDereference ==
    /\ readerPc = "IssueDereference"
    /\ derefInFlight' = sawObjectLive
    /\ readerPc' = "ClearThreadId"
    /\ UNCHANGED <<memory, storeBuffer, sawObjectLive, owns, announcedEpoch,
                   triggerEpoch, reclaimerPc, derefSaw>>

\* The in-flight load is satisfied, taking whatever is globally visible now.
\* Independent of readerPc, so it may interleave with the unpublish below.
BindDereference ==
    /\ derefInFlight
    /\ derefSaw = Unbound
    /\ derefSaw' = IF Load(Reader, "objectFreed") THEN "freed" ELSE "live"
    /\ UNCHANGED <<memory, storeBuffer, sawObjectLive, owns, announcedEpoch,
                   readerPc, triggerEpoch, reclaimerPc, derefInFlight>>

DerefStillInFlight == derefInFlight /\ derefSaw = Unbound

ClearThreadId ==
    /\ readerPc = "ClearThreadId"
    /\ storeBuffer' = SB!Buffer(Reader, "slotThreadId", 0)
    /\ readerPc' = "ReleaseSlot"
    /\ UNCHANGED <<memory, sawObjectLive, owns, announcedEpoch, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

\* The unpublish. Under a plain store on an architecture that relaxes
\* Load->Store it may execute with the dereference still outstanding; a release
\* store, or x86-TSO, forbids that.
ReleaseSlot ==
    /\ readerPc = "ReleaseSlot"
    /\ MayUnpublishEarly \/ ~DerefStillInFlight
    /\ storeBuffer' = SB!Buffer(Reader, "slotEpoch", 0)
    /\ owns' = FALSE
    /\ readerPc' = "Done"
    /\ UNCHANGED <<memory, sawObjectLive, announcedEpoch, triggerEpoch,
                   reclaimerPc, derefInFlight, derefSaw>>

(***************************************************************************)
(* Reclaimer -- unchanged from CasAnnounceOneReader                        *)
(***************************************************************************)

Unlink ==
    /\ reclaimerPc = "Unlink"
    /\ storeBuffer' = SB!Buffer(Reclaimer, "objectUnlinked", TRUE)
    /\ reclaimerPc' = "BumpCurrentEpoch"
    /\ UNCHANGED <<memory, sawObjectLive, owns, announcedEpoch, readerPc,
                   triggerEpoch, derefInFlight, derefSaw>>

BumpCurrentEpoch ==
    /\ reclaimerPc = "BumpCurrentEpoch"
    /\ LET fencedMemory == SB!Fenced(Reclaimer)
       IN /\ memory' = [fencedMemory EXCEPT !.currentEpoch = fencedMemory.currentEpoch + 1]
          /\ triggerEpoch' = fencedMemory.currentEpoch
    /\ storeBuffer' = SB!Drained(Reclaimer)
    /\ reclaimerPc' = "ComputeNewSafeToReclaimEpoch"
    /\ UNCHANGED <<sawObjectLive, owns, announcedEpoch, readerPc,
                   derefInFlight, derefSaw>>

ComputeNewSafeToReclaimEpoch ==
    /\ reclaimerPc = "ComputeNewSafeToReclaimEpoch"
    /\ LET slot               == memory.slotEpoch
           oldestOngoingCall  == IF slot > 0 THEN Min(memory.currentEpoch, slot) ELSE memory.currentEpoch
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN /\ memory' = [memory EXCEPT !.objectFreed = TRUE]
               /\ reclaimerPc' = "Done"
          ELSE /\ UNCHANGED <<memory, reclaimerPc>>
    /\ UNCHANGED <<storeBuffer, sawObjectLive, owns, announcedEpoch, readerPc,
                   triggerEpoch, derefInFlight, derefSaw>>

Next ==
    \/ ReadEpoch \/ Claim \/ PublishThreadId \/ ProtectAndDrain
    \/ ReadObject \/ IssueDereference \/ BindDereference
    \/ ClearThreadId \/ ReleaseSlot
    \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* The property. A dereference that was issued because the object still     *)
(* looked live must not be satisfied from memory that has since been freed. *)
(***************************************************************************)
NoUseAfterFreeDereference == derefSaw # "freed"

\* The same statement as a window rather than an outcome: while the reader has
\* a dereference outstanding, the object it is reading must not be freed.
NoFreeWhileDereferenceInFlight == ~ (DerefStillInFlight /\ memory.objectFreed)
=============================================================================
