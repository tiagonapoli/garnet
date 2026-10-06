------------------------------- MODULE StoreBuffer -------------------------------
(***************************************************************************)
(* Shared per-core store-buffer harness for the LightEpoch epoch specs.     *)
(*                                                                         *)
(* Every epoch spec models the same hardware substrate: each processor      *)
(* buffers its plain stores privately, and those stores become visible to   *)
(* other processors asynchronously. This module factors that substrate out  *)
(* so the epoch specs contain only the algorithm.                          *)
(*                                                                         *)
(* Instantiate it with implicit substitution — an epoch spec that declares  *)
(* VARIABLES memory, storeBuffer and CONSTANT Model simply writes:                     *)
(*                                                                         *)
(*     SB == INSTANCE StoreBuffer                                          *)
(*                                                                         *)
(* and then uses SB!Load, SB!Buffer, SB!Fenced, SB!Drained, SB!FlushOne.    *)
(* SB!FlushOne constrains only memory' and storeBuffer', so the caller conjoins its own *)
(* UNCHANGED <<...>> for the remaining variables.                          *)
(*                                                                         *)
(* STATE SHAPE                                                             *)
(*   memory       a record of shared fields, e.g.                          *)
(*                  [ currentEpoch      |-> 1,                             *)
(*                    localCurrentEpoch |-> 0,                             *)
(*                    objectUnlinked    |-> FALSE,                         *)
(*                    objectFreed       |-> FALSE ]                        *)
(*   storeBuffer  storeBuffer[p] is processor p's private FIFO of pending  *)
(*                stores, each a record                                    *)
(*                  [f |-> <field of memory>, v |-> <value>]               *)
(*                                                                         *)
(* MEMORY MODEL — CONSTANT Model                                           *)
(*                                                                         *)
(*   "tso"  x86-TSO. The buffer drains in FIFO order: only the OLDEST      *)
(*          pending store may become visible next, so two stores by one    *)
(*          processor are observed in program order. StoreStore, LoadLoad, *)
(*          and LoadStore order are preserved; only StoreLoad is relaxed.  *)
(*                                                                         *)
(*   "arm"  A store-order relaxation of TSO. ANY pending store may become  *)
(*          visible next, so a processor's stores can be observed out of   *)
(*          program order (StoreStore reordering), which TSO forbids.      *)
(*                                                                         *)
(* Because i = 1 is always among the choices, every "tso" behavior is also  *)
(* an "arm" behavior. So an invariant VIOLATED under "tso" is necessarily   *)
(* violated under "arm", and an invariant that HOLDS under "arm" also holds *)
(* under "tso". Checking both is therefore free of contradiction, and the   *)
(* "tso" results are the conservative ones for a bug claim.                *)
(*                                                                         *)
(* SCOPE OF "arm" — this is deliberately NOT a model of the ARM            *)
(* architecture. It relaxes store visibility order and nothing else. It     *)
(* omits load-load reordering, address/data/control dependencies, acquire   *)
(* loads and release stores, exclusive/atomic RMW semantics, shareability   *)
(* domains, and multi-copy atomicity (there is a single shared `memory`, so    *)
(* every processor observes a propagated store simultaneously). A HOLDS     *)
(* result under "arm" therefore means "robust against StoreStore as well as *)
(* StoreLoad reordering" — it is NOT a proof of correctness on AArch64.     *)
(* The ARM64 evidence in this repo is the hardware repro, not this model.   *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANT Model          \* "tso" | "arm"

VARIABLES memory, storeBuffer

Max(S) == CHOOSE x \in S : \A y \in S : y <= x
Min(a, b) == IF a < b THEN a ELSE b

(***************************************************************************)
(* Loads, with store forwarding: a processor sees its OWN pending stores    *)
(* even while other processors still observe the stale value in memory.     *)
(* This is what makes the announce bug subtle — the announcing thread can   *)
(* read back the epoch it just announced and conclude it is protected.      *)
(***************************************************************************)
LoadFrom(m, s, f) ==
    LET idxs == { i \in DOMAIN s : s[i].f = f }
    IN  IF idxs = {} THEN m[f] ELSE s[Max(idxs)].v

Load(p, f) == LoadFrom(memory, storeBuffer[p], f)

RECURSIVE ApplyAll(_, _)
ApplyAll(m, s) == IF s = <<>> THEN m
                  ELSE ApplyAll([m EXCEPT ![Head(s).f] = Head(s).v], Tail(s))

\* A plain store by p: appended to p's private buffer, invisible to others.
Buffer(p, f, v) == [storeBuffer EXCEPT ![p] = Append(storeBuffer[p], [f |-> f, v |-> v])]

(***************************************************************************)
(* A full StoreLoad barrier on p (MFENCE, a locked RMW, or a process-wide   *)
(* asymmetric barrier targeting p): everything p has pending becomes        *)
(* globally visible at once. Use the pair together:                        *)
(*     memory' = SB!Fenced(p)   /\   storeBuffer' = SB!Drained(p)                      *)
(***************************************************************************)
Fenced(p)  == ApplyAll(memory, storeBuffer[p])
Drained(p) == [storeBuffer EXCEPT ![p] = <<>>]

(***************************************************************************)
(* A store by p immediately followed by a full StoreLoad barrier —          *)
(* equivalently, a sequentially consistent atomic store or RMW. The new     *)
(* store and everything already pending become globally visible before p's  *)
(* next load. Use with Drained:                                            *)
(*     memory' = SB!FencedStore(p, "localCurrentEpoch", e)   /\   storeBuffer' = SB!Drained(p)       *)
(***************************************************************************)
FencedStore(p, f, v) == ApplyAll(memory, Append(storeBuffer[p], [f |-> f, v |-> v]))

(***************************************************************************)
(* Asynchronous propagation: one pending store becomes globally visible.    *)
(* Constrains memory' and storeBuffer' only — conjoin UNCHANGED for the rest.          *)
(***************************************************************************)
FlushOne(p) ==
    /\ storeBuffer[p] # <<>>
    /\ IF Model = "tso"
       THEN /\ memory' = [memory EXCEPT ![Head(storeBuffer[p]).f] = Head(storeBuffer[p]).v]
            /\ storeBuffer'  = [storeBuffer EXCEPT ![p] = Tail(storeBuffer[p])]
       ELSE \E i \in DOMAIN storeBuffer[p] :
              /\ memory' = [memory EXCEPT ![storeBuffer[p][i].f] = storeBuffer[p][i].v]
              /\ storeBuffer'  = [storeBuffer EXCEPT ![p] =
                           SubSeq(storeBuffer[p], 1, i-1) \o SubSeq(storeBuffer[p], i+1, Len(storeBuffer[p]))]
=============================================================================
