// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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

        /// <summary>Test hook deciding whether a link from the given existing path may be created; null in production.</summary>
        internal static Func<string, bool> CanLinkForTest;

        /// <summary>Test hook run before <see cref="Copy"/> reads its source; null in production.</summary>
        internal static Action<string> BeforeCopyForTest;

        private static long linkFailures;

        /// <summary>
        /// Gets the number of hard links that could not be created. Checkpoint and data directories on different mounts (even of
        /// one file system) fail every link between them with EXDEV, turning each into a full copy.
        /// </summary>
        public static long LinkFailures => Interlocked.Read(ref linkFailures);

        /// <summary>Replaces <paramref name="linkPath"/> with a hard link to <paramref name="existingPath"/>.</summary>
        /// <returns><c>false</c> if the link could not be created (missing source, different volume, unsupported file system).</returns>
        public static bool TryReplaceWithHardLink(string existingPath, string linkPath)
        {
            DeleteIfExists(linkPath);
            bool linked;
            try
            {
                linked = CanLinkForTest?.Invoke(existingPath) != false && (OperatingSystem.IsWindows() ? CreateHardLinkW(linkPath, existingPath, IntPtr.Zero) : link(existingPath, linkPath) == 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                linked = false;
            }

            if (!linked)
                _ = Interlocked.Increment(ref linkFailures);
            return linked;
        }

        /// <summary>Bytes written between forced syncs of a <see cref="Copy"/> destination.</summary>
        internal const int SyncIntervalBytes = 64 << 20;

        private const int CopyBufferBytes = 4 << 20;

        /// <summary>
        /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, forcing the written data to disk every
        /// <see cref="SyncIntervalBytes"/>. A plain copy of a 1 GB tree leaves it all dirty in the page cache; a few concurrent
        /// copies exceed the cgroup's dirty limit, and the kernel then throttles every writing thread in the process (AOF,
        /// logging, replica replay) until writeback catches up, which stalled a replica for 60 s.
        /// </summary>
        /// <param name="sourcePath">The file to copy.</param>
        /// <param name="destinationPath">The file to create. With <paramref name="overwrite"/>, an existing file is unlinked first,
        /// so the copy never writes through a hard link it shares with an immutable snapshot.</param>
        /// <param name="overwrite">Whether to replace an existing destination; otherwise an existing destination throws <see cref="IOException"/>.</param>
        /// <exception cref="FileNotFoundException">The source does not exist. No partial destination is left behind on any failure.</exception>
        public static void Copy(string sourcePath, string destinationPath, bool overwrite)
        {
            BeforeCopyForTest?.Invoke(sourcePath);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.SequentialScan);
            if (overwrite)
                DeleteIfExists(destinationPath);

            var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                using (destination)
                {
                    long unsynced = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, CopyBufferBytes)) > 0)
                    {
                        destination.Write(buffer, 0, read);
                        unsynced += read;
                        if (unsynced >= SyncIntervalBytes)
                        {
                            destination.Flush(flushToDisk: true);
                            unsynced = 0;
                        }
                    }

                    destination.Flush(flushToDisk: true);
                }
            }
            catch
            {
                DeleteIfExists(destinationPath);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Replaces <paramref name="destinationPath"/> with a byte copy of the immutable snapshot <paramref name="sourcePath"/>.</summary>
        /// <returns><c>false</c> if the source is gone or the copy failed; no partial destination is left behind.</returns>
        public static bool TryReplaceWithCopy(string sourcePath, string destinationPath)
        {
            try
            {
                Copy(sourcePath, destinationPath, overwrite: true);
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