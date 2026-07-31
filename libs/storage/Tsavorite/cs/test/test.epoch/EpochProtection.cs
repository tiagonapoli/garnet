// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Scopes a protected region to a <c>using</c> block. Most tests here have to stay protected
    /// across their assertions, and an assertion failure must still suspend the thread or the
    /// fixture teardown disposes an epoch that a live entry still announces.
    /// </summary>
    internal static class EpochProtection
    {
        internal static Scope Protected(this LightEpoch epoch)
        {
            epoch.Resume();
            return new Scope(epoch);
        }

        internal readonly struct Scope : IDisposable
        {
            readonly LightEpoch epoch;

            internal Scope(LightEpoch epoch) => this.epoch = epoch;

            public void Dispose() => epoch.Suspend();
        }
    }
}