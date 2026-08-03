// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Instance tracking. Each <see cref="LightEpoch"/> claims an instance id that indexes the
    /// thread-static entry table, so instances must be independent and ids must be returned on
    /// <see cref="LightEpoch.Dispose"/>.
    /// </summary>
    [TestFixture]
    public class InstanceTests : EpochTestBase
    {
        [Test]
        public void ActiveInstanceCountTracksConstructionAndDispose()
        {
            var before = LightEpoch.ActiveInstanceCount();

            using (var first = new LightEpoch())
            using (var second = new LightEpoch())
                Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(before + 2));

            Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(before), "Dispose did not return the instance id");
        }

        [Test]
        public void ProtectingOneInstanceLeavesTheOtherUntouched()
        {
            using var first = new LightEpoch();
            using var second = new LightEpoch();

            var secondEpoch = second.CurrentEpoch;

            using (first.ProtectedScope())
            {
                Assert.That(first.ThisInstanceProtected(), Is.True);
                Assert.That(second.ThisInstanceProtected(), Is.False);

                for (var i = 0; i < 4; i++)
                    _ = first.BumpCurrentEpoch();

                Assert.That(second.CurrentEpoch, Is.EqualTo(secondEpoch), "bumping one instance advanced another");
                Assert.That(second.TestHookMinAnnouncedEpoch(), Is.EqualTo(secondEpoch));
            }
        }

        [Test]
        public void OneThreadCanHoldSlotsInSeveralInstancesAtOnce()
        {
            using var first = new LightEpoch();
            using var second = new LightEpoch();

            using (first.ProtectedScope())
            using (second.ProtectedScope())
            {
                Assert.That(first.ThisInstanceProtected(), Is.True);
                Assert.That(second.ThisInstanceProtected(), Is.True);
            }

            Assert.That(first.ThisInstanceProtected(), Is.False);
            Assert.That(second.ThisInstanceProtected(), Is.False);
        }

        [Test]
        public void ARecycledInstanceIdStartsFromCleanThreadStaticState()
        {
            var first = new LightEpoch();
            first.Resume();
            first.Suspend();
            first.Dispose();

            var second = new LightEpoch();
            try
            {
                Assert.That(second.ThisInstanceProtected(), Is.False, "a recycled instance id left stale thread-static state behind");

                using (second.ProtectedScope())
                    Assert.That(second.ThisInstanceProtected(), Is.True);
            }
            finally
            {
                second.Dispose();
            }
        }

        [Test]
        public void ExhaustingInstanceSlotsThrows()
        {
            var created = new List<LightEpoch>();
            try
            {
                InvalidOperationException caught = null;
                try
                {
                    while (created.Count <= LightEpoch.TestHookMaxInstanceCount)
                        created.Add(new LightEpoch());
                }
                catch (InvalidOperationException e)
                {
                    caught = e;
                }

                Assert.That(caught, Is.Not.Null, "creating more than TestHookMaxInstanceCount instances must throw");
                Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(LightEpoch.TestHookMaxInstanceCount));
            }
            finally
            {
                foreach (var instance in created)
                    instance.Dispose();
            }

            using var afterwards = new LightEpoch();
            afterwards.Resume();
            afterwards.Suspend();
        }
    }
}
