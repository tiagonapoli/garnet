; ===========================================================================
; REDUCED AArch64 instruction stream -- LightEpoch, both variants.
;
; Derived from ../arm64.asm (verbatim RyuJIT FullOpts output, .NET 10.0.100
; on Ubuntu 24.04 aarch64, Azure Standard_D8ps_v5).
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
; Note: x19 holds `this` throughout, so [x19, #0x30] is CUR and
; [x19, #0x28] is the tableAligned pointer.
; ===========================================================================


; ###########################################################################
; ## VARIANT: main
; ## The claim CAS targets TID; the announce is a separate plain store.
; ###########################################################################


; --- Acquire()  [raw: OpResume, G_M000_IG07 .. G_M000_IG16] ----------------

Acquire:
        ldr     w2, [TID]                       ; probe for a free slot
        cbnz    w2, Acquire                     ; (probe sequence collapsed)

        ldr     w1, [Metadata.threadId]
        mov     w2, wzr
        casal   w2, w1, [TID]                   ; CLAIM: TID 0 -> myTid
        cbnz    w2, Acquire                     ; lost the race

        ldp     x1, x0, [x19, #0x28]            ; PLAIN pair load: tableAligned AND CUR
        str     x0, [LCE]                       ; PLAIN store -- THE ANNOUNCE


; --- ProtectAndDrain()  [raw: OpProtectAndDrain, G_M000_IG02] --------------
; CurrentEpoch is read as half of an LDP -- an ordinary, unordered pair load.
; Nothing here orders this load against the caller's subsequent data accesses.

Refresh:
        ldp     x0, x2, [x19, #0x28]            ; PLAIN pair load: tableAligned AND CUR
        str     x2, [LCE]                       ; PLAIN store -- the re-announce


; --- Release()  [raw: OpSuspend, G_M000_IG02] ------------------------------

Release:
        str     xzr, [LCE]                      ; PLAIN store, LCE <- 0    (FIRST)
        str     wzr, [TID]                      ; PLAIN store, TID <- 0    (SECOND)


; --- BumpCurrentEpoch()  [raw: LightEpoch:BumpCurrentEpoch] ----------------

Bump:
        ldaddal x2, x1, [CUR]                   ; Interlocked.Increment -> LDADDAL


; --- ComputeNewSafeToReclaimEpoch()  [raw: same method] --------------------
; The reclaimer's scan: a plain load per entry, plain store of the result.

Reclaim:
        ldr     x4, [LCE]                       ; PLAIN load of one entry's announce
        str     x1, [STR]                       ; PLAIN store of SafeToReclaimEpoch


; ###########################################################################
; ## VARIANT: fixed
; ## The claim CAS targets LCE, so claiming the slot and announcing the epoch
; ## are a single locked RMW.
; ###########################################################################


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
