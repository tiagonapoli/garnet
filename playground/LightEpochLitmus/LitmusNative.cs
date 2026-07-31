// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tsavorite.epoch.litmus
{
    /// <summary>
    /// The two OS primitives the litmus tests need: whole-page allocation that can be truly
    /// unmapped (so a later access faults), and best-effort thread-to-core pinning.
    /// </summary>
    internal static unsafe class LitmusNative
    {
        const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_RW = 0x04;
        [DllImport("kernel32", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr a, nuint s, uint t, uint p);
        [DllImport("kernel32", SetLastError = true)] static extern bool VirtualFree(IntPtr a, nuint s, uint t);
        [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr m);

        const int PROT_READ = 0x1, PROT_WRITE = 0x2;
        const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20;
        [DllImport("libc", SetLastError = true, EntryPoint = "mmap")] static extern IntPtr LinuxMmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);
        [DllImport("libc", SetLastError = true, EntryPoint = "munmap")] static extern int LinuxMunmap(IntPtr addr, nuint length);
        [DllImport("libc", SetLastError = true, EntryPoint = "sched_setaffinity")] static extern int LinuxSchedSetAffinity(int pid, nuint cpuSetSize, ulong* mask);

        /// <summary>Bytes in the kernel cpu_set_t passed to sched_setaffinity (1024 CPUs).</summary>
        const int CpuSetBytes = 128;

        /// <summary>Whether page unmapping and core pinning are available on this platform.</summary>
        internal static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        /// <summary>
        /// Allocate a standalone reservation, committed and readable/writable. <see cref="Unmap"/>
        /// removes it from the process page tables, which requires the base address of a whole
        /// VirtualAlloc reservation on Windows — so each page must come from its own call rather
        /// than be carved out of a larger block.
        /// </summary>
        internal static byte* MapPage(nuint bytes)
        {
            if (OperatingSystem.IsWindows())
            {
                var pointer = VirtualAlloc(IntPtr.Zero, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_RW);
                if (pointer == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAlloc failed.");
                return (byte*)pointer;
            }

            var mapped = LinuxMmap(IntPtr.Zero, bytes, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
            if (mapped == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "mmap failed.");

            return (byte*)mapped;
        }

        /// <summary>Fully unmap the region; any subsequent access to it faults.</summary>
        internal static void Unmap(byte* p, nuint bytes)
        {
            if (OperatingSystem.IsWindows())
            {
                // MEM_RELEASE requires dwSize to be 0 and releases the whole reservation, so the
                // range leaves the page tables and a later access raises STATUS_ACCESS_VIOLATION.
                // If a later VirtualAlloc happens to reuse the range the access silently succeeds
                // instead, which can only hide a violation, never manufacture one.
                if (!VirtualFree((IntPtr)p, 0, MEM_RELEASE))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualFree failed.");

                return;
            }

            if (LinuxMunmap((IntPtr)p, bytes) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "munmap failed.");
        }

        /// <summary>
        /// Pin the calling thread to <paramref name="core"/>. Best effort: containers and CI
        /// agents often restrict affinity, and a failure only adds jitter to the race window
        /// rather than invalidating a violation.
        /// </summary>
        internal static bool TryPin(int core)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if ((uint)core >= UIntPtr.Size * 8)
                        return false;

                    return SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << core)) != UIntPtr.Zero;
                }

                if (!OperatingSystem.IsLinux() || (uint)core >= CpuSetBytes * 8)
                    return false;

                var cpuMask = stackalloc ulong[CpuSetBytes / sizeof(ulong)];
                for (var i = 0; i < CpuSetBytes / sizeof(ulong); i++)
                    cpuMask[i] = 0;

                cpuMask[core / 64] = 1UL << (core % 64);

                // pid 0 means the calling thread: every Linux thread is a task with its own mask.
                return LinuxSchedSetAffinity(0, CpuSetBytes, cpuMask) == 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
