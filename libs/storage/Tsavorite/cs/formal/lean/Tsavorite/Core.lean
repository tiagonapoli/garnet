import Std

namespace Tsavorite.Core

abbrev Store (Key Value : Type) := Key → Option Value

inductive Command (Key Value : Type) where
  | read (key : Key)
  | upsert (key : Key) (value : Value)
  | delete (key : Key)
  | rmw (key : Key) (update : Option Value → Value)

variable {Key Value Client : Type} [DecidableEq Key] [DecidableEq Client]

def put (store : Store Key Value) (key : Key) (value : Option Value) : Store Key Value :=
  fun other => if other = key then value else store other

def execute (command : Command Key Value) (store : Store Key Value) : Store Key Value :=
  match command with
  | .read _ => store
  | .upsert key value => put store key (some value)
  | .delete key => put store key none
  | .rmw key update => put store key (some (update (store key)))

def replay (initial : Store Key Value) (history : List (Command Key Value)) : Store Key Value :=
  history.foldl (fun store command => execute command store) initial

theorem replay_append (initial : Store Key Value) (history : List (Command Key Value)) (command : Command Key Value) :
    replay initial (history ++ [command]) = execute command (replay initial history) := by
  simp [replay, List.foldl_append]

structure Proposal (Key Value : Type) where
  command : Command Key Value
  base : Store Key Value
  generation : Nat

structure State (Key Value Client : Type) where
  data : Store Key Value
  owner : Option Client
  generation : Nat
  proposals : Client → Option (Proposal Key Value)
  history : List (Command Key Value)

def initial (store : Store Key Value) : State Key Value Client :=
  ⟨store, none, 0, fun _ => none, []⟩

inductive Step : State Key Value Client → State Key Value Client → Prop where
  | acquire (s : State Key Value Client) (client : Client) (free : s.owner = none) :
      Step s { s with owner := some client }
  | prepare (s : State Key Value Client) (client : Client) (command : Command Key Value) (owns : s.owner = some client) :
      Step s { s with proposals := fun c => if c = client then some ⟨command, s.data, s.generation⟩ else s.proposals c }
  | publish (s : State Key Value Client) (client : Client) (proposal : Proposal Key Value)
      (owns : s.owner = some client) (pending : s.proposals client = some proposal) :
      Step s { s with
        data := execute proposal.command proposal.base
        generation := s.generation + 1
        proposals := fun c => if c = client then none else s.proposals c
        history := s.history ++ [proposal.command] }
  | discard (s : State Key Value Client) (client : Client) :
      Step s { s with proposals := fun c => if c = client then none else s.proposals c }
  | release (s : State Key Value Client) (client : Client) (owns : s.owner = some client) :
      Step s { s with owner := none, proposals := fun _ => none }

def Invariant (store : Store Key Value) (s : State Key Value Client) : Prop :=
  s.data = replay store s.history ∧
  ∀ client proposal, s.proposals client = some proposal →
    s.owner = some client ∧ proposal.generation = s.generation ∧ proposal.base = s.data

omit [DecidableEq Client] in
theorem initial_invariant (store : Store Key Value) : Invariant store (initial store : State Key Value Client) := by
  simp [Invariant, initial, replay]

theorem step_preserves (store : Store Key Value) {s next : State Key Value Client}
    (inv : Invariant store s) (step : Step s next) : Invariant store next := by
  rcases inv with ⟨data, proposals⟩
  cases step with
  | acquire client free =>
    refine ⟨data, ?_⟩
    intro c p pending
    have owned := (proposals c p pending).1
    rw [free] at owned
    cases owned
  | release client owns =>
    exact ⟨data, by intro c p pending; cases pending⟩
  | prepare client command owns =>
    refine ⟨data, ?_⟩
    intro c p pending
    by_cases same : c = client
    · subst c
      simp at pending
      subst p
      exact ⟨owns, rfl, rfl⟩
    · simp [same] at pending
      exact proposals c p pending
  | discard client =>
    refine ⟨data, ?_⟩
    intro c p pending
    by_cases same : c = client
    · simp [same] at pending
    · simp [same] at pending
      exact proposals c p pending
  | publish client proposal owns pending =>
    have base := (proposals client proposal pending).2.2
    constructor
    · simp only [replay_append]
      rw [base, data]
    · intro c p hp
      by_cases same : c = client
      · simp [same] at hp
      · simp [same] at hp
        have owned := (proposals c p hp).1
        rw [owns] at owned
        exact False.elim (same (Option.some.inj owned).symm)

inductive Reachable (store : Store Key Value) : State Key Value Client → Prop where
  | init : Reachable store (initial store)
  | next {s next : State Key Value Client} : Reachable store s → Step s next → Reachable store next

theorem reachable_invariant (store : Store Key Value) {s : State Key Value Client} (reachable : Reachable store s) :
    Invariant store s := by
  induction reachable with
  | init => exact initial_invariant store
  | next _ step ih => exact step_preserves store ih step

theorem reachable_refines (store : Store Key Value) {s : State Key Value Client} (reachable : Reachable store s) :
    s.data = replay store s.history :=
  (reachable_invariant store reachable).1

theorem read_agrees (store : Store Key Value) {s : State Key Value Client} (reachable : Reachable store s) (key : Key) :
    s.data key = replay store s.history key := by
  rw [reachable_refines store reachable]

end Tsavorite.Core
