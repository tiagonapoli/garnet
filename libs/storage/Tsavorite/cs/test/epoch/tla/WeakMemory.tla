------------------------------- MODULE WeakMemory -------------------------------
(***************************************************************************)
(* Weak-memory substrate with PER-PROCESSOR VIEWS.                         *)
(*                                                                         *)
(* MODULE StoreBuffer relaxes store visibility only: there is a single      *)
(* shared `memory`, so once a store propagates EVERY processor observes it  *)
(* at once, and a load always returns the newest propagated value. That     *)
(* substrate is multi-copy atomic and has no load-load reordering, and it   *)
(* is therefore unable to express the behaviour the Neoverse-N2 hardware    *)
(* repro exhibits. This module supplies the missing dimension.              *)
(*                                                                         *)
(* WHY IT IS NEEDED — the hardware failure that StoreBuffer cannot model.   *)
(*                                                                         *)
(* In the Resume/Refresh shape the reclaimer unlinks the object and then    *)
(* bumps the epoch with a locked RMW, so the unlink is ordered before the   *)
(* new epoch value on the WRITER side. Under StoreBuffer that ordering is   *)
(* also what every reader observes, so a reader that sees the bumped epoch  *)
(* necessarily sees the unlink, and the invariant holds.                    *)
(*                                                                         *)
(* AArch64 gives no such guarantee to the READER. A barrier on the writer   *)
(* orders the writer's accesses; it does not stop an unfenced reader from   *)
(* satisfying its load of `objectUnlinked` early and its load of            *)
(* `currentEpoch` late. The reader can therefore hold a stale               *)
(* objectUnlinked = FALSE while announcing the NEW epoch, which raises      *)
(* SafeToReclaimEpoch and authorises the free of the very object it is      *)
(* about to dereference.                                                    *)
(*                                                                         *)
(* This module makes that expressible by giving each processor its own view *)
(* that catches up per FIELD, independently and in any order.               *)
(*                                                                         *)
(* STATE SHAPE                                                             *)
(*   memory       the coherent global state: the value a store takes once   *)
(*                it has left its writer's buffer.                          *)
(*   storeBuffer  storeBuffer[p] is p's private FIFO of pending stores,     *)
(*                each [f |-> field, v |-> value].                          *)
(*   view         view[p] is what p has actually OBSERVED. view[p][f] may   *)
(*                lag memory[f] until p catches up on that field.           *)
(*                                                                         *)
(* The lag is per field, so p may observe a later store to one field and    *)
(* still hold an earlier value of another. That is the observable content   *)
(* of load-load reordering and of non-multi-copy atomicity, and it is the   *)
(* only behaviour this module adds over StoreBuffer.                        *)
(*                                                                         *)
(* MEMORY MODEL — CONSTANT Model                                           *)
(*   "tso"   FIFO drain (only the oldest pending store may propagate) and   *)
(*           views never lag: Observe is disabled, so this reduces exactly  *)
(*           to MODULE StoreBuffer's "tso".                                *)
(*   "arm"   any pending store may propagate, and views never lag —        *)
(*           reduces exactly to MODULE StoreBuffer's "arm". Kept so a spec  *)
(*           ported to this module still reproduces its old results.       *)
(*   "armlb" any pending store may propagate AND views lag per field.      *)
(*           This is the mode that models reader-side reordering.          *)
(*                                                                         *)
(* Because "armlb" permits every "arm" behaviour and more, an invariant     *)
(* VIOLATED under "arm" is violated under "armlb", and one that HOLDS under *)
(* "armlb" holds under "arm".                                              *)
(*                                                                         *)
(* SCOPE — still NOT the ARM architecture. There are no dependencies, no    *)
(* speculation, no exclusives, no shareability domains, and coherence is    *)
(* only approximated (a view catches up to `memory`, so a processor cannot  *)
(* observe a value older than one it already observed for that field, but   *)
(* nothing forbids two processors disagreeing about the order of stores to  *)
(* DIFFERENT fields). A HOLDS under "armlb" means "robust against store     *)
(* reordering and against per-field observation skew" — it is evidence,     *)
(* not a proof, for AArch64. The proof obligation remains the hardware      *)
(* repro.                                                                   *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model          \* "tso" | "arm" | "armlb"

VARIABLES memory, storeBuffer, view

Max(S) == CHOOSE x \in S : \A y \in S : y <= x
Min(a, b) == IF a < b THEN a ELSE b

ViewsMayLag == Model = "armlb"
StoresDrainInOrder == Model = "tso"

(***************************************************************************)
(* Loads, with store forwarding: a processor sees its OWN pending stores    *)
(* even while other processors still observe the stale value. Absent a      *)
(* pending store, p reads its own view, which may lag `memory`.             *)
(***************************************************************************)
LoadFrom(vw, s, f) ==
    LET idxs == { i \in DOMAIN s : s[i].f = f }
    IN  IF idxs = {} THEN vw[f] ELSE s[Max(idxs)].v

Load(p, f) == LoadFrom(view[p], storeBuffer[p], f)

RECURSIVE ApplyAll(_, _)
ApplyAll(m, s) == IF s = <<>> THEN m
                  ELSE ApplyAll([m EXCEPT ![Head(s).f] = Head(s).v], Tail(s))

\* A plain store by p: appended to p's private buffer, invisible to others.
Buffer(p, f, v) == [storeBuffer EXCEPT ![p] = Append(storeBuffer[p], [f |-> f, v |-> v])]

(***************************************************************************)
(* Store propagation. The store leaves p's buffer and lands in `memory`.    *)
(* p's OWN view is updated at the same time — a processor never loses sight *)
(* of a store it has already made. Other processors pick it up via Observe. *)
(***************************************************************************)
PropagateOne(p) ==
    /\ storeBuffer[p] # <<>>
    /\ \E i \in DOMAIN storeBuffer[p] :
         /\ StoresDrainInOrder => i = 1
         /\ LET st == storeBuffer[p][i]
            IN  /\ memory' = [memory EXCEPT ![st.f] = st.v]
                /\ view' = IF ViewsMayLag
                           THEN [view EXCEPT ![p][st.f] = st.v]
                           ELSE [q \in DOMAIN view |-> [view[q] EXCEPT ![st.f] = st.v]]
                /\ storeBuffer' = [storeBuffer EXCEPT ![p] =
                        SubSeq(storeBuffer[p], 1, i-1) \o SubSeq(storeBuffer[p], i+1, Len(storeBuffer[p]))]

(***************************************************************************)
(* p catches up on ONE field. Enabled only in "armlb"; in the other modes   *)
(* propagation already updated every view, so no view can lag.              *)
(*                                                                         *)
(* This single action is the whole of load-load reordering: p may refresh   *)
(* the fields it reads in any order relative to its own program steps, so   *)
(* it can hold a stale value of one field while holding a fresh value of    *)
(* another.                                                                 *)
(***************************************************************************)
Observe(p, f) ==
    /\ ViewsMayLag
    /\ view[p][f] # memory[f]
    /\ view' = [view EXCEPT ![p][f] = memory[f]]
    /\ UNCHANGED <<memory, storeBuffer>>

ObserveAny(p) == \E f \in DOMAIN memory : Observe(p, f)

(***************************************************************************)
(* A full barrier on p (DMB ISH, a locked RMW, or MFENCE). Two effects, and *)
(* the SECOND is the one the naive fix relies on and a release store does   *)
(* not provide:                                                            *)
(*   1. everything p has pending becomes globally visible;                  *)
(*   2. p's view catches up on EVERY field, so p's subsequent loads cannot  *)
(*      be satisfied by a value it observed before the barrier.             *)
(* Use the triple together:                                                *)
(*     memory' = WM!Fenced(p) /\ view' = WM!FencedView(p) /\ storeBuffer' = WM!Drained(p)  *)
(***************************************************************************)
Fenced(p)  == ApplyAll(memory, storeBuffer[p])
Drained(p) == [storeBuffer EXCEPT ![p] = <<>>]
FencedView(p) == [q \in DOMAIN view |->
                    IF q = p THEN ApplyAll(memory, storeBuffer[p])
                    ELSE IF ViewsMayLag THEN view[q]
                    ELSE ApplyAll(memory, storeBuffer[p])]

(***************************************************************************)
(* A store by p immediately followed by a full barrier — a sequentially     *)
(* consistent store, or the store half of a locked RMW.                     *)
(***************************************************************************)
FencedStore(p, f, v) == ApplyAll(memory, Append(storeBuffer[p], [f |-> f, v |-> v]))
FencedStoreView(p, f, v) ==
    LET m == ApplyAll(memory, Append(storeBuffer[p], [f |-> f, v |-> v]))
    IN  [q \in DOMAIN view |->
            IF q = p THEN m
            ELSE IF ViewsMayLag THEN view[q]
            ELSE m]

(***************************************************************************)
(* A RELEASE store by p: everything p has pending, plus this store, becomes *)
(* globally visible in order. Crucially it does NOT refresh p's view, so    *)
(* p's later loads may still be satisfied by values observed earlier. This  *)
(* is what distinguishes it from FencedStore, and it is why the hardware    *)
(* A/B sees `release` fail where `fence` survives.                          *)
(***************************************************************************)
ReleaseStore(p, f, v) == ApplyAll(memory, Append(storeBuffer[p], [f |-> f, v |-> v]))
ReleaseStoreView(p, f, v) ==
    LET m == ApplyAll(memory, Append(storeBuffer[p], [f |-> f, v |-> v]))
    IN  [q \in DOMAIN view |->
            IF ViewsMayLag
            THEN IF q = p
                 THEN [g \in DOMAIN view[p] |->
                          IF g \in { st.f : st \in { storeBuffer[p][i] : i \in DOMAIN storeBuffer[p] } } \cup {f}
                          THEN m[g] ELSE view[p][g]]
                 ELSE view[q]
            ELSE m]

(***************************************************************************)
(* An ACQUIRE load by p (LDAR, or Volatile.Read). If the value p reads is   *)
(* the latest propagated one, then everything the writer ordered before     *)
(* releasing that field is also visible to p: p's view catches up. If p     *)
(* reads a stale value it has observed nothing new, so its view is          *)
(* unchanged.                                                              *)
(*                                                                         *)
(* This is the reader half of message passing, and it is exactly what x86   *)
(* provides for free on every load — which is why the plain code is safe    *)
(* on x86 and unsafe on AArch64. Note it orders p's subsequent LOADS; it    *)
(* does NOT drain p's store buffer, so it is strictly weaker (and on ARM    *)
(* strictly cheaper) than the full barrier modelled by Fenced.              *)
(***************************************************************************)
AcquireLoadView(p, f) ==
    IF LoadFrom(view[p], storeBuffer[p], f) = memory[f]
    THEN [view EXCEPT ![p] = memory]
    ELSE view

(***************************************************************************)
(* A deliberately WEAKER acquire load, used to show that the safety of the  *)
(* acquire-load fix does not depend on AcquireLoadView being too generous.  *)
(*                                                                         *)
(* AcquireLoadView above catches the whole view up to memory, which grants  *)
(* the loading processor more than real acquire semantics do: a genuine     *)
(* LDAR only orders the load against writes that happened-before the write  *)
(* it observed, not against every write in the system. AcquireLoadFields    *)
(* restricts the catch-up to an explicit set of fields, so a caller can     *)
(* model exactly the message-passing guarantee and nothing more.            *)
(*                                                                         *)
(* Passing the singleton {f} models a PLAIN load, which refreshes only the  *)
(* field being read and transfers nothing -- useful as a near-miss control. *)
(*                                                                         *)
(* LIMITATION -- RCsc vs RCpc.  This models acquire as "catch up on the     *)
(* named fields", which is the load->load ordering an acquire provides.     *)
(* It therefore does NOT distinguish AArch64's two acquire loads:           *)
(*   LDAR   (RCsc) -- also ordered against an EARLIER store-release,        *)
(*   LDAPR  (RCpc) -- that pairing may be reordered.                        *)
(* Both give load->load ordering, so both look identical here.  This        *)
(* matters because .NET's Volatile.Read on arm64 was observed to emit       *)
(* LDAPR, not LDAR (confirmed by disassembling emitted JIT bytes).  The     *)
(* message-passing hazard modelled here needs only load->load ordering, so  *)
(* the abstraction is sound FOR THIS PROPERTY -- but a HOLDS here is not    *)
(* evidence about any property that turns on the RCsc/RCpc difference.      *)
(* That distinction must be settled with herd7 against the official cat     *)
(* file, which can express both forms; see herd/results.txt.                *)
(***************************************************************************)
AcquireLoadFields(p, f, fields) ==
    IF LoadFrom(view[p], storeBuffer[p], f) = memory[f]
    THEN [view EXCEPT ![p] = [g \in DOMAIN memory |->
                                IF g \in fields THEN memory[g] ELSE view[p][g]]]
    ELSE view

InitView == [q \in DOMAIN view |-> memory]
=============================================================================
