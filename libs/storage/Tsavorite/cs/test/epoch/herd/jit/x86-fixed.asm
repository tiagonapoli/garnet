; Assembly listing for method Tsavorite.core.DisasmDriver:OpResume() (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 3 single block inlinees; 7 inlinees without PGO data

G_M000_IG01:
       push     r15
       push     r14
       push     r13
       push     rdi
       push     rsi
       push     rbp
       push     rbx
       sub      rsp, 32
 
G_M000_IG02:
       test     byte  ptr [(reloc)], 1
       je       G_M000_IG23
 
G_M000_IG03:
       mov      rcx, 0xD1FFAB1E
       mov      rbx, gword ptr [rcx]
       cmp      byte  ptr [rbx], bl
       mov      rcx, qword ptr GS:[0x0058]
       mov      rcx, qword ptr [rcx+0x50]
       cmp      dword ptr [rcx+0x238], 3
       jle      G_M000_IG24
       mov      rcx, gword ptr [rcx+0x240]
       mov      rax, bword ptr [rcx+0x18]
       test     rax, rax
       je       G_M000_IG24
 
G_M000_IG04:
       mov      rax, gword ptr [rax+0x10]
       movsxd   rcx, dword ptr [rbx+0x48]
       lea      rsi, bword ptr [rax+4*rcx+0x08]
       mov      rdi, qword ptr [rbx+0x30]
       mov      rax, qword ptr GS:[0x0058]
       mov      rbp, qword ptr [rax+0x50]
       add      rbp, 632
       cmp      dword ptr [rbp+0x10], 0
       jne      SHORT G_M000_IG07
 
G_M000_IG05:
       call     System.Environment:get_CurrentManagedThreadId():int
       mov      dword ptr [rbp+0x10], eax
       mov      ecx, dword ptr [rbp+0x10]
       mov      eax, ecx
       shr      eax, 16
       xor      eax, ecx
       imul     ecx, eax, 0xD1FFAB1E
       mov      eax, ecx
       shr      eax, 13
       xor      eax, ecx
       imul     r14d, eax, 0xD1FFAB1E
       mov      r15d, r14d
       shr      r15d, 16
       xor      r15d, r14d
       lea      r14, [rbp+0x14]
       test     byte  ptr [(reloc)], 1
       je       G_M000_IG25
 
G_M000_IG06:
       mov      r13, 0xD1FFAB1E
       movzx    rcx, word  ptr [r13]
       mov      eax, r15d
       xor      edx, edx
       div      edx:eax, ecx
       inc      edx
       mov      word  ptr [r14], dx
       mov      eax, r15d
       shr      eax, 16
       xor      edx, edx
       div      edx:eax, ecx
       inc      edx
       mov      word  ptr [rbp+0x16], dx
 
G_M000_IG07:
       movzx    rax, word  ptr [rbp+0x14]
       mov      dword ptr [rsi], eax
       mov      eax, dword ptr [rsi]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       jne      SHORT G_M000_IG09
 
G_M000_IG08:
       mov      rcx, rdx
       add      rcx, qword ptr [rbx+0x28]
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rcx], rdi
       test     rax, rax
       jne      SHORT G_M000_IG09
       mov      rax, qword ptr [rbx+0x28]
       mov      ecx, dword ptr [rbp+0x10]
       mov      dword ptr [rax+rdx+0x08], ecx
       jmp      G_M000_IG19
       align    [0 bytes for IG12]
 
G_M000_IG09:
       movzx    rax, word  ptr [rbp+0x14]
       movzx    rcx, word  ptr [rbp+0x16]
       mov      word  ptr [rbp+0x14], cx
       mov      word  ptr [rbp+0x16], ax
       mov      dword ptr [rsi], ecx
       mov      eax, dword ptr [rsi]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       jne      SHORT G_M000_IG10
       add      rcx, rdx
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rcx], rdi
       test     rax, rax
       jne      SHORT G_M000_IG10
       mov      rcx, qword ptr [rbx+0x28]
       mov      eax, dword ptr [rbp+0x10]
       mov      dword ptr [rcx+rdx+0x08], eax
       jmp      G_M000_IG19
 
G_M000_IG10:
       xor      r14d, r14d
       test     byte  ptr [(reloc)], 1
       je       G_M000_IG26
 
G_M000_IG11:
       mov      r13, 0xD1FFAB1E
       movzx    rax, word  ptr [r13]
       add      eax, eax
       test     eax, eax
       jle      SHORT G_M000_IG17
 
G_M000_IG12:
       inc      word  ptr [rbp+0x14]
       movzx    rax, word  ptr [rbp+0x14]
       movzx    rcx, word  ptr [r13]
       cmp      eax, ecx
       jle      SHORT G_M000_IG14
 
G_M000_IG13:
       sub      eax, ecx
       mov      word  ptr [rbp+0x14], ax
 
G_M000_IG14:
       movzx    rax, word  ptr [rbp+0x14]
       mov      dword ptr [rsi], eax
       mov      eax, dword ptr [rsi]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       jne      SHORT G_M000_IG16
 
G_M000_IG15:
       add      rcx, rdx
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rcx], rdi
       test     rax, rax
       je       SHORT G_M000_IG18
 
G_M000_IG16:
       inc      r14d
       movzx    rdx, word  ptr [r13]
       add      edx, edx
       cmp      r14d, edx
       jl       SHORT G_M000_IG12
 
G_M000_IG17:
       xor      ecx, ecx
       mov      dword ptr [rsi], ecx
       mov      rcx, rbx
       mov      rdx, rsi
       call     [Tsavorite.core.LightEpoch:ReserveEntryWait(byref):this]
       jmp      SHORT G_M000_IG19
 
G_M000_IG18:
       mov      rcx, qword ptr [rbx+0x28]
       mov      eax, dword ptr [rbp+0x10]
       mov      dword ptr [rcx+rdx+0x08], eax
 
G_M000_IG19:
       cmp      dword ptr [rbx+0x44], 0
       jle      SHORT G_M000_IG21
 
G_M000_IG20:
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rcx, dword ptr [rsi]
       shl      rcx, 6
       mov      rdx, qword ptr [rdx+rcx]
       mov      rcx, rbx
       call     [Tsavorite.core.LightEpoch:Drain(long):this]
 
G_M000_IG21:
       nop      
 
G_M000_IG22:
       add      rsp, 32
       pop      rbx
       pop      rbp
       pop      rsi
       pop      rdi
       pop      r13
       pop      r14
       pop      r15
       ret      
 
G_M000_IG23:
       mov      rcx, 0xD1FFAB1E
       call     CORINFO_HELP_GET_GCSTATIC_BASE
       jmp      G_M000_IG03
 
G_M000_IG24:
       mov      ecx, 3
       call     CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR_OPTIMIZED
       jmp      G_M000_IG04
 
G_M000_IG25:
       mov      rcx, 0xD1FFAB1E
       call     CORINFO_HELP_GET_NONGCSTATIC_BASE
       jmp      G_M000_IG06
 
G_M000_IG26:
       mov      rcx, 0xD1FFAB1E
       call     CORINFO_HELP_GET_NONGCSTATIC_BASE
       jmp      G_M000_IG11
 
; Total bytes of code 636

; Assembly listing for method Tsavorite.core.DisasmDriver:OpProtectAndDrain() (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 1 single block inlinees; 1 inlinees without PGO data

G_M000_IG01:
       push     rbx
       sub      rsp, 32
 
G_M000_IG02:
       mov      rcx, 0xD1FFAB1E
       mov      rbx, gword ptr [rcx]
       mov      rcx, qword ptr GS:[0x0058]
       mov      rcx, qword ptr [rcx+0x50]
       cmp      dword ptr [rcx+0x238], 3
       jle      SHORT G_M000_IG09
       mov      rcx, gword ptr [rcx+0x240]
       mov      rax, bword ptr [rcx+0x18]
       test     rax, rax
       je       SHORT G_M000_IG09
 
G_M000_IG03:
       mov      rdx, gword ptr [rax+0x10]
       movsxd   rcx, dword ptr [rbx+0x48]
       lea      rdx, bword ptr [rdx+4*rcx+0x08]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rax, dword ptr [rdx]
       shl      rax, 6
       mov      r8, qword ptr [rbx+0x30]
       mov      qword ptr [rcx+rax], r8
       cmp      dword ptr [rbx+0x44], 0
       jle      SHORT G_M000_IG05
 
G_M000_IG04:
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, dword ptr [rdx]
       shl      rdx, 6
       mov      rdx, qword ptr [rcx+rdx]
       mov      rcx, rbx
       call     [Tsavorite.core.LightEpoch:Drain(long):this]
 
G_M000_IG05:
       cmp      dword ptr [rbx+0x40], 0
       jle      SHORT G_M000_IG07
 
G_M000_IG06:
       mov      rcx, rbx
       call     [Tsavorite.core.LightEpoch:SuspendResume():this]
 
G_M000_IG07:
       nop      
 
G_M000_IG08:
       add      rsp, 32
       pop      rbx
       ret      
 
G_M000_IG09:
       mov      ecx, 3
       call     CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR_OPTIMIZED
       jmp      SHORT G_M000_IG03
 
; Total bytes of code 152

; Assembly listing for method Tsavorite.core.LightEpoch:BumpCurrentEpoch():long:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 2 single block inlinees; 0 inlinees without PGO data

G_M000_IG01:
       push     rbx
       sub      rsp, 32
 
G_M000_IG02:
       lea      rdx, bword ptr [rcx+0x30]
       mov      eax, 1
       mov      rbx, rax
       lock     
       xadd     qword ptr [rdx], rbx
       inc      rbx
       cmp      dword ptr [rcx+0x44], 0
       jg       SHORT G_M000_IG04
 
G_M000_IG03:
       mov      rdx, rbx
       call     [Tsavorite.core.LightEpoch:ComputeNewSafeToReclaimEpoch(long):long:this]
       jmp      SHORT G_M000_IG05
 
G_M000_IG04:
       mov      rdx, rbx
       call     [Tsavorite.core.LightEpoch:Drain(long):this]
 
G_M000_IG05:
       mov      rax, rbx
 
G_M000_IG06:
       add      rsp, 32
       pop      rbx
       ret      
 
; Total bytes of code 60

; Assembly listing for method Tsavorite.core.LightEpoch:ComputeNewSafeToReclaimEpoch(long):long:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; fully interruptible
; No PGO data

G_M000_IG01:
 
G_M000_IG02:
       mov      eax, 1
       mov      r8, qword ptr [rcx+0x28]
       align    [0 bytes for IG03]
 
G_M000_IG03:
       movsxd   r10, eax
       shl      r10, 6
       mov      r10, qword ptr [r8+r10]
       test     r10, r10
       je       SHORT G_M000_IG05
 
G_M000_IG04:
       cmp      r10, rdx
       jge      SHORT G_M000_IG05
       mov      rdx, r10
 
G_M000_IG05:
       inc      eax
       cmp      eax, 128
       jle      SHORT G_M000_IG03
 
G_M000_IG06:
       lea      rax, [rdx-0x01]
       mov      qword ptr [rcx+0x38], rax
 
G_M000_IG07:
       ret      
 
; Total bytes of code 51

; Assembly listing for method Tsavorite.core.DisasmDriver:OpSuspend() (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; FullOpts code
; optimized code
; rsp based frame
; fully interruptible
; No PGO data
; 0 inlinees with PGO data; 8 single block inlinees; 11 inlinees without PGO data

G_M000_IG01:
       push     r14
       push     rdi
       push     rsi
       push     rbp
       push     rbx
       sub      rsp, 32
 
G_M000_IG02:
       mov      rcx, 0xD1FFAB1E
       mov      rbx, gword ptr [rcx]
       mov      rcx, qword ptr GS:[0x0058]
       mov      rcx, qword ptr [rcx+0x50]
       cmp      dword ptr [rcx+0x238], 3
       jle      G_M000_IG31
       mov      rcx, gword ptr [rcx+0x240]
       mov      rax, bword ptr [rcx+0x18]
       test     rax, rax
       je       G_M000_IG31
 
G_M000_IG03:
       mov      rsi, gword ptr [rax+0x10]
       add      rsi, 8
       movsxd   rcx, dword ptr [rbx+0x48]
       lea      rcx, bword ptr [rsi+4*rcx]
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rax, dword ptr [rcx]
       shl      rax, 6
       xor      r8d, r8d
       mov      dword ptr [rdx+rax+0x08], r8d
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rax, dword ptr [rcx]
       shl      rax, 6
 
G_M000_IG04:
       mov      qword ptr [rdx+rax], r8
 
G_M000_IG05:
       mov      dword ptr [rcx], r8d
       cmp      dword ptr [rbx+0x40], 0
       jle      SHORT G_M000_IG07
 
G_M000_IG06:
       mov      rcx, gword ptr [rbx+0x10]
       cmp      byte  ptr [rcx], cl
       mov      edx, 1
       call     [System.Threading.SemaphoreSlim:Release(int):int:this]
 
G_M000_IG07:
       cmp      dword ptr [rbx+0x44], 0
       jle      G_M000_IG30
 
G_M000_IG08:
       mov      rax, qword ptr GS:[0x0058]
       mov      rdi, qword ptr [rax+0x50]
       add      rdi, 632
       jmp      G_M000_IG27
       align    [0 bytes for IG15]
 
G_M000_IG09:
       movsxd   rax, dword ptr [rbx+0x48]
       lea      rbp, bword ptr [rsi+4*rax]
       mov      r14, qword ptr [rbx+0x30]
       cmp      dword ptr [rdi+0x10], 0
       jne      SHORT G_M000_IG11
 
G_M000_IG10:
       call     System.Environment:get_CurrentManagedThreadId():int
       mov      dword ptr [rdi+0x10], eax
       mov      eax, dword ptr [rdi+0x10]
       mov      ecx, eax
       shr      ecx, 16
       xor      ecx, eax
       imul     eax, ecx, 0xD1FFAB1E
       mov      ecx, eax
       shr      ecx, 13
       xor      ecx, eax
       imul     eax, ecx, 0xD1FFAB1E
       mov      ecx, eax
       shr      ecx, 16
       xor      ecx, eax
       mov      eax, ecx
       and      eax, 127
       inc      eax
       mov      word  ptr [rdi+0x14], ax
       shr      ecx, 16
       and      ecx, 127
       inc      ecx
       mov      word  ptr [rdi+0x16], cx
 
G_M000_IG11:
       movzx    rax, word  ptr [rdi+0x14]
       mov      dword ptr [rbp], eax
       mov      eax, dword ptr [rbp]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       jne      SHORT G_M000_IG13
 
G_M000_IG12:
       mov      rcx, rdx
       add      rcx, qword ptr [rbx+0x28]
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rcx], r14
       test     rax, rax
       jne      SHORT G_M000_IG13
       mov      rax, qword ptr [rbx+0x28]
       mov      ecx, dword ptr [rdi+0x10]
       mov      dword ptr [rax+rdx+0x08], ecx
       jmp      G_M000_IG22
 
G_M000_IG13:
       movzx    rax, word  ptr [rdi+0x14]
       movzx    rcx, word  ptr [rdi+0x16]
       mov      word  ptr [rdi+0x14], cx
       mov      word  ptr [rdi+0x16], ax
       mov      dword ptr [rbp], ecx
       mov      eax, dword ptr [rbp]
       mov      rcx, qword ptr [rbx+0x28]
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       jne      SHORT G_M000_IG14
       add      rcx, rdx
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rcx], r14
       test     rax, rax
       jne      SHORT G_M000_IG14
       mov      rax, qword ptr [rbx+0x28]
       mov      ecx, dword ptr [rdi+0x10]
       mov      dword ptr [rax+rdx+0x08], ecx
       jmp      SHORT G_M000_IG22
 
G_M000_IG14:
       mov      ecx, 256
 
G_M000_IG15:
       inc      word  ptr [rdi+0x14]
       movzx    rax, word  ptr [rdi+0x14]
       cmp      eax, 128
       jle      SHORT G_M000_IG17
 
G_M000_IG16:
       add      eax, -128
       mov      word  ptr [rdi+0x14], ax
 
G_M000_IG17:
       movzx    rax, word  ptr [rdi+0x14]
       mov      dword ptr [rbp], eax
       mov      eax, dword ptr [rbp]
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   r8, eax
       shl      r8, 6
       cmp      qword ptr [rdx+r8], 0
       jne      SHORT G_M000_IG19
 
G_M000_IG18:
       add      rdx, r8
       xor      eax, eax
       lock     
       cmpxchg  qword ptr [rdx], r14
       test     rax, rax
       je       SHORT G_M000_IG21
 
G_M000_IG19:
       dec      ecx
       jne      SHORT G_M000_IG15
 
G_M000_IG20:
       xor      ecx, ecx
       mov      dword ptr [rbp], ecx
       mov      rcx, rbx
       mov      rdx, rbp
       call     [Tsavorite.core.LightEpoch:ReserveEntryWait(byref):this]
       jmp      SHORT G_M000_IG22
       align    [13 bytes for IG28]
 
G_M000_IG21:
       mov      rcx, qword ptr [rbx+0x28]
       mov      edx, dword ptr [rdi+0x10]
       mov      dword ptr [rcx+r8+0x08], edx
 
G_M000_IG22:
       cmp      dword ptr [rbx+0x44], 0
       jle      SHORT G_M000_IG24
 
G_M000_IG23:
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rcx, dword ptr [rbp]
       shl      rcx, 6
       mov      rdx, qword ptr [rdx+rcx]
       mov      rcx, rbx
       call     [Tsavorite.core.LightEpoch:Drain(long):this]
 
G_M000_IG24:
       movsxd   rcx, dword ptr [rbx+0x48]
       lea      rcx, bword ptr [rsi+4*rcx]
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rax, dword ptr [rcx]
       shl      rax, 6
       xor      r8d, r8d
       mov      dword ptr [rdx+rax+0x08], r8d
       mov      rdx, qword ptr [rbx+0x28]
       movsxd   rax, dword ptr [rcx]
       shl      rax, 6
 
G_M000_IG25:
       mov      qword ptr [rdx+rax], r8
 
G_M000_IG26:
       mov      dword ptr [rcx], r8d
       cmp      dword ptr [rbx+0x40], 0
       jle      SHORT G_M000_IG27
       mov      rcx, gword ptr [rbx+0x10]
       cmp      byte  ptr [rcx], cl
       mov      edx, 1
       call     [System.Threading.SemaphoreSlim:Release(int):int:this]
 
G_M000_IG27:
       cmp      dword ptr [rbx+0x44], 0
       jle      SHORT G_M000_IG30
       lock     
       or       dword ptr [rsp], 0
       mov      eax, 1
       mov      rcx, qword ptr [rbx+0x28]
       cmp      qword ptr [rcx+0x40], 0
       jne      SHORT G_M000_IG30
 
G_M000_IG28:
       inc      eax
       cmp      eax, 128
       jg       G_M000_IG09
 
G_M000_IG29:
       movsxd   rdx, eax
       shl      rdx, 6
       cmp      qword ptr [rcx+rdx], 0
       je       SHORT G_M000_IG28
 
G_M000_IG30:
       add      rsp, 32
       pop      rbx
       pop      rbp
       pop      rsi
       pop      rdi
       pop      r14
       ret      
 
G_M000_IG31:
       mov      ecx, 3
       call     CORINFO_HELP_GETDYNAMIC_GCTHREADSTATIC_BASE_NOCTOR_OPTIMIZED
       jmp      G_M000_IG03
 
; Total bytes of code 693

driver done: 501 125750

