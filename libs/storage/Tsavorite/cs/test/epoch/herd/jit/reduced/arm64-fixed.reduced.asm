; ===========================================================================
; REDUCED AArch64 instruction stream -- LightEpoch with the CAS-announce fix.
;
; Derived from ../arm64-fixed.asm (verbatim RyuJIT FullOpts output,
; .NET 10.0.100 on Ubuntu 24.04 aarch64, Azure Standard_D8ps_v5).
; Every removal is itemised and justified in ../../REDUCTION.md.
;
; Symbolic locations (mapping explained in ../../MODEL.md):
;   LCE = tableAligned[entry].localCurrentEpoch   long, Entry offset 0x00
;   TID = tableAligned[entry].threadId            int,  Entry offset 0x08
;   CUR = this.CurrentEpoch                       long, LightEpoch offset 0x30
;   STR = this.SafeToReclaimEpoch                 long, LightEpoch offset 0x38
; ===========================================================================


; --- Acquire() / TryClaimEntry()  [raw: OpResume, G_M000_IG07] -------------
; Claim and announce collapse into one CASAL on LCE. Note the epoch operand is
; still read with a plain LDR -- it is safe there because the CASAL that
; consumes it sits between that load and every subsequent data access.

Acquire:
        ldr     x1, [LCE]                       ; probe for a free slot
        cbnz    x1, Acquire                     ; (probe sequence collapsed)

        ldr     x21, [x19, #0x30]               ; plain load of CUR (CAS operand)
        mov     x2, xzr
        casal   x2, x21, [LCE]                  ; CLAIM + ANNOUNCE in one RMW
        cbnz    x2, Acquire                     ; lost the race

        ldr     w1, [Metadata.threadId]
        str     w1, [TID]                       ; plain store; slot is already ours


; --- ProtectAndDrain()  [raw: OpProtectAndDrain, G_M000_IG02] --------------
; This is where the fix bites on AArch64: the LDP disappears and CurrentEpoch
; is read with LDAPR (load-acquire RCpc), which orders every later load.

Refresh:
        add     x3, x19, #48
        ldapr   x3, [CUR]                       ; Volatile.Read -> LDAPR
        str     x3, [LCE]                       ; plain store -- the re-announce


; --- Release()  [raw: OpSuspend, G_M000_IG02] ------------------------------
; Order is inverted relative to main, and the LCE clear is now STLR.

Release:
        str     wzr, [TID]                      ; plain store, TID <- 0    (FIRST)
        stlr    xzr, [LCE]                      ; Volatile.Write -> STLR
                                                ;   LCE <- 0              (SECOND)


; --- BumpCurrentEpoch()  [raw: LightEpoch:BumpCurrentEpoch] ----------------

Bump:
        ldaddal x2, x1, [CUR]                   ; Interlocked.Increment -> LDADDAL


; --- ComputeNewSafeToReclaimEpoch()  [raw: same method] --------------------
; Unchanged by the fix.

Reclaim:
        ldr     x4, [LCE]                       ; PLAIN load of one entry's announce
        str     x1, [STR]                       ; PLAIN store of SafeToReclaimEpoch
