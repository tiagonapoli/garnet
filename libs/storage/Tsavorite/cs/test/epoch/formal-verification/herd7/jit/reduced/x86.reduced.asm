; ===========================================================================
; REDUCED x86-64 instruction stream -- LightEpoch, both variants.
;
; Derived from ../x86.asm (verbatim RyuJIT FullOpts output, .NET 10.0.100).
; Every removal is itemised and justified in ../../REDUCTION.md.
;
; The two variants are kept in one file because they are meant to be read
; against each other: the litmus tests in ../../litmus are a control/fix pair,
; and what makes the fix legible is the diff between these two streams.
;
;   VARIANT main  -- LightEpoch as on origin/main
;   VARIANT fixed -- the claim CAS targets LCE and carries the announce
;
; Symbolic locations (mapping explained in ../../MODEL.md):
;   LCE = tableAligned[entry].localCurrentEpoch   long, Entry offset 0x00
;   TID = tableAligned[entry].threadId            int,  Entry offset 0x08
;   CUR = this.CurrentEpoch                       long, LightEpoch offset 0x30
;   STR = this.SafeToReclaimEpoch                 long, LightEpoch offset 0x38
;
; Register names are kept as RyuJIT chose them so each line can be matched
; back to the raw dump by eye.
; ===========================================================================


; ###########################################################################
; ## VARIANT: main
; ## The claim CAS targets TID; the announce is a separate plain store.
; ###########################################################################


; --- Acquire()  [raw: OpResume, G_M000_IG07 .. G_M000_IG17] ----------------

Acquire:
        cmp      dword ptr [TID], 0             ; probe for a free slot
        jne      Acquire                        ; (probe sequence collapsed)

        mov      edx, dword ptr [Metadata.threadId]
        xor      eax, eax
        lock
        cmpxchg  dword ptr [TID], edx           ; CLAIM: TID 0 -> myTid   (full fence)
        test     eax, eax
        jne      Acquire                        ; lost the race

        mov      rax, qword ptr [CUR]           ; plain load  of CurrentEpoch
        mov      qword ptr [LCE], rax           ; plain store -- THE ANNOUNCE


; --- ProtectAndDrain()  [raw: OpProtectAndDrain, G_M000_IG03] --------------

Refresh:
        mov      r8, qword ptr [CUR]            ; plain load  of CurrentEpoch
        mov      qword ptr [LCE], r8            ; plain store -- the re-announce


; --- Release()  [raw: OpSuspend, G_M000_IG03] ------------------------------

Release:
        xor      r8d, r8d
        mov      qword ptr [LCE], r8            ; plain store, LCE <- 0    (FIRST)
        mov      dword ptr [TID], r8d           ; plain store, TID <- 0    (SECOND)


; --- BumpCurrentEpoch()  [raw: LightEpoch:BumpCurrentEpoch] ----------------

Bump:
        lock
        xadd     qword ptr [CUR], rbx           ; Interlocked.Increment    (full fence)


; --- ComputeNewSafeToReclaimEpoch()  [raw: same method] --------------------
; The reclaimer's scan. One plain load per table entry, then a plain store of
; the result. This is the side that must not observe a stale LCE.

Reclaim:
        mov      rax, qword ptr [LCE]           ; plain load of one entry's announce
        mov      qword ptr [STR], rcx           ; plain store of SafeToReclaimEpoch


; ###########################################################################
; ## VARIANT: fixed
; ## The claim CAS targets LCE, so claiming the slot and announcing the epoch
; ## are a single locked RMW.
; ###########################################################################


; --- Acquire() / TryClaimEntry()  [raw: OpResume, G_M000_IG07] -------------
; The claim and the announce are now the same instruction: the CAS writes the
; epoch into LCE. There is no separate plain announce store to delay.

Acquire:
        cmp      qword ptr [LCE], 0             ; probe for a free slot
        jne      Acquire                        ; (probe sequence collapsed)

        mov      rdi, qword ptr [CUR]           ; plain load of CurrentEpoch
        xor      eax, eax
        lock
        cmpxchg  qword ptr [LCE], rdi           ; CLAIM + ANNOUNCE in one RMW (full fence)
        test     rax, rax
        jne      Acquire                        ; lost the race

        mov      ecx, dword ptr [Metadata.threadId]
        mov      dword ptr [TID], ecx           ; plain store; slot is already ours


; --- ProtectAndDrain()  [raw: OpProtectAndDrain, G_M000_IG03] --------------
; Volatile.Read compiles to a plain MOV on x86: TSO already forbids the
; load-load reordering that the acquire load exists to prevent.

Refresh:
        mov      r8, qword ptr [CUR]            ; Volatile.Read -> plain load on x86
        mov      qword ptr [LCE], r8            ; plain store -- the re-announce


; --- Release()  [raw: OpSuspend, G_M000_IG03/IG04] -------------------------
; Order is inverted relative to main: TID is cleared FIRST, and LCE -- now the
; slot-ownership word -- is cleared LAST with a release store.

Release:
        xor      r8d, r8d
        mov      dword ptr [TID], r8d           ; plain store, TID <- 0    (FIRST)
        mov      qword ptr [LCE], r8            ; Volatile.Write -> plain store on x86
                                                ;   LCE <- 0              (SECOND)


; --- BumpCurrentEpoch()  [raw: LightEpoch:BumpCurrentEpoch] ----------------

Bump:
        lock
        xadd     qword ptr [CUR], rbx           ; Interlocked.Increment    (full fence)


; --- ComputeNewSafeToReclaimEpoch()  [raw: same method] --------------------
; Unchanged by the fix.

Reclaim:
        mov      rax, qword ptr [LCE]           ; plain load of one entry's announce
        mov      qword ptr [STR], rcx           ; plain store of SafeToReclaimEpoch
