------------------------------- MODULE Core -------------------------------
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS Clients, Keys, Values, Absent, MaxOps, BrokenLock

Rmw(value) == IF value = Absent THEN 0 ELSE (value + 1) % Cardinality(Values)

(* --algorithm Core
variables
  data = [k \in Keys |-> Absent],
  reference = [k \in Keys |-> Absent],
  owner = 0,
  generation = 0,
  completed = [client \in Clients |-> 0],
  readOK = [client \in Clients |-> TRUE];

fair process Client \in Clients
variables command = "Read", key = CHOOSE k \in Keys : TRUE,
          value = CHOOSE v \in Values : TRUE, candidate = Absent,
          expected = 0, append = FALSE, retried = FALSE;
begin
Choose:
  while completed[self] < MaxOps do
    with op \in {"Read", "Upsert", "Delete", "Rmw"}, k \in Keys do
      command := op;
      key := k;
    end with;
    if command = "Upsert" then
      with v \in Values do value := v; end with;
    end if;
    with mode \in BOOLEAN do append := mode; end with;
    retried := FALSE;
Lock:
    await owner = 0 \/ BrokenLock;
    owner := self;
Prepare:
    expected := generation;
    candidate := CASE command = "Upsert" -> value
                  [] command = "Delete" -> Absent
                  [] command = "Rmw" -> Rmw(data[key])
                  [] OTHER -> data[key];
Publish:
    if command = "Read" then
      readOK[self] := readOK[self] /\ data[key] = reference[key];
    elsif append then
      either
        await ~retried;
        retried := TRUE;
        goto Prepare;
      or
        if expected # generation then
          goto Prepare;
        else
          data[key] := candidate;
          reference[key] := CASE command = "Upsert" -> value
                             [] command = "Delete" -> Absent
                             [] OTHER -> Rmw(reference[key]);
          generation := generation + 1;
        end if;
      end either;
    else
      data[key] := candidate;
      reference[key] := CASE command = "Upsert" -> value
                         [] command = "Delete" -> Absent
                         [] OTHER -> Rmw(reference[key]);
      generation := generation + 1;
    end if;
Unlock:
    owner := 0;
    completed[self] := completed[self] + 1;
    command := "Read";
    key := CHOOSE k \in Keys : TRUE;
    value := CHOOSE v \in Values : TRUE;
    candidate := Absent;
    expected := 0;
    append := FALSE;
    retried := FALSE;
  end while;
end process;
end algorithm; *)
\* BEGIN TRANSLATION (chksum(pcal) = "63ac9e2e" /\ chksum(tla) = "20ce4b7d")
VARIABLES data, reference, owner, generation, completed, readOK, pc, command, 
          key, value, candidate, expected, append, retried

vars == << data, reference, owner, generation, completed, readOK, pc, command, 
           key, value, candidate, expected, append, retried >>

ProcSet == (Clients)

Init == (* Global variables *)
        /\ data = [k \in Keys |-> Absent]
        /\ reference = [k \in Keys |-> Absent]
        /\ owner = 0
        /\ generation = 0
        /\ completed = [client \in Clients |-> 0]
        /\ readOK = [client \in Clients |-> TRUE]
        (* Process Client *)
        /\ command = [self \in Clients |-> "Read"]
        /\ key = [self \in Clients |-> CHOOSE k \in Keys : TRUE]
        /\ value = [self \in Clients |-> CHOOSE v \in Values : TRUE]
        /\ candidate = [self \in Clients |-> Absent]
        /\ expected = [self \in Clients |-> 0]
        /\ append = [self \in Clients |-> FALSE]
        /\ retried = [self \in Clients |-> FALSE]
        /\ pc = [self \in ProcSet |-> "Choose"]

Choose(self) == /\ pc[self] = "Choose"
                /\ IF completed[self] < MaxOps
                      THEN /\ \E op \in {"Read", "Upsert", "Delete", "Rmw"}:
                                \E k \in Keys:
                                  /\ command' = [command EXCEPT ![self] = op]
                                  /\ key' = [key EXCEPT ![self] = k]
                           /\ IF command'[self] = "Upsert"
                                 THEN /\ \E v \in Values:
                                           value' = [value EXCEPT ![self] = v]
                                 ELSE /\ TRUE
                                      /\ value' = value
                           /\ \E mode \in BOOLEAN:
                                append' = [append EXCEPT ![self] = mode]
                           /\ retried' = [retried EXCEPT ![self] = FALSE]
                           /\ pc' = [pc EXCEPT ![self] = "Lock"]
                      ELSE /\ pc' = [pc EXCEPT ![self] = "Done"]
                           /\ UNCHANGED << command, key, value, append, 
                                           retried >>
                /\ UNCHANGED << data, reference, owner, generation, completed, 
                                readOK, candidate, expected >>

Lock(self) == /\ pc[self] = "Lock"
              /\ owner = 0 \/ BrokenLock
              /\ owner' = self
              /\ pc' = [pc EXCEPT ![self] = "Prepare"]
              /\ UNCHANGED << data, reference, generation, completed, readOK, 
                              command, key, value, candidate, expected, append, 
                              retried >>

Prepare(self) == /\ pc[self] = "Prepare"
                 /\ expected' = [expected EXCEPT ![self] = generation]
                 /\ candidate' = [candidate EXCEPT ![self] = CASE command[self] = "Upsert" -> value[self]
                                                              [] command[self] = "Delete" -> Absent
                                                              [] command[self] = "Rmw" -> Rmw(data[key[self]])
                                                              [] OTHER -> data[key[self]]]
                 /\ pc' = [pc EXCEPT ![self] = "Publish"]
                 /\ UNCHANGED << data, reference, owner, generation, completed, 
                                 readOK, command, key, value, append, retried >>

Publish(self) == /\ pc[self] = "Publish"
                 /\ IF command[self] = "Read"
                       THEN /\ readOK' = [readOK EXCEPT ![self] = readOK[self] /\ data[key[self]] = reference[key[self]]]
                            /\ pc' = [pc EXCEPT ![self] = "Unlock"]
                            /\ UNCHANGED << data, reference, generation, 
                                            retried >>
                       ELSE /\ IF append[self]
                                  THEN /\ \/ /\ ~retried[self]
                                             /\ retried' = [retried EXCEPT ![self] = TRUE]
                                             /\ pc' = [pc EXCEPT ![self] = "Prepare"]
                                             /\ UNCHANGED <<data, reference, generation>>
                                          \/ /\ IF expected[self] # generation
                                                   THEN /\ pc' = [pc EXCEPT ![self] = "Prepare"]
                                                        /\ UNCHANGED << data, 
                                                                        reference, 
                                                                        generation >>
                                                   ELSE /\ data' = [data EXCEPT ![key[self]] = candidate[self]]
                                                        /\ reference' = [reference EXCEPT ![key[self]] = CASE command[self] = "Upsert" -> value[self]
                                                                                                          [] command[self] = "Delete" -> Absent
                                                                                                          [] OTHER -> Rmw(reference[key[self]])]
                                                        /\ generation' = generation + 1
                                                        /\ pc' = [pc EXCEPT ![self] = "Unlock"]
                                             /\ UNCHANGED retried
                                  ELSE /\ data' = [data EXCEPT ![key[self]] = candidate[self]]
                                       /\ reference' = [reference EXCEPT ![key[self]] = CASE command[self] = "Upsert" -> value[self]
                                                                                         [] command[self] = "Delete" -> Absent
                                                                                         [] OTHER -> Rmw(reference[key[self]])]
                                       /\ generation' = generation + 1
                                       /\ pc' = [pc EXCEPT ![self] = "Unlock"]
                                       /\ UNCHANGED retried
                            /\ UNCHANGED readOK
                 /\ UNCHANGED << owner, completed, command, key, value, 
                                 candidate, expected, append >>

Unlock(self) == /\ pc[self] = "Unlock"
                /\ owner' = 0
                /\ completed' = [completed EXCEPT ![self] = completed[self] + 1]
                /\ command' = [command EXCEPT ![self] = "Read"]
                /\ key' = [key EXCEPT ![self] = CHOOSE k \in Keys : TRUE]
                /\ value' = [value EXCEPT ![self] = CHOOSE v \in Values : TRUE]
                /\ candidate' = [candidate EXCEPT ![self] = Absent]
                /\ expected' = [expected EXCEPT ![self] = 0]
                /\ append' = [append EXCEPT ![self] = FALSE]
                /\ retried' = [retried EXCEPT ![self] = FALSE]
                /\ pc' = [pc EXCEPT ![self] = "Choose"]
                /\ UNCHANGED << data, reference, generation, readOK >>

Client(self) == Choose(self) \/ Lock(self) \/ Prepare(self)
                   \/ Publish(self) \/ Unlock(self)

(* Allow infinite stuttering to prevent deadlock on termination. *)
Terminating == /\ \A self \in ProcSet: pc[self] = "Done"
               /\ UNCHANGED vars

Next == (\E self \in Clients: Client(self))
           \/ Terminating

Spec == /\ Init /\ [][Next]_vars
        /\ \A self \in Clients : WF_vars(Client(self))

Termination == <>(\A self \in ProcSet: pc[self] = "Done")

\* END TRANSLATION 

DataRefinesMap == data = reference
ReadsAgree == \A client \in Clients : readOK[client]
TypeOK == /\ data \in [Keys -> Values \cup {Absent}]
          /\ reference \in [Keys -> Values \cup {Absent}]
          /\ owner \in Clients \cup {0}
          /\ completed \in [Clients -> 0..MaxOps]
=============================================================================
