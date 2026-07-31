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
    public class InstanceTests
    {
        [Test]
        public void ActiveInstanceCountTracksConstructionAndDispose()
        {
            var before = LightEpoch.ActiveInstanceCount();

            var first = new LightEpoch();
            var second = new LightEpoch();
            try
            {
                Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(before + 2));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(before), "Dispose did not return the instance id");
        }

        [Test]
        public void ProtectingOneInstanceLeavesTheOtherUntouched()
        {
            var first = new LightEpoch();
            var second = new LightEpoch();
            try
            {
                var secondEpoch = second.CurrentEpoch;

                using (first.Protected())
                {
                    Assert.That(first.ThisInstanceProtected(), Is.True);
                    Assert.That(second.ThisInstanceProtected(), Is.False);

                    for (var i = 0; i < 4; i++)
                        _ = first.BumpCurrentEpoch();

                    Assert.That(second.CurrentEpoch, Is.EqualTo(secondEpoch), "bumping one instance advanced another");
                    Assert.That(second.MinAnnouncedEpoch(), Is.EqualTo(secondEpoch));
                }
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void OneThreadCanHoldSlotsInSeveralInstancesAtOnce()
        {
            var first = new LightEpoch();
            var second = new LightEpoch();
            try
            {
                using (first.Protected())
                using (second.Protected())
                {
                    Assert.That(first.ThisInstanceProtected(), Is.True);
                    Assert.That(second.ThisInstanceProtected(), Is.True);
                }

                Assert.That(first.ThisInstanceProtected(), Is.False);
                Assert.That(second.ThisInstanceProtected(), Is.False);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
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

                using (second.Protected())
                {
                    Assert.That(second.ThisInstanceProtected(), Is.True);
                }
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
                    while (created.Count <= LightEpoch.MaxInstanceCount)
                        created.Add(new LightEpoch());
                }
                catch (InvalidOperationException e)
                {
                    caught = e;
                }

                Assert.That(caught, Is.Not.Null, "creating more than MaxInstanceCount instances must throw");
                Assert.That(LightEpoch.ActiveInstanceCount(), Is.EqualTo(LightEpoch.MaxInstanceCount));
            }
            finally
            {
                foreach (var instance in created)
                    instance.Dispose();
            }

            var afterwards = new LightEpoch();
            try
            {
                afterwards.Resume();
                afterwards.Suspend();
            }
            finally
            {
                afterwards.Dispose();
            }
        }
    }
}