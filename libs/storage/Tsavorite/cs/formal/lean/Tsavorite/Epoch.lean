import Std

namespace Tsavorite.Epoch

variable {Client Resource : Type}

structure State (Client Resource : Type) where
  current : Nat
  announced : Client → Option Nat
  pointer : Client → Option Resource
  retired : Resource → Option Nat
  freed : Resource → Bool

def Protected (s : State Client Resource) (resource : Resource) : Prop :=
  ∃ client epoch, s.announced client = some epoch ∧ s.pointer client = some resource

def AccessDiscipline (s : State Client Resource) : Prop :=
  ∀ client epoch resource retirement,
    s.announced client = some epoch → s.pointer client = some resource →
    s.retired resource = some retirement → epoch ≤ retirement

def Eligible (s : State Client Resource) (resource : Resource) : Prop :=
  ∃ retirement, s.retired resource = some retirement ∧
    ∀ client epoch, s.announced client = some epoch → retirement < epoch

theorem reclamation_safe (s : State Client Resource) (discipline : AccessDiscipline s) (resource : Resource)
    (eligible : Eligible s resource) : ¬ Protected s resource := by
  rcases eligible with ⟨retirement, retired, drained⟩
  rintro ⟨client, epoch, announced, pointer⟩
  have old := discipline client epoch resource retirement announced pointer retired
  have newer := drained client epoch announced
  omega

def refresh (s : State Client Resource) (client : Client) [DecidableEq Client] (epoch : Nat) : State Client Resource :=
  { s with
    announced := fun c => if c = client then some epoch else s.announced c
    pointer := fun c => if c = client then none else s.pointer c }

theorem refresh_preserves_discipline (s : State Client Resource) (discipline : AccessDiscipline s)
    (client : Client) [DecidableEq Client] (epoch : Nat) : AccessDiscipline (refresh s client epoch) := by
  intro c announced resource retirement holder pointer retired
  by_cases same : c = client
  · simp [refresh, same] at pointer
  · simp [refresh, same] at holder pointer retired
    exact discipline c announced resource retirement holder pointer retired

def Invariant (s : State Client Resource) : Prop :=
  (∀ client epoch, s.announced client = some epoch → epoch ≤ s.current) ∧
  (∀ client resource, s.pointer client = some resource → ∃ epoch, s.announced client = some epoch) ∧
  AccessDiscipline s ∧
  (∀ client resource, s.pointer client = some resource → s.freed resource = false) ∧
  (∀ resource, s.freed resource = true → ∃ epoch, s.retired resource = some epoch)

def initial : State Client Resource :=
  ⟨1, fun _ => none, fun _ => none, fun _ => none, fun _ => false⟩

def announce (s : State Client Resource) (client : Client) [DecidableEq Client] (value : Option Nat) : State Client Resource :=
  { s with
    announced := fun c => if c = client then value else s.announced c
    pointer := fun c => if c = client then none else s.pointer c }

theorem announcement_preserves (s : State Client Resource) (inv : Invariant s) (client : Client)
    [DecidableEq Client] (value : Option Nat) (bounded : ∀ epoch, value = some epoch → epoch ≤ s.current) :
    Invariant (announce s client value) := by
  rcases inv with ⟨bounds, tracked, discipline, noFreed, retired⟩
  refine ⟨?_, ?_, ?_, ?_, retired⟩
  · intro c epoch h
    by_cases same : c = client
    · simp [announce, same] at h
      exact bounded epoch h
    · simp [announce, same] at h
      exact bounds c epoch h
  · intro c resource h
    by_cases same : c = client
    · simp [announce, same] at h
    · simp [announce, same] at h
      obtain ⟨epoch, holder⟩ := tracked c resource h
      exact ⟨epoch, by simpa [announce, same] using holder⟩
  · intro c epoch resource retirement holder pointer hr
    by_cases same : c = client
    · simp [announce, same] at pointer
    · simp [announce, same] at holder pointer hr
      exact discipline c epoch resource retirement holder pointer hr
  · intro c resource h
    by_cases same : c = client
    · simp [announce, same] at h
    · simp [announce, same] at h
      exact noFreed c resource h

inductive Step [DecidableEq Client] [DecidableEq Resource] : State Client Resource → State Client Resource → Prop where
  | acquire (s : State Client Resource) (client : Client) (unprotected : s.announced client = none) :
      Step s (announce s client (some s.current))
  | load (s : State Client Resource) (client : Client) (resource : Resource) (epoch : Nat)
      (holder : s.announced client = some epoch) (live : s.retired resource = none) :
      Step s { s with pointer := fun c => if c = client then some resource else s.pointer c }
  | refresh (s : State Client Resource) (client : Client) :
      Step s (announce s client (some s.current))
  | suspend (s : State Client Resource) (client : Client) :
      Step s (announce s client none)
  | retire (s : State Client Resource) (resource : Resource) (live : s.retired resource = none) :
      Step s { s with
        current := s.current + 1
        retired := fun r => if r = resource then some s.current else s.retired r }
  | reclaim (s : State Client Resource) (resource : Resource) (eligible : Eligible s resource) :
      Step s { s with freed := fun r => if r = resource then true else s.freed r }

theorem initial_safe : Invariant (initial : State Client Resource) := by
  simp [Invariant, AccessDiscipline, initial]

theorem step_preserves [DecidableEq Client] [DecidableEq Resource] {s next : State Client Resource}
    (inv : Invariant s) (step : Step s next) : Invariant next := by
  rcases inv with ⟨bounds, tracked, discipline, noFreed, retired⟩
  cases step with
  | acquire client unprotected =>
    exact announcement_preserves s ⟨bounds, tracked, discipline, noFreed, retired⟩ client (some s.current)
      (by intro epoch h; cases Option.some.inj h; exact Nat.le_refl _)
  | refresh client =>
    exact announcement_preserves s ⟨bounds, tracked, discipline, noFreed, retired⟩ client (some s.current)
      (by intro epoch h; cases Option.some.inj h; exact Nat.le_refl _)
  | suspend client =>
    exact announcement_preserves s ⟨bounds, tracked, discipline, noFreed, retired⟩ client none
      (by intro epoch h; cases h)
  | load client resource epoch holder live =>
    have notFreed : s.freed resource = false := by
      cases h : s.freed resource with
      | false => rfl
      | true =>
        obtain ⟨retirement, hr⟩ := retired resource h
        rw [live] at hr
        cases hr
    refine ⟨bounds, ?_, ?_, ?_, retired⟩
    · intro c r pointer
      by_cases same : c = client
      · exact ⟨epoch, by simpa [same] using holder⟩
      · simp [same] at pointer
        exact tracked c r pointer
    · intro c e r retirement announced pointer hr
      by_cases same : c = client
      · simp [same] at pointer
        subst r
        rw [live] at hr
        cases hr
      · simp [same] at pointer
        exact discipline c e r retirement announced pointer hr
    · intro c r pointer
      by_cases same : c = client
      · simp [same] at pointer
        subst r
        exact notFreed
      · simp [same] at pointer
        exact noFreed c r pointer
  | retire resource live =>
    refine ⟨?_, tracked, ?_, noFreed, ?_⟩
    · intro c epoch holder
      have bound := bounds c epoch holder
      change epoch ≤ s.current + 1
      omega
    · intro c epoch r retirement holder pointer hr
      by_cases same : r = resource
      · simp [same] at hr
        subst retirement
        exact bounds c epoch holder
      · simp [same] at hr
        exact discipline c epoch r retirement holder pointer hr
    · intro r freed
      by_cases same : r = resource
      · exact ⟨s.current, by simp [same]⟩
      · obtain ⟨epoch, hr⟩ := retired r freed
        exact ⟨epoch, by simpa [same] using hr⟩
  | reclaim resource eligible =>
    have inaccessible := reclamation_safe s discipline resource eligible
    refine ⟨bounds, tracked, discipline, ?_, ?_⟩
    · intro c r pointer
      by_cases same : r = resource
      · obtain ⟨epoch, holder⟩ := tracked c r pointer
        exact False.elim (inaccessible ⟨c, epoch, holder, by simpa [same] using pointer⟩)
      · simpa [same] using noFreed c r pointer
    · intro r freed
      by_cases same : r = resource
      · obtain ⟨epoch, hr, _⟩ := eligible
        exact ⟨epoch, by simpa [same] using hr⟩
      · simp [same] at freed
        exact retired r freed

inductive Reachable [DecidableEq Client] [DecidableEq Resource] : State Client Resource → Prop where
  | init : Reachable initial
  | next {s next : State Client Resource} : Reachable s → Step s next → Reachable next

theorem reachable_safe [DecidableEq Client] [DecidableEq Resource] {s : State Client Resource} (reachable : Reachable s) :
    Invariant s := by
  induction reachable with
  | init => exact initial_safe
  | next _ step ih => exact step_preserves ih step

theorem protected_not_freed [DecidableEq Client] [DecidableEq Resource] {s : State Client Resource}
    (reachable : Reachable s) (client : Client) (resource : Resource) (pointer : s.pointer client = some resource) :
    s.freed resource = false :=
  (reachable_safe reachable).2.2.2.1 client resource pointer

end Tsavorite.Epoch
