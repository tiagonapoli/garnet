// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Garnet.server
{
    /// <summary>Hard-link helpers for immutable RangeIndex snapshot files.</summary>
    internal static class RangeIndexFileLinks
    {
        /// <summary>Deletes <paramref name="path"/> if present, so a later write cannot reach another hard link to the same file.</summary>
        public static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>Replaces <paramref name="linkPath"/> with a hard link to <paramref name="existingPath"/>.</summary>
        /// <returns><c>false</c> if the link could not be created (missing source, different volume, unsupported file system).</returns>
        public static bool TryReplaceWithHardLink(string existingPath, string linkPath)
        {
            DeleteIfExists(linkPath);
            try
            {
                return OperatingSystem.IsWindows() ? CreateHardLinkW(linkPath, existingPath, IntPtr.Zero) : link(existingPath, linkPath) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Replaces <paramref name="destinationPath"/> with a byte copy of the immutable snapshot <paramref name="sourcePath"/>.</summary>
        /// <returns><c>false</c> if the source is gone or the copy failed; no partial destination is left behind.</returns>
        public static bool TryReplaceWithCopy(string sourcePath, string destinationPath)
        {
            DeleteIfExists(destinationPath);
            try
            {
                File.Copy(sourcePath, destinationPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteIfExists(destinationPath);
                return false;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}