; Assembly listing for method Tsavorite.core.DisasmDriver:OpResume() (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Unix
; FullOpts code
; optimized code
; fp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 3 single block inlinees; 4 inlinees without PGO data

G_M000_IG01:
            stp     fp, lr, [sp, #-0x40]!
            stp     x19, x20, [sp, #0x10]
            stp     x21, x22, [sp, #0x20]
            stp     x23, x24, [sp, #0x30]
            mov     fp, sp
 
G_M000_IG02:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldapr   w0, [x0]
            tbz     w0, #0, G_M000_IG20
 
G_M000_IG03:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldr     x19, [x0]
            ldrsb   wzr, [x19]
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            movz    x1, #0xD1FFAB1E
            movk    x1, #0xD1FFAB1E LSL #16
            movk    x1, #0xD1FFAB1E LSL #32
            ldr     x1, [x1]
            blr     x1
            ldr     x0, [x0, #0x10]
            add     x0, x0, #8
            ldr     w1, [x19, #0x48]
            sbfiz   x1, x1, #2, #32
            add     x20, x0, x1
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            movz    x1, #0xD1FFAB1E
            movk    x1, #0xD1FFAB1E LSL #16
            movk    x1, #0xD1FFAB1E LSL #32
            ldr     x1, [x1]
            blr     x1
            mov     x21, x0
            ldr     w0, [x21, #0x10]
            cbnz    w0, G_M000_IG06
 
G_M000_IG04:
            bl      System.Environment:get_CurrentManagedThreadId():int
            str     w0, [x21, #0x10]
            ldr     w0, [x21, #0x10]
            eor     w0, w0, w0,  LSR #16
            movz    w1, #0xD1FFAB1E
            movk    w1, #0xD1FFAB1E LSL #16
            mul     w0, w0, w1
            eor     w0, w0, w0,  LSR #13
            movz    w1, #0xD1FFAB1E
            movk    w1, #0xD1FFAB1E LSL #16
            mul     w0, w0, w1
            eor     w22, w0, w0,  LSR #16
            add     x23, x21, #20
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldapr   w0, [x0]
            tbz     w0, #0, G_M000_IG21
 
G_M000_IG05:
            movz    x24, #0xD1FFAB1E
            movk    x24, #0xD1FFAB1E LSL #16
            movk    x24, #0xD1FFAB1E LSL #32
            ldrh    w0, [x24]
            sxtw    w1, w0
            cbz     w1, G_M000_IG23
            udiv    w2, w22, w1
            msub    w1, w2, w1, w22
            add     w1, w1, #1
            strh    w1, [x23]
            lsr     w1, w22, #16
            cbz     w0, G_M000_IG23
            udiv    w2, w1, w0
            msub    w0, w2, w0, w1
            add     w0, w0, #1
            strh    w0, [x21, #0x16]
 
G_M000_IG06:
            ldrh    w0, [x21, #0x14]
            str     w0, [x20]
            ldr     x0, [x19, #0x28]
            ldr     w1, [x20]
            sbfiz   x1, x1, #6, #32
            add     x0, x0, x1
            ldr     w0, [x0, #0x08]
            cbnz    w0, G_M000_IG08
 
G_M000_IG07:
            ldr     x0, [x19, #0x28]
            add     x22, x0, x1
            ldrsb   wzr, [x22]
            add     x0, x22, #8
            ldr     w1, [x21, #0x10]
            mov     w2, wzr
            casal   w2, w1, [x0]
            cbz     w2, G_M000_IG16
 
G_M000_IG08:
            ldrh    w0, [x21, #0x14]
            ldrh    w1, [x21, #0x16]
            strh    w1, [x21, #0x14]
            strh    w0, [x21, #0x16]
            str     w1, [x20]
            ldr     x0, [x19, #0x28]
            ldr     w1, [x20]
            sbfiz   x1, x1, #6, #32
            add     x2, x0, x1
            ldr     w2, [x2, #0x08]
            cbnz    w2, G_M000_IG09
            add     x22, x0, x1
            ldrsb   wzr, [x22]
            add     x0, x22, #8
            ldr     w1, [x21, #0x10]
            mov     w2, wzr
            casal   w2, w1, [x0]
            cbz     w2, G_M000_IG16
 
G_M000_IG09:
            mov     w23, wzr
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldapr   w0, [x0]
            tbz     w0, #0, G_M000_IG22
 
G_M000_IG10:
            movz    x24, #0xD1FFAB1E
            movk    x24, #0xD1FFAB1E LSL #16
            movk    x24, #0xD1FFAB1E LSL #32
            b       G_M000_IG12
            align   [0 bytes for IG11]
            align   [0 bytes]
            align   [0 bytes]
            align   [0 bytes]
 
G_M000_IG11:
            add     w23, w23, #1
 
G_M000_IG12:
            ldrh    w0, [x24]
            cmp     w23, w0,  LSL #1
            bge     G_M000_IG19
            ldrh    w1, [x21, #0x14]
            add     w1, w1, #1
            strh    w1, [x21, #0x14]
            ldrh    w1, [x21, #0x14]
            cmp     w1, w0
            ble     G_M000_IG14
 
G_M000_IG13:
            sub     w0, w1, w0
            strh    w0, [x21, #0x14]
 
G_M000_IG14:
            ldrh    w0, [x21, #0x14]
            str     w0, [x20]
            ldr     x0, [x19, #0x28]
            ldr     w1, [x20]
            sbfiz   x1, x1, #6, #32
            add     x2, x0, x1
            ldr     w2, [x2, #0x08]
            cbnz    w2, G_M000_IG11
 
G_M000_IG15:
            add     x22, x0, x1
            ldrsb   wzr, [x22]
            add     x0, x22, #8
            ldr     w1, [x21, #0x10]
            mov     w2, wzr
            casal   w2, w1, [x0]
            cbnz    w2, G_M000_IG11
 
G_M000_IG16:
            ldp     x1, x0, [x19, #0x28]
            ldr     w2, [x20]
            sbfiz   x2, x2, #6, #32
            str     x0, [x1, x2]
            add     x1, x19, #68
            ldapr   w1, [x1]
            cmp     w1, #0
            ble     G_M000_IG18
 
G_M000_IG17:
            ldr     x1, [x19, #0x28]
            ldr     w0, [x20]
            sbfiz   x0, x0, #6, #32
            ldr     x1, [x1, x0]
            mov     x0, x19
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG18:
            ldp     x23, x24, [sp, #0x30]
            ldp     x21, x22, [sp, #0x20]
            ldp     x19, x20, [sp, #0x10]
            ldp     fp, lr, [sp], #0x40
            ret     lr
 
G_M000_IG19:
            str     wzr, [x20]
            mov     x0, x19
            mov     x1, x20
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
            b       G_M000_IG16
 
G_M000_IG20:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GET_GCSTATIC_BASE
            b       G_M000_IG03
 
G_M000_IG21:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GET_NONGCSTATIC_BASE
            b       G_M000_IG05
 
G_M000_IG22:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GET_NONGCSTATIC_BASE
            b       G_M000_IG10
 
G_M000_IG23:
            bl      CORINFO_HELP_THROWDIVZERO
            brk     #0
 
; Total bytes of code 772

; Assembly listing for method Tsavorite.core.DisasmDriver:OpProtectAndDrain() (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Unix
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 1 single block inlinees; 1 inlinees without PGO data

G_M000_IG01:
            stp     fp, lr, [sp, #-0x20]!
            str     x19, [sp, #0x18]
            mov     fp, sp
 
G_M000_IG02:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldr     x19, [x0]
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR
            ldr     x1, [x0, #0x10]
            add     x1, x1, #8
            ldr     w0, [x19, #0x48]
            sbfiz   x0, x0, #2, #32
            add     x1, x1, x0
            ldp     x0, x2, [x19, #0x28]
            ldr     w3, [x1]
            sbfiz   x3, x3, #6, #32
            str     x2, [x0, x3]
            add     x0, x19, #68
            ldapr   w0, [x0]
            cmp     w0, #0
            ble     G_M000_IG04
 
G_M000_IG03:
            ldr     x0, [x19, #0x28]
            ldr     w1, [x1]
            sbfiz   x1, x1, #6, #32
            ldr     x1, [x0, x1]
            mov     x0, x19
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG04:
            add     x0, x19, #64
            ldapr   w0, [x0]
            cmp     w0, #0
            ble     G_M000_IG06
 
G_M000_IG05:
            mov     x0, x19
            movz    x1, #0xD1FFAB1E
            movk    x1, #0xD1FFAB1E LSL #16
            movk    x1, #0xD1FFAB1E LSL #32
            ldr     x1, [x1]
            blr     x1
 
G_M000_IG06:
            ldr     x19, [sp, #0x18]
            ldp     fp, lr, [sp], #0x20
            ret     lr
 
; Total bytes of code 188

; Assembly listing for method Tsavorite.core.LightEpoch:BumpCurrentEpoch():long:this (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Unix
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 2 single block inlinees; 0 inlinees without PGO data

G_M000_IG01:
            stp     fp, lr, [sp, #-0x20]!
            str     x19, [sp, #0x18]
            mov     fp, sp
 
G_M000_IG02:
            add     x1, x0, #48
            mov     x2, #1
            ldaddal x2, x1, [x1]
            add     x19, x1, #1
            add     x1, x0, #68
            ldapr   w1, [x1]
            cmp     w1, #0
            bgt     G_M000_IG04
 
G_M000_IG03:
            mov     x1, x19
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
            b       G_M000_IG05
 
G_M000_IG04:
            mov     x1, x19
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG05:
            mov     x0, x19
 
G_M000_IG06:
            ldr     x19, [sp, #0x18]
            ldp     fp, lr, [sp], #0x20
            ret     lr
 
; Total bytes of code 112

; Assembly listing for method Tsavorite.core.LightEpoch:ComputeNewSafeToReclaimEpoch(long):long:this (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Unix
; FullOpts code
; optimized code
; fp based frame
; fully interruptible
; No PGO data

G_M000_IG01:
            stp     fp, lr, [sp, #-0x10]!
            mov     fp, sp
 
G_M000_IG02:
            mov     w2, #1
            ldr     x3, [x0, #0x28]
            align   [0 bytes for IG03]
            align   [0 bytes]
            align   [0 bytes]
            align   [0 bytes]
 
G_M000_IG03:
            sbfiz   x4, x2, #6, #32
            ldr     x4, [x3, x4]
            cbz     x4, G_M000_IG05
 
G_M000_IG04:
            cmp     x4, x1
            bge     G_M000_IG05
            mov     x1, x4
 
G_M000_IG05:
            add     w2, w2, #1
            cmp     w2, #128
            ble     G_M000_IG03
 
G_M000_IG06:
            sub     x1, x1, #1
            str     x1, [x0, #0x38]
            mov     x0, x1
 
G_M000_IG07:
            ldp     fp, lr, [sp], #0x10
            ret     lr
 
; Total bytes of code 72

; Assembly listing for method Tsavorite.core.DisasmDriver:OpSuspend() (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Unix
; FullOpts code
; optimized code
; fp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 8 single block inlinees; 8 inlinees without PGO data

G_M000_IG01:
            stp     fp, lr, [sp, #-0x40]!
            stp     x19, x20, [sp, #0x10]
            stp     x21, x22, [sp, #0x20]
            stp     x23, x24, [sp, #0x30]
            mov     fp, sp
 
G_M000_IG02:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            ldr     x19, [x0]
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR
            ldr     x0, [x0, #0x10]
            add     x20, x0, #8
            ldr     w0, [x19, #0x48]
            sbfiz   x0, x0, #2, #32
            add     x0, x20, x0
            ldr     x1, [x19, #0x28]
            ldr     w2, [x0]
            sbfiz   x2, x2, #6, #32
            str     xzr, [x1, x2]
            ldr     x1, [x19, #0x28]
            ldr     w2, [x0]
            sbfiz   x2, x2, #6, #32
            add     x1, x1, x2
            str     wzr, [x1, #0x08]
            str     wzr, [x0]
            add     x21, x19, #64
            ldapr   w0, [x21]
            cmp     w0, #0
            ble     G_M000_IG04
 
G_M000_IG03:
            ldr     x0, [x19, #0x10]
            ldrsb   wzr, [x0]
            mov     w1, #1
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG04:
            add     x22, x19, #68
            ldapr   w0, [x22]
            cmp     w0, #0
            ble     G_M000_IG24
 
G_M000_IG05:
            movz    x0, #0xD1FFAB1E
            movk    x0, #0xD1FFAB1E LSL #16
            movk    x0, #0xD1FFAB1E LSL #32
            bl      CORINFO_HELP_GETDYNAMIC_NONGCTHREADSTATIC_BASE_NOCTOR
            mov     x23, x0
            b       G_M000_IG21
            align   [0 bytes for IG12]
            align   [0 bytes]
            align   [0 bytes]
            align   [0 bytes]
 
G_M000_IG06:
            ldr     w0, [x19, #0x48]
            sbfiz   x0, x0, #2, #32
            add     x24, x20, x0
            ldr     w0, [x23, #0x10]
            cbnz    w0, G_M000_IG08
 
G_M000_IG07:
            bl      System.Environment:get_CurrentManagedThreadId():int
            str     w0, [x23, #0x10]
            ldr     w0, [x23, #0x10]
            eor     w0, w0, w0,  LSR #16
            movz    w1, #0xD1FFAB1E
            movk    w1, #0xD1FFAB1E LSL #16
            mul     w0, w0, w1
            eor     w0, w0, w0,  LSR #13
            movz    w1, #0xD1FFAB1E
            movk    w1, #0xD1FFAB1E LSL #16
            mul     w0, w0, w1
            eor     w0, w0, w0,  LSR #16
            and     w1, w0, #127
            add     w1, w1, #1
            strh    w1, [x23, #0x14]
            lsr     w0, w0, #16
            and     w0, w0, #127
            add     w0, w0, #1
            strh    w0, [x23, #0x16]
 
G_M000_IG08:
            ldrh    w0, [x23, #0x14]
            str     w0, [x24]
            ldr     x0, [x19, #0x28]
            ldr     w1, [x24]
            sbfiz   x1, x1, #6, #32
            add     x2, x0, x1
            ldr     w2, [x2, #0x08]
            cbnz    w2, G_M000_IG10
 
G_M000_IG09:
            add     x0, x0, x1
            ldrsb   wzr, [x0], #0x08
            ldr     w1, [x23, #0x10]
            mov     w2, wzr
            casal   w2, w1, [x0]
            cbz     w2, G_M000_IG18
 
G_M000_IG10:
            ldrh    w0, [x23, #0x14]
            ldrh    w1, [x23, #0x16]
            strh    w1, [x23, #0x14]
            strh    w0, [x23, #0x16]
            str     w1, [x24]
            ldr     x0, [x19, #0x28]
            ldr     w1, [x24]
            sbfiz   x1, x1, #6, #32
            add     x2, x0, x1
            ldr     w2, [x2, #0x08]
         driver done: 501 125750
   cbnz    w2, G_M000_IG11
            add     x0, x0, x1
            ldrsb   wzr, [x0], #0x08
            ldr     w1, [x23, #0x10]
            mov     w2, wzr
            casal   w2, w1, [x0]
            cbz     w2, G_M000_IG18
 
G_M000_IG11:
            mov     w0, #0xD1FFAB1E
 
G_M000_IG12:
            ldrh    w1, [x23, #0x14]
            add     w1, w1, #1
            strh    w1, [x23, #0x14]
            ldrh    w1, [x23, #0x14]
            cmp     w1, #128
            ble     G_M000_IG14
 
G_M000_IG13:
            sub     w1, w1, #128
            strh    w1, [x23, #0x14]
 
G_M000_IG14:
            ldrh    w1, [x23, #0x14]
            str     w1, [x24]
            ldr     x1, [x19, #0x28]
            ldr     w2, [x24]
            sbfiz   x2, x2, #6, #32
            add     x3, x1, x2
            ldr     w3, [x3, #0x08]
            cbnz    w3, G_M000_IG16
 
G_M000_IG15:
            add     x1, x1, x2
            ldrsb   wzr, [x1], #0x08
            ldr     w2, [x23, #0x10]
            mov     w3, wzr
            casal   w3, w2, [x1]
            cbz     w3, G_M000_IG18
 
G_M000_IG16:
            sub     w0, w0, #1
            cbnz    w0, G_M000_IG12
 
G_M000_IG17:
            str     wzr, [x24]
            mov     x0, x19
            mov     x1, x24
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG18:
            ldp     x1, x0, [x19, #0x28]
            ldr     w2, [x24]
            sbfiz   x2, x2, #6, #32
            str     x0, [x1, x2]
            ldapr   w1, [x22]
            cmp     w1, #0
            ble     G_M000_IG20
 
G_M000_IG19:
            ldr     x1, [x19, #0x28]
            ldr     w0, [x24]
            sbfiz   x0, x0, #6, #32
            ldr     x1, [x1, x0]
            mov     x0, x19
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG20:
            ldr     w0, [x19, #0x48]
            sbfiz   x0, x0, #2, #32
            add     x0, x20, x0
            ldr     x1, [x19, #0x28]
            ldr     w2, [x0]
            sbfiz   x2, x2, #6, #32
            str     xzr, [x1, x2]
            ldr     x1, [x19, #0x28]
            ldr     w2, [x0]
            sbfiz   x2, x2, #6, #32
            add     x1, x1, x2
            str     wzr, [x1, #0x08]
            str     wzr, [x0]
            ldapr   w0, [x21]
            cmp     w0, #0
            ble     G_M000_IG21
            ldr     x0, [x19, #0x10]
            ldrsb   wzr, [x0]
            mov     w1, #1
            movz    x2, #0xD1FFAB1E
            movk    x2, #0xD1FFAB1E LSL #16
            movk    x2, #0xD1FFAB1E LSL #32
            ldr     x2, [x2]
            blr     x2
 
G_M000_IG21:
            ldapr   w0, [x22]
            cmp     w0, #0
            ble     G_M000_IG24
            dmb     ish
            mov     w0, #1
            ldr     x1, [x19, #0x28]
            ldr     x2, [x1, #0x40]
            cbnz    x2, G_M000_IG24
            align   [0 bytes for IG22]
            align   [0 bytes]
            align   [0 bytes]
            align   [0 bytes]
 
G_M000_IG22:
            add     w0, w0, #1
            cmp     w0, #128
            bgt     G_M000_IG06
 
G_M000_IG23:
            sbfiz   x2, x0, #6, #32
            ldr     x2, [x1, x2]
            cbz     x2, G_M000_IG22
 
G_M000_IG24:
            ldp     x23, x24, [sp, #0x30]
            ldp     x21, x22, [sp, #0x20]
            ldp     x19, x20, [sp, #0x10]
            ldp     fp, lr, [sp], #0x40
            ret     lr
 
; Total bytes of code 792

