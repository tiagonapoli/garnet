// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using NUnit.Framework;
using Tsavorite.core;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// <see cref="Murmur3"/> exists only because <c>Utility.Murmur3</c> cannot be referenced from
    /// this project without a cycle, so the property that matters is that the copy has not drifted:
    /// the epoch spreads a thread id across the entry table with it, and a divergence would change
    /// slot placement rather than fail loudly.
    /// </summary>
    [TestFixture]
    public class Murmur3Tests
    {
        [Test]
        public void HashMatchesTheUtilityImplementationItDuplicates()
        {
            foreach (var value in new[] { int.MinValue, -1, 0, 1, 2, 42, 1023, 1 << 30, int.MaxValue })
                Assert.That(Murmur3.Hash(value), Is.EqualTo(Utility.Murmur3(value)), $"the copy has drifted for {value}");

            for (var value = 1; value <= 4096; value++)
                Assert.That(Murmur3.Hash(value), Is.EqualTo(Utility.Murmur3(value)));
        }

        [Test]
        public void HashIsDeterministicAndSpreadsSmallInputs()
        {
            Assert.That(Murmur3.Hash(7), Is.EqualTo(Murmur3.Hash(7)));

            // Thread ids handed to the epoch are small and consecutive, so the only thing that keeps
            // them off the same table slot is that the low bits of the hash differ.
            const int Slots = 128;
            var distinct = new HashSet<int>();
            for (var id = 1; id <= Slots; id++)
                _ = distinct.Add(Murmur3.Hash(id) & (Slots - 1));

            Assert.That(distinct.Count, Is.GreaterThan(Slots / 2), "consecutive ids collapsed onto too few slots to be usable for placement");
        }
    }
}