// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Tsavorite.epoch.litmus
{
    /// <summary>
    /// Best-effort detection of running under an emulator.
    ///
    /// <para>This matters more than it looks. An emulator interleaves guest instructions on the
    /// host's memory model, so it does not reproduce the guest architecture's ordering: a
    /// weakly-ordered guest emulated on a strongly-ordered host will simply never exhibit the
    /// reorderings this harness is built to catch. The run comes back clean and looks like
    /// evidence that the epoch is correct on that architecture, when it is evidence of nothing.
    /// The classic way to get burned is <c>docker run --platform linux/arm64</c> on an x86 host,
    /// where binfmt_misc silently routes the image through qemu-aarch64.</para>
    ///
    /// <para>The forced-failure control does not protect against this, because it recycles pages
    /// unconditionally rather than relying on a reordering, so it fires under emulation exactly
    /// as it does on real hardware.</para>
    ///
    /// <para>Detection is heuristic and can only ever be one-sided: a positive is reliable, a
    /// negative is not a guarantee of native execution.</para>
    /// </summary>
    internal static class Emulation
    {
        internal readonly struct Result
        {
            internal bool IsEmulated { get; init; }
            internal string Evidence { get; init; }
        }

        internal static Result Detect()
        {
            // A process architecture that differs from the OS architecture is emulation by
            // definition: x64 under Windows-on-ARM Prism, or an x64 container on Apple silicon.
            if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
                return Emulated($"process is {RuntimeInformation.ProcessArchitecture} on a {RuntimeInformation.OSArchitecture} OS");

            if (!OperatingSystem.IsLinux())
                return default;

            // qemu-user maps its own binary into the guest process, so it shows up in the address
            // space even though uname, /proc/cpuinfo and the process architecture all report the
            // emulated target faithfully.
            if (TryReadFile("/proc/self/maps", out var maps) && maps.Contains("qemu", StringComparison.OrdinalIgnoreCase))
                return Emulated("qemu is mapped into this process (/proc/self/maps)");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QEMU_LD_PREFIX")))
                return Emulated("QEMU_LD_PREFIX is set");

            if (!TryReadFile("/proc/cpuinfo", out var cpuinfo))
                return default;

            if (cpuinfo.Contains("QEMU", StringComparison.Ordinal))
                return Emulated("/proc/cpuinfo reports a QEMU CPU");

            // The check that actually catches `docker run --platform linux/arm64` on an x86 host.
            // qemu-user there reports aarch64 through uname, the process architecture and the
            // cpuinfo feature list, and keeps itself out of /proc/self/maps, so the only thing
            // left that gives it away is the MIDR: every real implementer is registered and
            // non-zero (0x41 ARM, 0x50 Ampere, 0x51 Qualcomm, 0x61 Apple), while TCG synthesises
            // an all-zero one.
            if (TryGetCpuImplementer(cpuinfo, out var implementer) && implementer == 0)
                return Emulated("/proc/cpuinfo reports CPU implementer 0x00, which no real part uses");

            return default;
        }

        static bool TryGetCpuImplementer(string cpuinfo, out int implementer)
        {
            implementer = -1;

            foreach (var line in cpuinfo.Split('\n'))
            {
                if (!line.StartsWith("CPU implementer", StringComparison.Ordinal))
                    continue;

                var colon = line.IndexOf(':');
                if (colon < 0)
                    continue;

                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    value = value[2..];

                return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out implementer);
            }

            return false;
        }

        static Result Emulated(string evidence) => new() { IsEmulated = true, Evidence = evidence };

        static bool TryReadFile(string path, out string contents)
        {
            try
            {
                contents = File.ReadAllText(path);
                return true;
            }
            catch (Exception)
            {
                contents = null;
                return false;
            }
        }
    }
}
