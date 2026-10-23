---------------------------- MODULE Checkpoint ----------------------------
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS Versions, Values, NoVersion, NoValue, MaxCrashes, BrokenCommit

(* --algorithm Checkpoint
variables pending = NoVersion,
          durable = {},
          committed = NoVersion,
          recovered = NoVersion,
          captured = [version \in Versions |-> NoValue],
          disk = [version \in Versions |-> NoValue],
          nextVersion = 1,
          crashes = 0;

fair process Checkpointer = "Checkpointer"
begin
Capture:
  while nextVersion <= Cardinality(Versions) do
    pending := nextVersion;
    with value \in Values do captured[nextVersion] := value; end with;
    nextVersion := nextVersion + 1;
Flush:
    either
      if pending # NoVersion then
        disk[pending] := captured[pending];
        durable := durable \cup {pending};
      end if;
    or
      await BrokenCommit;
    end either;
Commit:
    if pending # NoVersion then
      await pending \in durable \/ BrokenCommit;
      committed := pending;
    end if;
  end while;
end process;

fair process CrashRecovery = "CrashRecovery"
begin
Crash:
  while crashes < MaxCrashes do
    pending := NoVersion;
    recovered := NoVersion;
    crashes := crashes + 1;
Recover:
    recovered := committed;
  end while;
end process;
end algorithm; *)
\* BEGIN TRANSLATION (chksum(pcal) = "c5d267bc" /\ chksum(tla) = "3c4c242d")
VARIABLES pending, durable, committed, recovered, captured, disk, nextVersion, 
          crashes, pc

vars == << pending, durable, committed, recovered, captured, disk, 
           nextVersion, crashes, pc >>

ProcSet == {"Checkpointer"} \cup {"CrashRecovery"}

Init == (* Global variables *)
        /\ pending = NoVersion
        /\ durable = {}
        /\ committed = NoVersion
        /\ recovered = NoVersion
        /\ captured = [version \in Versions |-> NoValue]
        /\ disk = [version \in Versions |-> NoValue]
        /\ nextVersion = 1
        /\ crashes = 0
        /\ pc = [self \in ProcSet |-> CASE self = "Checkpointer" -> "Capture"
                                        [] self = "CrashRecovery" -> "Crash"]

Capture == /\ pc["Checkpointer"] = "Capture"
           /\ IF nextVersion <= Cardinality(Versions)
                 THEN /\ pending' = nextVersion
                      /\ \E value \in Values:
                           captured' = [captured EXCEPT ![nextVersion] = value]
                      /\ nextVersion' = nextVersion + 1
                      /\ pc' = [pc EXCEPT !["Checkpointer"] = "Flush"]
                 ELSE /\ pc' = [pc EXCEPT !["Checkpointer"] = "Done"]
                      /\ UNCHANGED << pending, captured, nextVersion >>
           /\ UNCHANGED << durable, committed, recovered, disk, crashes >>

Flush == /\ pc["Checkpointer"] = "Flush"
         /\ \/ /\ IF pending # NoVersion
                     THEN /\ disk' = [disk EXCEPT ![pending] = captured[pending]]
                          /\ durable' = (durable \cup {pending})
                     ELSE /\ TRUE
                          /\ UNCHANGED << durable, disk >>
            \/ /\ BrokenCommit
               /\ UNCHANGED <<durable, disk>>
         /\ pc' = [pc EXCEPT !["Checkpointer"] = "Commit"]
         /\ UNCHANGED << pending, committed, recovered, captured, nextVersion, 
                         crashes >>

Commit == /\ pc["Checkpointer"] = "Commit"
          /\ IF pending # NoVersion
                THEN /\ pending \in durable \/ BrokenCommit
                     /\ committed' = pending
                ELSE /\ TRUE
                     /\ UNCHANGED committed
          /\ pc' = [pc EXCEPT !["Checkpointer"] = "Capture"]
          /\ UNCHANGED << pending, durable, recovered, captured, disk, 
                          nextVersion, crashes >>

Checkpointer == Capture \/ Flush \/ Commit

Crash == /\ pc["CrashRecovery"] = "Crash"
         /\ IF crashes < MaxCrashes
               THEN /\ pending' = NoVersion
                    /\ recovered' = NoVersion
                    /\ crashes' = crashes + 1
                    /\ pc' = [pc EXCEPT !["CrashRecovery"] = "Recover"]
               ELSE /\ pc' = [pc EXCEPT !["CrashRecovery"] = "Done"]
                    /\ UNCHANGED << pending, recovered, crashes >>
         /\ UNCHANGED << durable, committed, captured, disk, nextVersion >>

Recover == /\ pc["CrashRecovery"] = "Recover"
           /\ recovered' = committed
           /\ pc' = [pc EXCEPT !["CrashRecovery"] = "Crash"]
           /\ UNCHANGED << pending, durable, committed, captured, disk, 
                           nextVersion, crashes >>

CrashRecovery == Crash \/ Recover

(* Allow infinite stuttering to prevent deadlock on termination. *)
Terminating == /\ \A self \in ProcSet: pc[self] = "Done"
               /\ UNCHANGED vars

Next == Checkpointer \/ CrashRecovery
           \/ Terminating

Spec == /\ Init /\ [][Next]_vars
        /\ WF_vars(Checkpointer)
        /\ WF_vars(CrashRecovery)

Termination == <>(\A self \in ProcSet: pc[self] = "Done")

\* END TRANSLATION 

CommittedIsDurable == committed = NoVersion \/ committed \in durable
RecoveryHasArtifact == recovered = NoVersion \/
  (recovered \in durable /\ disk[recovered] = captured[recovered])
TypeOK == /\ pending \in Versions \cup {NoVersion}
          /\ committed \in Versions \cup {NoVersion}
          /\ recovered \in Versions \cup {NoVersion}
          /\ durable \subseteq Versions
          /\ captured \in [Versions -> Values \cup {NoValue}]
          /\ disk \in [Versions -> Values \cup {NoValue}]
=============================================================================
