// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading;

namespace Garnet.server
{
    /// <summary>Why the checkpoint started on the current async flow was requested, for checkpoint diagnostics.</summary>
    internal static class CheckpointReason
    {
        static readonly AsyncLocal<string> current = new();

        public static string Current => current.Value;

        /// <summary>Sets the trigger until the returned scope is disposed.</summary>
        public static Scope Set(string trigger)
        {
            var previous = current.Value;
            current.Value = trigger;
            return new Scope(previous);
        }

        public readonly struct Scope(string previous) : IDisposable
        {
            public void Dispose() => current.Value = previous;
        }
    }
}
