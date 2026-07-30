; ===========================================================================
; REDUCED AArch64 instruction stream -- LightEpoch as on origin/main.
;
; Derived from ../arm64-main.asm (verbatim RyuJIT FullOpts output,
; .NET 10.0.100 on Ubuntu 24.04 aarch64, Azure Standard_D8ps_v5).
; Every removal is itemised and justified in ../../REDUCTION.md.
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
