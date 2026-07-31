// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// Relies on the DEBUG-only VectorManager test hooks (VectorManager.TestHooks.cs).
#if DEBUG

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using Garnet.common;
using Garnet.server;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using StackExchange.Redis;

namespace Garnet.test
{
    /// <summary>
    /// Shared harness for the standalone Vector Set cleanup-pipeline fixtures. These exercise
    /// <c>VectorManager.WaitForCleanupComplete</c> against a single server using the DEBUG-only
    /// exception-injection seams, so they do not need (and cannot use) the cluster replication
    /// harness — they prove the drain semantics that the cluster full-sync paths depend on.
    /// </summary>
    public abstract class VectorSetCleanupTestBase : TestBase
    {
        /// <summary>Length in bytes of an XB8 vector as written by <see cref="PopulateVectorSet"/>.</summary>
        protected const int VectorDimensions = 75;

        private global::Garnet.GarnetServer server;

        /// <summary>
        /// The VectorManager under test. Fails the test if Vector Set preview was not enabled.
        /// </summary>
        protected VectorManager VectorManager
        {
            get
            {
                var vectorManager = server.Provider.StoreWrapper.DefaultDatabase.VectorManager;
                ClassicAssert.IsNotNull(vectorManager, "VectorManager not initialised — enableVectorSetPreview must be true");

                return vectorManager;
            }
        }

        [SetUp]
        public virtual void Setup()
        {
            TestUtils.DeleteDirectory(TestUtils.MethodTestDir, wait: true);
            server = TestUtils.CreateGarnetServer(TestUtils.MethodTestDir, enableAOF: true, enableVectorSetPreview: true);
            server.Start();
        }

        [TearDown]
        public virtual void TearDown()
        {
            // Never leave an injection armed — it would park the next test's cleanup pipeline.
            ExceptionInjectionHelper.DisableException(ExceptionInjectionType.VectorSet_Pause_In_Native_Index_Drop);
            ExceptionInjectionHelper.DisableException(ExceptionInjectionType.VectorSet_Pause_In_Cleanup_Scan);
            try { server.Dispose(); } catch { }
            TestUtils.OnTearDown();
        }

        /// <summary>
        /// Adds deterministic XB8 elements to a Vector Set. XPREQ8 round-trips exactly through VEMB.
        /// </summary>
        protected static void PopulateVectorSet(IDatabase db, string key, int elements, int seed)
        {
            var elem = new byte[4];
            var data = new byte[VectorDimensions];
            var rand = new Random(seed);
            for (var i = 0; i < elements; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(elem, i);
                rand.NextBytes(data);
                _ = db.Execute("VADD", [key, "XB8", data, elem, "XPREQ8"]);
            }
        }

        /// <summary>
        /// Spin until a background task reaches an observable state, failing with <paramref name="message"/>
        /// if it never does.
        /// </summary>
        protected static void SpinUntil(Func<bool> condition, TimeSpan timeout, string message)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > timeout)
                    Assert.Fail(message);

                Thread.Yield();
            }
        }

        /// <summary>
        /// Arm an injection seam and wait for the background task to park on it. The seam clears its own
        /// flag on arrival, which is the "arrived, parked" signal; re-arming it releases the parked task.
        /// </summary>
        protected static void WaitUntilParked(ExceptionInjectionType injection, string message)
            => SpinUntil(() => !ExceptionInjectionHelper.IsEnabled(injection), TimeSpan.FromSeconds(30), message);
    }
}

#endif
