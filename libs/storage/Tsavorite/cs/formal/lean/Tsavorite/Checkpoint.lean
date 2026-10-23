import Std

namespace Tsavorite.Checkpoint

structure State (Snapshot : Type) where
  pending : Option Snapshot
  durable : List Snapshot
  committed : Option Snapshot
  recovered : Option Snapshot

def initial {Snapshot : Type} : State Snapshot := ⟨none, [], none, none⟩

inductive Step {Snapshot : Type} : State Snapshot → State Snapshot → Prop where
  | begin (s : State Snapshot) (snapshot : Snapshot) : Step s { s with pending := some snapshot }
  | flush (s : State Snapshot) (snapshot : Snapshot) (pending : s.pending = some snapshot) :
      Step s { s with durable := snapshot :: s.durable }
  | commit (s : State Snapshot) (snapshot : Snapshot) (pending : s.pending = some snapshot) (flushed : snapshot ∈ s.durable) :
      Step s { s with committed := some snapshot }
  | crash (s : State Snapshot) : Step s { s with pending := none, recovered := none }
  | recover (s : State Snapshot) : Step s { s with recovered := s.committed }

def Invariant {Snapshot : Type} (s : State Snapshot) : Prop :=
  (∀ snapshot, s.committed = some snapshot → snapshot ∈ s.durable) ∧
  (∀ snapshot, s.recovered = some snapshot → snapshot ∈ s.durable)

theorem initial_safe {Snapshot : Type} : Invariant (initial : State Snapshot) := by
  simp [Invariant, initial]

theorem step_preserves {Snapshot : Type} {s next : State Snapshot} (inv : Invariant s) (step : Step s next) : Invariant next := by
  rcases inv with ⟨committed, recovered⟩
  cases step with
  | begin snapshot => exact ⟨committed, recovered⟩
  | flush snapshot pending =>
    exact ⟨fun value h => List.mem_cons_of_mem _ (committed value h),
      fun value h => List.mem_cons_of_mem _ (recovered value h)⟩
  | commit snapshot pending flushed =>
    refine ⟨?_, recovered⟩
    intro value h
    cases Option.some.inj h
    exact flushed
  | crash =>
    exact ⟨committed, by intro value h; cases h⟩
  | recover => exact ⟨committed, committed⟩

inductive Reachable {Snapshot : Type} : State Snapshot → Prop where
  | init : Reachable initial
  | next {s next : State Snapshot} : Reachable s → Step s next → Reachable next

theorem reachable_safe {Snapshot : Type} {s : State Snapshot} (reachable : Reachable s) : Invariant s := by
  induction reachable with
  | init => exact initial_safe
  | next _ step ih => exact step_preserves ih step

theorem recovered_is_durable {Snapshot : Type} {s : State Snapshot} (reachable : Reachable s) (snapshot : Snapshot)
    (recovered : s.recovered = some snapshot) : snapshot ∈ s.durable :=
  (reachable_safe reachable).2 snapshot recovered

end Tsavorite.Checkpoint
