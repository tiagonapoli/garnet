----------------------- MODULE CasAnnounceProtectedQuery -----------------------
(***************************************************************************)
(* What removing the CAS from Entry.threadId does to the two public APIs    *)
(* that read it: TrySuspend() and ResumeIfNotProtected().                   *)
(*                                                                         *)
(* Upstream, threadId IS the claim word:                                   *)
(*                                                                         *)
(*     CAS(slot.threadId, 0 -> myThreadId)      claims the slot            *)
(*     slot.localCurrentEpoch = e               announces                  *)
(*     ...                                                                 *)
(*     slot.localCurrentEpoch = 0               unannounces                *)
(*     slot.threadId = 0                        frees the slot   <-- LAST  *)
(*                                                                         *)
(* so `slot.threadId == Metadata.threadId` is an exact test of ownership:   *)
(* no other thread can write that word while this thread holds the slot,    *)
(* because the only way in is a CAS that this thread's own id blocks.       *)
(*                                                                         *)
(* The fix moves the claim onto the epoch word:                            *)
(*                                                                         *)
(*     CAS(slot.localCurrentEpoch, 0 -> e)      claims AND announces       *)
(*     slot.threadId = myThreadId               plain store, ownership tag  *)
(*     ...                                                                 *)
(*     slot.threadId = 0                        plain store     <-- FIRST  *)
(*     slot.localCurrentEpoch = 0               frees the slot             *)
(*                                                                         *)
(* threadId is now DERIVED state: a plain store that trails the claim. It   *)
(* no longer gates entry to the slot, so its correctness is no longer given *)
(* by the CAS -- it has to be argued. That argument is what this module     *)
(* checks, and it is not the argument the other specs make. Their           *)
(* ThreadIdIntact is a predicate on the raw slot word, evaluated only while *)
(* the reader's own store buffer happens to be empty. Here the thread       *)
(* actually PERFORMS the load (with store forwarding, as real hardware      *)
(* does), branches on it, and the invariants are stated over the CONSEQUENCE*)
(* of that branch.                                                         *)
(*                                                                         *)
(*   ThisInstanceProtected()                                               *)
(*       == kInvalidIndex != entry && slot.threadId == Metadata.threadId    *)
(*                                                                         *)
(*   TrySuspend()           if protected { Suspend(); return true }         *)
(*                          return false                                    *)
(*   ResumeIfNotProtected() if protected { return false }                   *)
(*                          Resume(); return true                           *)
(*                                                                         *)
(* A FALSE POSITIVE is structurally impossible and no invariant below looks *)
(* for one: the query conjoins the thread-PRIVATE entry index, which        *)
(* Release() resets, and threadIds of live threads are distinct. Only the   *)
(* false negative -- protected, but reports unprotected -- is reachable,    *)
(* and each API turns it into a different failure:                         *)
(*                                                                         *)
(*   TrySuspend           returns false, so the caller does not suspend and *)
(*                        the slot is never released. Its announce pins     *)
(*                        SafeToReclaimEpoch and reclamation stops for the  *)
(*                        life of the process.                             *)
(*                                                                         *)
(*   ResumeIfNotProtected returns true and calls Resume() -> Acquire()      *)
(*                        while already protected. In Debug that trips the  *)
(*                        `entry == kInvalidIndex` assert; in Release it    *)
(*                        takes a SECOND slot and overwrites                *)
(*                        Metadata.Entries[instanceId], orphaning the first *)
(*                        slot with a non-zero epoch -- the same permanent  *)
(*                        stall, plus a leaked entry.                       *)
(*                                                                         *)
(* AXIS UNDER TEST -- ReleaseOrder. The claim is the fix's CAS in every     *)
(* row, so any violation is attributable to the release side alone.        *)
(*                                                                         *)
(*   "release"   the fix: clear threadId, THEN unpublish the slot with a    *)
(*               release store (Volatile.Write).                           *)
(*                                                                         *)
(*   "plain"     the fix's program order, but the unpublish is a plain      *)
(*               store. Isolates the FENCE.                                *)
(*                                                                         *)
(*   "upstream"  the upstream program order -- unpublish the slot FIRST,    *)
(*               clear threadId after -- kept together with the fix's CAS.  *)
(*               This is what you get by changing the claim word and        *)
(*               leaving Release() alone. The unpublish is a release store  *)
(*               here, so that the fence axis is held fixed and the         *)
(*               violation is attributable to the ORDER alone.             *)
(*                                                                         *)
(* Expected:                                                               *)
(*   release  tso HOLDS      arm HOLDS                                     *)
(*   plain    tso HOLDS      arm VIOLATED                                  *)
(*   upstream tso VIOLATED   arm VIOLATED                                  *)
(*                                                                         *)
(* The upstream row is the one that matters for an x86-only change: it      *)
(* fails under plain TSO, so inverting the two stores in Release() is not a *)
(* weak-memory nicety, it is load-bearing on x86. The plain row is the      *)
(* separate, ARM-only reason for the release store.                        *)
(*                                                                         *)
(* The violating rows are also the liveness control for the holding ones:   *)
(* they use the same invariants, so a HOLDS cannot be an artifact of a      *)
(* query the model can never get wrong.                                    *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model         \* "tso" | "arm"
CONSTANT ReleaseOrder  \* "release" (the fix) | "plain" | "upstream"

R1 == "R1"
R2 == "R2"
Reclaimer == "Reclaimer"
Readers == {R1, R2}
Threads == {R1, R2, Reclaimer}

\* Distinct, non-zero: 0 is reserved to mean "no thread".
ThreadIdOf == [r \in Readers |-> IF r = R1 THEN 11 ELSE 22]

VARIABLES memory, storeBuffer, inCriticalSection, owns, announcedEpoch,
          readerPc, observed, triggerEpoch, reclaimerPc
vars == <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch,
          readerPc, observed, triggerEpoch, reclaimerPc>>

SB == INSTANCE StoreBuffer
Load(p, f) == SB!Load(p, f)
Min(a, b)  == SB!Min(a, b)

\* Both readers contend for ONE slot, so the slot is handed back and forth and
\* every acquire is a reuse -- the only setting in which a departing thread's
\* threadId store can collide with its successor's.
\*
\* `owns[r]` models Metadata.Entries[instanceId] != kInvalidIndex: the
\* thread-private entry index, which is also the ground truth for "r is
\* protected". Release() resets it, so it is never stale (that hazard has its
\* own spec, CasAnnounceNoThreadIdStaleIndex).
Init ==
    /\ memory = [ currentEpoch |-> 1, slotEpoch |-> 0, slotThreadId |-> 0,
                  objectUnlinked |-> FALSE, objectFreed |-> FALSE ]
    /\ storeBuffer = [ p \in Threads |-> <<>> ]
    /\ inCriticalSection = [ r \in Readers |-> FALSE ]
    /\ owns = [ r \in Readers |-> FALSE ]
    /\ announcedEpoch = [ r \in Readers |-> 0 ]
    /\ readerPc = [ r \in Readers |-> "ReadEpoch" ]
    /\ observed = [ r \in Readers |-> FALSE ]
    /\ triggerEpoch = 0
    /\ reclaimerPc = "Unlink"

FlushOne(p) ==
    /\ SB!FlushOne(p)
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, readerPc, observed,
                   triggerEpoch, reclaimerPc>>

\* ThisInstanceProtected(). A PLAIN load of the slot's threadId word, so it is
\* subject to store forwarding: the thread sees its own not-yet-visible publish
\* even while other cores still read the old value. That is what makes the false
\* negative narrow -- it needs this reader's own store to have already drained
\* and the departing reader's stale clear to land afterwards.
ThisInstanceProtected(r) == owns[r] /\ Load(r, "slotThreadId") = ThreadIdOf[r]

\* Where Release() begins depends on the axis under test.
ReleaseEntryPc == IF ReleaseOrder = "upstream" THEN "ReleaseSlotUpstream" ELSE "ClearThreadId"

(***************************************************************************)
(* Reader                                                                  *)
(***************************************************************************)

ReadEpoch(r) ==
    /\ readerPc[r] = "ReadEpoch"
    /\ announcedEpoch' = [announcedEpoch EXCEPT ![r] = Load(r, "currentEpoch")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Claim"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, observed,
                   triggerEpoch, reclaimerPc>>

\* Interlocked.CompareExchange(ref slot.localCurrentEpoch, e, 0) -- the fix, in
\* every row. A locked RMW: drains this core's buffer and acts on the globally
\* visible value.
Claim(r) ==
    /\ readerPc[r] = "Claim"
    /\ LET m == SB!Fenced(r)
       IN IF m.slotEpoch = 0
          THEN /\ memory' = [m EXCEPT !.slotEpoch = announcedEpoch[r]]
               /\ owns' = [owns EXCEPT ![r] = TRUE]
               /\ readerPc' = [readerPc EXCEPT ![r] = "PublishThreadId"]
          ELSE /\ memory' = m
               /\ owns' = owns
               /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ storeBuffer' = SB!Drained(r)
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, observed, triggerEpoch, reclaimerPc>>

\* Plain store: the slot is already ours, so no interlocked write is needed to
\* keep others out. This is the store whose visibility the query depends on.
PublishThreadId(r) ==
    /\ readerPc[r] = "PublishThreadId"
    /\ storeBuffer' = SB!Buffer(r, "slotThreadId", ThreadIdOf[r])
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadObject"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, observed,
                   triggerEpoch, reclaimerPc>>

ReadObject(r) ==
    /\ readerPc[r] = "ReadObject"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = ~ Load(r, "objectUnlinked")]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Dereference"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, observed,
                   triggerEpoch, reclaimerPc>>

Dereference(r) ==
    /\ readerPc[r] = "Dereference"
    /\ inCriticalSection' = [inCriticalSection EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Query"]
    /\ UNCHANGED <<memory, storeBuffer, owns, announcedEpoch, observed,
                   triggerEpoch, reclaimerPc>>

\* The reader, still protected, calls ThisInstanceProtected() and remembers what
\* it got. Splitting the query from the branch is what lets the two APIs share
\* one observation and keeps the failure attributable to the load.
Query(r) ==
    /\ readerPc[r] = "Query"
    /\ observed' = [observed EXCEPT ![r] = ThisInstanceProtected(r)]
    /\ readerPc' = [readerPc EXCEPT ![r] = "Api"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch,
                   triggerEpoch, reclaimerPc>>

\* TrySuspend(): protected -> Suspend() and return true; otherwise return false
\* WITHOUT suspending. The caller asked to be suspended, so a false return while
\* protected is not a benign "nothing to do" -- the thread stays in the table.
\* "SuspendLost" is absorbing and keeps owns[r], which is exactly the production
\* consequence: the announce pins SafeToReclaimEpoch forever.
ApiTrySuspend(r) ==
    /\ readerPc[r] = "Api"
    /\ readerPc' = [readerPc EXCEPT ![r] = IF observed[r] THEN ReleaseEntryPc ELSE "SuspendLost"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch,
                   observed, triggerEpoch, reclaimerPc>>

\* ResumeIfNotProtected(): protected -> return false and do nothing, which is
\* correct and lets the reader go on to finish normally. Not protected ->
\* Resume() -> Acquire(), which here is reached while the thread DOES hold a
\* slot: a second acquire that orphans the first entry.
ApiResumeIfNotProtected(r) ==
    /\ readerPc[r] = "Api"
    /\ readerPc' = [readerPc EXCEPT ![r] = IF observed[r] THEN ReleaseEntryPc ELSE "DoubleAcquire"]
    /\ UNCHANGED <<memory, storeBuffer, inCriticalSection, owns, announcedEpoch,
                   observed, triggerEpoch, reclaimerPc>>

(***************************************************************************)
(* Release(), in the two orders                                            *)
(***************************************************************************)

\* The fix: clear the tag BEFORE the slot is published as free.
ClearThreadId(r) ==
    /\ readerPc[r] = "ClearThreadId"
    /\ storeBuffer' = SB!Buffer(r, "slotThreadId", 0)
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReleaseSlot"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, observed,
                   triggerEpoch, reclaimerPc>>

\* Unpublish. "release" drains everything this thread has pending, so the
\* ClearThreadId above cannot be observed after the slot is seen free. "plain"
\* leaves it in the buffer, where a StoreStore-relaxing model may let the
\* unpublish overtake it.
ReleaseSlot(r) ==
    /\ readerPc[r] = "ReleaseSlot"
    /\ \/ /\ ReleaseOrder = "release"
          /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
          /\ storeBuffer' = SB!Drained(r)
       \/ /\ ReleaseOrder = "plain"
          /\ storeBuffer' = SB!Buffer(r, "slotEpoch", 0)
          /\ UNCHANGED memory
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, observed, triggerEpoch, reclaimerPc>>

\* Upstream order. A release store, so no reordering is needed for the hazard:
\* the threadId clear is ISSUED after the slot is already free, and the next
\* owner may claim and publish in between. `owns` drops here because this is the
\* point the thread relinquishes; the trailing clear is a store from a thread
\* that no longer holds the slot, which is the entire shape of the bug.
ReleaseSlotUpstream(r) ==
    /\ readerPc[r] = "ReleaseSlotUpstream"
    /\ memory' = SB!FencedStore(r, "slotEpoch", 0)
    /\ storeBuffer' = SB!Drained(r)
    /\ owns' = [owns EXCEPT ![r] = FALSE]
    /\ readerPc' = [readerPc EXCEPT ![r] = "ClearThreadIdLate"]
    /\ UNCHANGED <<inCriticalSection, announcedEpoch, observed, triggerEpoch, reclaimerPc>>

ClearThreadIdLate(r) ==
    /\ readerPc[r] = "ClearThreadIdLate"
    /\ storeBuffer' = SB!Buffer(r, "slotThreadId", 0)
    /\ readerPc' = [readerPc EXCEPT ![r] = "ReadEpoch"]
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, observed,
                   triggerEpoch, reclaimerPc>>

(***************************************************************************)
(* Reclaimer -- unchanged from production                                  *)
(***************************************************************************)

Unlink ==
    /\ reclaimerPc = "Unlink"
    /\ storeBuffer' = SB!Buffer(Reclaimer, "objectUnlinked", TRUE)
    /\ reclaimerPc' = "BumpCurrentEpoch"
    /\ UNCHANGED <<memory, inCriticalSection, owns, announcedEpoch, readerPc,
                   observed, triggerEpoch>>

BumpCurrentEpoch ==
    /\ reclaimerPc = "BumpCurrentEpoch"
    /\ LET fencedMemory == SB!Fenced(Reclaimer)
       IN /\ memory' = [fencedMemory EXCEPT !.currentEpoch = fencedMemory.currentEpoch + 1]
          /\ triggerEpoch' = fencedMemory.currentEpoch
    /\ storeBuffer' = SB!Drained(Reclaimer)
    /\ reclaimerPc' = "ComputeNewSafeToReclaimEpoch"
    /\ UNCHANGED <<inCriticalSection, owns, announcedEpoch, readerPc, observed>>

\* Reads ONLY the epoch word, as production does. It never consulted threadId,
\* which is why the scan itself is indifferent to everything this spec varies --
\* the damage lands on the two APIs, not on the reclaimer's arithmetic.
ComputeNewSafeToReclaimEpoch ==
    /\ reclaimerPc = "ComputeNewSafeToReclaimEpoch"
    /\ LET slot               == memory.slotEpoch
           oldestOngoingCall  == IF slot > 0 THEN Min(memory.currentEpoch, slot) ELSE memory.currentEpoch
           safeToReclaimEpoch == oldestOngoingCall - 1
       IN IF triggerEpoch <= safeToReclaimEpoch
          THEN /\ memory' = [memory EXCEPT !.objectFreed = TRUE]
               /\ reclaimerPc' = "Done"
          ELSE /\ UNCHANGED <<memory, reclaimerPc>>
    /\ UNCHANGED <<storeBuffer, inCriticalSection, owns, announcedEpoch, readerPc,
                   observed, triggerEpoch>>

Next ==
    \/ \E r \in Readers :
         \/ ReadEpoch(r) \/ Claim(r) \/ PublishThreadId(r) \/ ReadObject(r)
         \/ Dereference(r) \/ Query(r)
         \/ ApiTrySuspend(r) \/ ApiResumeIfNotProtected(r)
         \/ ClearThreadId(r) \/ ReleaseSlot(r)
         \/ ReleaseSlotUpstream(r) \/ ClearThreadIdLate(r)
    \/ Unlink \/ BumpCurrentEpoch \/ ComputeNewSafeToReclaimEpoch
    \/ (\E p \in Threads : FlushOne(p))

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* Invariants                                                              *)
(***************************************************************************)

NoUseAfterFree == ~ (memory.objectFreed /\ (\E r \in Readers : inCriticalSection[r]))

SlotExclusive == ~ (owns[R1] /\ owns[R2])

\* The query itself. A reader sitting at "Api" has claimed and not released, so
\* it is protected by construction; ThisInstanceProtected() must agree.
ProtectedQueryAccurate ==
    \A r \in Readers : (readerPc[r] = "Api" /\ owns[r]) => observed[r]

\* The two API-level manifestations of a false negative. They are downstream of
\* ProtectedQueryAccurate rather than independent of it, and that is the point:
\* they name what the wrong answer costs each caller.
NoLostSuspend   == \A r \in Readers : readerPc[r] # "SuspendLost"
NoDoubleAcquire == \A r \in Readers : readerPc[r] # "DoubleAcquire"
=============================================================================
