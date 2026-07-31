// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using NUnit.Framework;
using Tsavorite.epoch.litmus;

namespace Tsavorite.test.epoch
{
    /// <summary>
    /// Long-running Store-Buffer litmus tests over <see cref="Tsavorite.core.LightEpoch"/>.
    /// These are the tests that actually exercise the memory ordering the CAS-carried announce
    /// exists for: a reader announces its epoch and dereferences a
    /// page while a reclaimer retires that same page, and the test asserts the epoch never
    /// authorises the free under the live reader.
    ///
    /// Every "no violation" assertion is paired with two guards, because a clean result on its
    /// own is worthless:
    ///
    /// <list type="number">
    /// <item>A vacuity guard, asserting the race window was actually sampled and that the epoch
    /// really did reclaim something. A run that never raced, or never freed, cannot fail
    /// regardless of whether the epoch is correct.</item>
    /// <item>A self-test control in a separate test, which forces the failure condition and
    /// asserts it IS detected. If the control ever passes silently the detector is blind and
    /// the clean verdict next to it is void.</item>
    /// </list>
    ///
    /// <para>Measured power, which is the only thing that makes a green run meaningful: with
    /// the announce reverted to a plain store, this configuration reports 8-11 violations per
    /// 30 s run on a 20-logical-processor x86-64 host, reproducibly. With the CAS-carried
    /// announce it reports none. The configuration is not arbitrary - the deref length and the
    /// absence of any work between the barrier and Resume() were each found to be the
    /// difference between detecting the bug and detecting nothing at all.</para>
    ///
    /// <para>These are <see cref="ExplicitAttribute"/>: they are minute-scale, they pin cores, and
    /// two of them running concurrently (as happens when the suite is multi-targeted) contend for
    /// the same cores, which distorts the very timing the result depends on. They are therefore
    /// opt-in rather than part of the default suite:
    /// <c>dotnet test --filter "TestCategory=Litmus"</c>.
    /// For sustained soaks prefer the standalone <c>Garnet.LightEpoch.litmus</c> CLI (and its
    /// Dockerfile), which runs one configuration per process with the runtime tuned for it.</para>
    /// </summary>
    [TestFixture]
    [Category("Litmus")]
    [Explicit("Minute-scale core-pinned soak; run with --filter \"TestCategory=Litmus\" or use the Garnet.LightEpoch.litmus CLI.")]
    public class LitmusTests
    {
        /// <summary>
        /// Words the reader dereferences per protected region, wrapping over the 4 KiB page. This
        /// is the knob that decides whether the harness can detect anything at all: the reclaimer
        /// only stamps the page from the drain callback, several epoch operations after the
        /// unlink, so a reader that walks 64 words has long left the page by then and the run
        /// reports nothing no matter how long it lasts. This value was picked by measuring
        /// detections against a deliberately unfixed epoch; splitting the budget across several
        /// values was tried and measured worse, because each arm falls below the detection rate.
        /// </summary>
        const int DerefWords = 20_000;

        /// <summary>
        /// How long each main litmus run lasts. Override with LE_LITMUS_SECONDS for a longer
        /// soak; the default is chosen to stay tolerable in a normal test run.
        /// </summary>
        static TimeSpan MainDuration => TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("LE_LITMUS_SECONDS"), out var seconds) && seconds > 0 ? seconds : 30);

        /// <summary>Controls only have to fire once, so they do not need the full soak.</summary>
        static readonly TimeSpan ControlDuration = TimeSpan.FromSeconds(5);

        static LitmusCores RequireCores()
        {
            if (!LitmusNative.IsSupported)
                Assert.Ignore("The litmus harness needs Windows or Linux for page allocation and core pinning.");

            if (!LitmusCores.TrySelect(out var cores))
                Assert.Ignore($"The litmus harness needs at least 4 logical processors to separate the reader from the reclaimer; this machine has {Environment.ProcessorCount}.");

            return cores;
        }

        /// <summary>
        /// The main result: over a sustained run, the epoch never lets a retired page be
        /// recycled while a protected reader is inside it.
        /// </summary>
        [Test]
        public void QuarantineLitmus_NeverRecyclesAPageUnderALiveReader()
        {
            var cores = RequireCores();
            var result = new QuarantineLitmus(MainDuration, DerefWords, cores).Run();
            TestContext.Out.WriteLine($"quarantine litmus: {result} cores({cores})");

            // Vacuity guards first: a clean run only means something if it raced and reclaimed.
            Assert.That(result.SampledRounds, Is.GreaterThan(0),
                "the reader never captured a live page pointer, so the race window was never sampled and this run proves nothing");
            Assert.That(result.Quarantines, Is.GreaterThan(0),
                "the epoch never decided any page was safe to recycle, so this run could not have failed regardless of correctness");

            Assert.That(result.Violations, Is.EqualTo(0),
                $"a protected reader read a recycled page - use-after-free. {result}");
        }

        /// <summary>
        /// Control for <see cref="QuarantineLitmus_NeverRecyclesAPageUnderALiveReader"/>. Recycles
        /// every page unconditionally, as if the epoch had wrongly cleared it on every round. The
        /// detector must report this; if it does not, the clean verdict above is void.
        /// </summary>
        [Test]
        public void QuarantineLitmus_SelfTestProvesTheDetectorIsLive()
        {
            var cores = RequireCores();
            var result = new QuarantineLitmus(ControlDuration, DerefWords, cores, selfTest: true).Run();

            TestContext.Out.WriteLine($"quarantine litmus self-test: {result} cores({cores})");

            Assert.That(result.SampledRounds, Is.GreaterThan(0),
                "the reader never captured a live page pointer, so even the forced failure could not be observed");
            Assert.That(result.Violations, Is.GreaterThan(0),
                "THE DETECTOR IS BLIND: pages were recycled under the reader on every round and nothing was reported, so every clean verdict from the quarantine litmus is void");
        }
    }
}
