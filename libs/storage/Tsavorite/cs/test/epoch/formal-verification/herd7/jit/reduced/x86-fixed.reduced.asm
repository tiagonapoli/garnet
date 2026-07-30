; ===========================================================================
; REDUCED x86-64 instruction stream -- LightEpoch with the CAS-announce fix.
;
; Derived from ../x86-fixed.asm (verbatim RyuJIT FullOpts output, .NET 10.0.100).
; Every removal is itemised and justified in ../../REDUCTION.md.
;
; Symbolic locations (mapping explained in ../../MODEL.md):
;   LCE = tableAligned[entry].localCurrentEpoch   long, Entry offset 0x00
;   TID = tableAligned[entry].threadId            int,  Entry offset 0x08
;   CUR = this.CurrentEpoch                       long, LightEpoch offset 0x30
;   STR = this.SafeToReclaimEpoch                 long, LightEpoch offset 0x38
; ===========================================================================


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
