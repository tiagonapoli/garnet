; ===========================================================================
; REDUCED x86-64 instruction stream -- LightEpoch as on origin/main.
;
; Derived from ../x86-main.asm (verbatim RyuJIT FullOpts output, .NET 10.0.100).
; Every removal is itemised and justified in ../../REDUCTION.md.
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
