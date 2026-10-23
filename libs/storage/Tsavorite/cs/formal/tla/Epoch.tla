------------------------------ MODULE Epoch ------------------------------
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS Readers, MaxGenerations, MaxOps, BrokenDrain

(* --algorithm Epoch
variables currentEpoch = 1, currentGeneration = 1,
          announced = [reader \in Readers |-> 0],
          pointer = [reader \in Readers |-> 0],
          retired = [generation \in 1..MaxGenerations |-> 0],
          freed = {}, completed = [reader \in Readers |-> 0];

fair process Reader \in Readers
begin
Acquire:
  while completed[self] < MaxOps do
    announced[self] := currentEpoch;
LoadPointer:
    pointer[self] := currentGeneration;
Use:
    either
      announced[self] := currentEpoch;
      pointer[self] := 0;
    or
      skip;
    end either;
Release:
    pointer[self] := 0;
    announced[self] := 0;
    completed[self] := completed[self] + 1;
  end while;
end process;

fair process Retirer = "Retirer"
begin
Retire:
  while currentGeneration < MaxGenerations do
    retired[currentGeneration] := currentEpoch;
    currentGeneration := currentGeneration + 1;
Bump:
    currentEpoch := currentEpoch + 1;
  end while;
end process;

fair process Reclaimer = "Reclaimer"
begin
Reclaim:
  while Cardinality(freed) < MaxGenerations - 1 do
    with generation \in 1..MaxGenerations do
      await generation \notin freed /\ retired[generation] # 0 /\
        (BrokenDrain \/ \A reader \in Readers :
          announced[reader] = 0 \/ announced[reader] > retired[generation]);
      freed := freed \cup {generation};
    end with;
  end while;
end process;
end algorithm; *)
\* BEGIN TRANSLATION (chksum(pcal) = "3a677099" /\ chksum(tla) = "ecd4cca9")
VARIABLES currentEpoch, currentGeneration, announced, pointer, retired, freed, 
          completed, pc

vars == << currentEpoch, currentGeneration, announced, pointer, retired, 
           freed, completed, pc >>

ProcSet == (Readers) \cup {"Retirer"} \cup {"Reclaimer"}

Init == (* Global variables *)
        /\ currentEpoch = 1
        /\ currentGeneration = 1
        /\ announced = [reader \in Readers |-> 0]
        /\ pointer = [reader \in Readers |-> 0]
        /\ retired = [generation \in 1..MaxGenerations |-> 0]
        /\ freed = {}
        /\ completed = [reader \in Readers |-> 0]
        /\ pc = [self \in ProcSet |-> CASE self \in Readers -> "Acquire"
                                        [] self = "Retirer" -> "Retire"
                                        [] self = "Reclaimer" -> "Reclaim"]

Acquire(self) == /\ pc[self] = "Acquire"
                 /\ IF completed[self] < MaxOps
                       THEN /\ announced' = [announced EXCEPT ![self] = currentEpoch]
                            /\ pc' = [pc EXCEPT ![self] = "LoadPointer"]
                       ELSE /\ pc' = [pc EXCEPT ![self] = "Done"]
                            /\ UNCHANGED announced
                 /\ UNCHANGED << currentEpoch, currentGeneration, pointer, 
                                 retired, freed, completed >>

LoadPointer(self) == /\ pc[self] = "LoadPointer"
                     /\ pointer' = [pointer EXCEPT ![self] = currentGeneration]
                     /\ pc' = [pc EXCEPT ![self] = "Use"]
                     /\ UNCHANGED << currentEpoch, currentGeneration, 
                                     announced, retired, freed, completed >>

Use(self) == /\ pc[self] = "Use"
             /\ \/ /\ announced' = [announced EXCEPT ![self] = currentEpoch]
                   /\ pointer' = [pointer EXCEPT ![self] = 0]
                \/ /\ TRUE
                   /\ UNCHANGED <<announced, pointer>>
             /\ pc' = [pc EXCEPT ![self] = "Release"]
             /\ UNCHANGED << currentEpoch, currentGeneration, retired, freed, 
                             completed >>

Release(self) == /\ pc[self] = "Release"
                 /\ pointer' = [pointer EXCEPT ![self] = 0]
                 /\ announced' = [announced EXCEPT ![self] = 0]
                 /\ completed' = [completed EXCEPT ![self] = completed[self] + 1]
                 /\ pc' = [pc EXCEPT ![self] = "Acquire"]
                 /\ UNCHANGED << currentEpoch, currentGeneration, retired, 
                                 freed >>

Reader(self) == Acquire(self) \/ LoadPointer(self) \/ Use(self)
                   \/ Release(self)

Retire == /\ pc["Retirer"] = "Retire"
          /\ IF currentGeneration < MaxGenerations
                THEN /\ retired' = [retired EXCEPT ![currentGeneration] = currentEpoch]
                     /\ currentGeneration' = currentGeneration + 1
                     /\ pc' = [pc EXCEPT !["Retirer"] = "Bump"]
                ELSE /\ pc' = [pc EXCEPT !["Retirer"] = "Done"]
                     /\ UNCHANGED << currentGeneration, retired >>
          /\ UNCHANGED << currentEpoch, announced, pointer, freed, completed >>

Bump == /\ pc["Retirer"] = "Bump"
        /\ currentEpoch' = currentEpoch + 1
        /\ pc' = [pc EXCEPT !["Retirer"] = "Retire"]
        /\ UNCHANGED << currentGeneration, announced, pointer, retired, freed, 
                        completed >>

Retirer == Retire \/ Bump

Reclaim == /\ pc["Reclaimer"] = "Reclaim"
           /\ IF Cardinality(freed) < MaxGenerations - 1
                 THEN /\ \E generation \in 1..MaxGenerations:
                           /\     generation \notin freed /\ retired[generation] # 0 /\
                              (BrokenDrain \/ \A reader \in Readers :
                                announced[reader] = 0 \/ announced[reader] > retired[generation])
                           /\ freed' = (freed \cup {generation})
                      /\ pc' = [pc EXCEPT !["Reclaimer"] = "Reclaim"]
                 ELSE /\ pc' = [pc EXCEPT !["Reclaimer"] = "Done"]
                      /\ freed' = freed
           /\ UNCHANGED << currentEpoch, currentGeneration, announced, pointer, 
                           retired, completed >>

Reclaimer == Reclaim

(* Allow infinite stuttering to prevent deadlock on termination. *)
Terminating == /\ \A self \in ProcSet: pc[self] = "Done"
               /\ UNCHANGED vars

Next == Retirer \/ Reclaimer
           \/ (\E self \in Readers: Reader(self))
           \/ Terminating

Spec == /\ Init /\ [][Next]_vars
        /\ \A self \in Readers : WF_vars(Reader(self))
        /\ WF_vars(Retirer)
        /\ WF_vars(Reclaimer)

Termination == <>(\A self \in ProcSet: pc[self] = "Done")

\* END TRANSLATION 

NoProtectedReclamation == \A reader \in Readers :
  pointer[reader] # 0 => pointer[reader] \notin freed
AccessDiscipline == \A reader \in Readers :
  pointer[reader] # 0 /\ retired[pointer[reader]] # 0 =>
    announced[reader] <= retired[pointer[reader]]
TypeOK == /\ currentGeneration \in 1..MaxGenerations
          /\ announced \in [Readers -> 0..MaxGenerations]
          /\ pointer \in [Readers -> 0..MaxGenerations]
          /\ freed \subseteq 1..MaxGenerations
=============================================================================
