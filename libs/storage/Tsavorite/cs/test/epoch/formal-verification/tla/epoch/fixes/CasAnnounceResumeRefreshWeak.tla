------------------- MODULE CasAnnounceResumeRefreshWeak -------------------
(***************************************************************************)
(* The CAS-carries-epoch fix, checked against Tsavorite's real per-operation *)
(* call sequence on the WeakMemory substrate (per-processor views).         *)
(*                                                                         *)
(* WHY THIS SPEC EXISTS                                                    *)
(*                                                                         *)
(* MODULE CasAnnounceSymmetricPeers HOLDS under StoreBuffer's "arm" model,  *)
(* yet the same algorithm faults on a Neoverse-N2 within seconds under      *)
(* --pattern resume-and-refresh. That HOLDS was never evidence: StoreBuffer *)
(* has a single shared `memory`, so a load always returns the newest        *)
(* propagated value and a reader cannot observe two fields out of order.    *)
(* The hardware failure needs exactly that, so the model could not express  *)
(* it. See MODULE WeakMemory for the substrate that can.                   *)
(*                                                                         *)
(* THE MECHANISM THIS SPEC IS BUILT TO EXPOSE                              *)
(*                                                                         *)
(*   1. The reader acquires at epoch E. The CAS carries the announce, so    *)
(*      slot = E is globally visible - the acquire announce is NOT the bug. *)
(*   2. The reclaimer unlinks the object and bumps the epoch to E+1. The    *)
(*      bump is a locked RMW, so on the RECLAIMER side the unlink is        *)
(*      ordered before the new epoch value.                                 *)
(*   3. The reader refreshes. It observes the new currentEpoch = E+1 but    *)
(*      has NOT yet observed the unlink - a writer-side barrier orders the  *)
(*      writer's accesses, it does not stop an unfenced reader from         *)
(*      satisfying its loads in a different order.                          *)
(*   4. The reader announces E+1. This RAISES SafeToReclaimEpoch to E,      *)
(*      authorising the free of the object published in epoch E.            *)
(*   5. The reader then acts on its stale objectUnlinked = FALSE and        *)
(*      dereferences the object the reclaimer just freed.                   *)
(*                                                                         *)
(* So the ordering-sensitive step is NOT only the 0 -> E acquire announce.  *)
(* The E -> E' refresh announce is equally dangerous, for a different       *)
(* reason: a stale acquire announce reclaims LESS and is safe, but a stale  *)
(* view at the refresh announce lets the reader vouch for an epoch it has   *)
(* not actually caught up to.                                              *)
(*                                                                         *)
(* CONSTANT RefreshOrder — the ProtectAndDrain announce, matching the       *)
(* LE_REFRESH_ORDER knob in FixedLightEpochWithCasAnnounce.cs and the       *)
(* hardware A/B:                                                           *)
(*                                                                         *)
(*   "plain"    a plain store. Expected: VIOLATED under "armlb".            *)
(*   "release"  a release store (STLR). It publishes the announce in order  *)
(*              but does NOT refresh the reader's view, so the reader's     *)
(*              later load of objectUnlinked can still be stale.            *)
(*              Expected: VIOLATED under "armlb".                          *)
(*   "fence"    plain store + full barrier (DMB ISH / Interlocked.Memory-   *)
(*              Barrier). The barrier refreshes the reader's whole view, so *)
(*              a reader that has announced E+1 has necessarily observed    *)
(*              the unlink. Expected: HOLDS.                                *)
(*                                                                         *)
(* That plain and release both fail while fence survives is the signature   *)
(* the hardware produced, and reproducing it here is the point of the spec. *)
(* If "release" HOLDS here but fails on hardware, this substrate is still   *)
(* too strong and must be reported as such rather than trusted.             *)
(*                                                                         *)
(* The reclaimer is modelled UNPROTECTED (it owns no epoch slot). Its       *)
(* absence from the min-scan can only RAISE SafeToReclaimEpoch, hence       *)
(* reclaim more aggressively, so proving the fix safe here is strictly      *)
(* stronger than proving it against a protected reclaimer.                  *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model          \* "tso" | "arm" | "armlb" — see MODULE WeakMemory
CONSTANT RefreshOrder   \* "plain" | "release" | "acqload" | "acqloadmp" | "acqloadself" | "fence"
CONSTANT AcquireOrder   \* "cas" | "release" | "plain" — how the FIRST announce is published
(***************************************************************************)
(* LIMITATION of AcquireOrder = "cas". This models the announce as a full   *)
(* drain of the store buffer plus a refresh of the announcing processor's   *)
(* own view, which is a faithful model of STR followed by DMB ISH, and (per *)
(* herd7 against the official aarch64.cat) of LSE CASAL as well.            *)
(*                                                                         *)
(* It CANNOT express the armv8.0 LDAXR/STLXR exclusive loop, which herd7    *)
(* shows is strictly weaker: an acquire load composed with a release store  *)
(* does not yield a StoreLoad barrier, and the buggy outcome is ALLOWED     *)
(* there. So a HOLDS under this mode says nothing about the non-LSE codegen *)
(* path. Do not read it as covering "whatever the CAS compiles to".         *)
(***************************************************************************)

Reader == "Reader"
Reclaimer == "Reclaimer"
Threads == {Reader, Reclaimer}

VARIABLES memory, storeBuffer, view,
          readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, readerPc, reclaimerPc
vars == <<memory, storeBuffer, view,
          readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, readerPc, reclaimerPc>>

WM == INSTANCE WeakMemory
Load(p, f) == WM!Load(p, f)
Min(a, b)  == WM!Min(a, b)

otherVars == <<readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, readerPc, reclaimerPc>>

InitMemory == [ currentEpoch |-> 1, localCurrentEpoch |-> 0, objectUnlinked |-> FALSE, objectFreed |-> FALSE ]

Init ==
    /\ memory = InitMemory
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ view = [ p \in Threads |-> InitMemory ]
    /\ readerInCriticalSection = FALSE
    /\ readerAnnouncedEpoch = 0
    /\ triggerEpoch = 0
    /\ readerPc = "Acquire"
    /\ reclaimerPc = "Unlink"

PropagateOne(p) ==
    /\ WM!PropagateOne(p)
    /\ UNCHANGED otherVars

\* A processor catches up on one field. This action is the whole of the
\* reader-side reordering; it is enabled only when Model = "armlb".
ObserveField(p) ==
    /\ WM!ObserveAny(p)
    /\ UNCHANGED otherVars

\* Reader ---------------------------------------------------------------------
\* Resume() -> Acquire. The CAS claims the slot AND carries the announce in one
\* locked RMW, so the epoch is globally visible the instant the slot is owned.
\* This is the fix under test, and it is why the acquire announce is not the
\* failing step here.
Acquire ==
    /\ readerPc = "Acquire"
    /\ Load(Reader, "localCurrentEpoch") = 0
    /\ readerAnnouncedEpoch' = Load(Reader, "currentEpoch")
    /\ \/ /\ AcquireOrder = "cas"
          /\ memory' = WM!FencedStore(Reader, "localCurrentEpoch", readerAnnouncedEpoch')
          /\ view' = WM!FencedStoreView(Reader, "localCurrentEpoch", readerAnnouncedEpoch')
          /\ storeBuffer' = WM!Drained(Reader)
       \* A release store is cheaper than a CAS. It publishes the announce
       \* without draining, so it answers whether the CAS is really needed.
       \/ /\ AcquireOrder = "release"
          /\ memory' = WM!ReleaseStore(Reader, "localCurrentEpoch", readerAnnouncedEpoch')
          /\ view' = WM!ReleaseStoreView(Reader, "localCurrentEpoch", readerAnnouncedEpoch')
          /\ storeBuffer' = WM!Drained(Reader)
       \* The original, unfenced announce: the store lingers in the buffer and
       \* the reclaimer reads the slot as 0. Sensitivity control for this axis.
       \/ /\ AcquireOrder = "plain"
          /\ storeBuffer' = WM!Buffer(Reader, "localCurrentEpoch", readerAnnouncedEpoch')
          /\ UNCHANGED <<memory, view>>
    /\ readerPc' = "ProtectAndDrain"
    /\ UNCHANGED <<readerInCriticalSection, triggerEpoch, reclaimerPc>>

\* InternalRefresh() -> ProtectAndDrain. The announce that the CAS does NOT
\* cover, and the one the hardware A/B varies.
ProtectAndDrain ==
    /\ readerPc = "ProtectAndDrain"
    /\ LET e == Load(Reader, "currentEpoch")
       IN  \/ /\ RefreshOrder = "plain"
              /\ storeBuffer' = WM!Buffer(Reader, "localCurrentEpoch", e)
              /\ UNCHANGED <<memory, view>>
           \/ /\ RefreshOrder = "release"
              /\ memory' = WM!ReleaseStore(Reader, "localCurrentEpoch", e)
              /\ view' = WM!ReleaseStoreView(Reader, "localCurrentEpoch", e)
              /\ storeBuffer' = WM!Drained(Reader)
           \/ /\ RefreshOrder = "acqload"
              /\ storeBuffer' = WM!Buffer(Reader, "localCurrentEpoch", e)
              /\ view' = WM!AcquireLoadView(Reader, "currentEpoch")
              /\ UNCHANGED memory
           \* Strictly weaker than "acqload": transfers only the two fields the
           \* message-passing argument actually claims, so a HOLDS here cannot
           \* be an artifact of AcquireLoadView refreshing the entire view.
           \/ /\ RefreshOrder = "acqloadmp"
              /\ storeBuffer' = WM!Buffer(Reader, "localCurrentEpoch", e)
              /\ view' = WM!AcquireLoadFields(Reader, "currentEpoch",
                                              {"currentEpoch", "objectUnlinked"})
              /\ UNCHANGED memory
           \* Near-miss control: refreshes ONLY the field being loaded, i.e. a
           \* plain load that transfers nothing. This MUST still be VIOLATED;
           \* if it is not, the check has gone dead and no HOLDS above counts.
           \/ /\ RefreshOrder = "acqloadself"
              /\ storeBuffer' = WM!Buffer(Reader, "localCurrentEpoch", e)
              /\ view' = WM!AcquireLoadFields(Reader, "currentEpoch", {"currentEpoch"})
              /\ UNCHANGED memory
           \/ /\ RefreshOrder = "fence"
              /\ memory' = WM!FencedStore(Reader, "localCurrentEpoch", e)
              /\ view' = WM!FencedStoreView(Reader, "localCurrentEpoch", e)
              /\ storeBuffer' = WM!Drained(Reader)
    /\ readerPc' = "ReadObject"
    /\ UNCHANGED <<readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, reclaimerPc>>

\* The operation's load. A reader that observes objectUnlinked = FALSE believes
\* the object is reachable and enters the critical section.
ReadObject ==
    /\ readerPc = "ReadObject"
    /\ readerInCriticalSection' = (~ Load(Reader, "objectUnlinked"))
    /\ readerPc' = "Dereference"
    /\ UNCHANGED <<memory, storeBuffer, view, readerAnnouncedEpoch, triggerEpoch, reclaimerPc>>

Dereference ==
    /\ readerPc = "Dereference"
    /\ readerInCriticalSection' = FALSE
    /\ readerPc' = "Release"
    /\ UNCHANGED <<memory, storeBuffer, view, readerAnnouncedEpoch, triggerEpoch, reclaimerPc>>

\* Suspend() -> Release. In the CAS design localCurrentEpoch is the slot-
\* ownership word, so clearing it is a release store.
Release ==
    /\ readerPc = "Release"
    /\ memory' = WM!ReleaseStore(Reader, "localCurrentEpoch", 0)
    /\ view' = WM!ReleaseStoreView(Reader, "localCurrentEpoch", 0)
    /\ storeBuffer' = WM!Drained(Reader)
    /\ readerPc' = "Done"
    /\ UNCHANGED <<readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, reclaimerPc>>

\* Reclaimer ------------------------------------------------------------------
Unlink ==
    /\ reclaimerPc = "Unlink"
    /\ storeBuffer' = WM!Buffer(Reclaimer, "objectUnlinked", TRUE)
    /\ reclaimerPc' = "BumpCurrentEpoch"
    /\ UNCHANGED <<memory, view, readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, readerPc>>

\* Interlocked.Increment(CurrentEpoch): a locked RMW, so it drains the
\* reclaimer's buffer and refreshes the reclaimer's own view.
BumpCurrentEpoch ==
    /\ reclaimerPc = "BumpCurrentEpoch"
    /\ LET fencedMemory == WM!Fenced(Reclaimer)
           bumped == [fencedMemory EXCEPT !.currentEpoch = fencedMemory.currentEpoch + 1]
       IN /\ memory' = bumped
          /\ view' = [q \in Threads |-> IF q = Reclaimer \/ Model # "armlb" THEN bumped ELSE view[q]]
          /\ triggerEpoch' = fencedMemory.currentEpoch
    /\ storeBuffer' = WM!Drained(Reclaimer)
    /\ reclaimerPc' = "ComputeNewSafeToReclaimEpoch"
    /\ UNCHANGED <<readerInCriticalSection, readerAnnouncedEpoch, readerPc>>

\* ComputeNewSafeToReclaimEpoch: scan the table, take the minimum announced
\* epoch, and free anything strictly older.
ComputeNewSafeToReclaimEpoch ==
    /\ reclaimerPc = "ComputeNewSafeToReclaimEpoch"
    /\ LET readerSlot         == Load(Reclaimer, "localCurrentEpoch")
           curEpoch           == Load(Reclaimer, "currentEpoch")
           oldestOngoingCall  == IF readerSlot > 0 THEN Min(curEpoch, readerSlot) ELSE curEpoch
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN /\ memory' = [memory EXCEPT !.objectFreed = TRUE]
               /\ view' = [q \in Threads |->
                             IF q = Reclaimer \/ Model # "armlb"
                             THEN [view[q] EXCEPT !.objectFreed = TRUE]
                             ELSE view[q]]
               /\ reclaimerPc' = "Done"
          ELSE /\ UNCHANGED <<memory, view, reclaimerPc>>
    /\ UNCHANGED <<storeBuffer, readerInCriticalSection, readerAnnouncedEpoch, triggerEpoch, readerPc>>

Next == \/ Acquire \/ ProtectAndDrain \/ ReadObject \/ Dereference \/ Release
        \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
        \/ (\E p \in Threads : PropagateOne(p))
        \/ (\E p \in Threads : ObserveField(p))

Spec == Init /\ [][Next]_vars

\* The object was freed while the reader was inside the critical section
\* holding a pointer to it.
NoUseAfterFree == ~ (memory.objectFreed /\ readerInCriticalSection)
=============================================================================
