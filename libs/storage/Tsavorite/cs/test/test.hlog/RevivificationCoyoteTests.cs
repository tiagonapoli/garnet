// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Tsavorite.core;

namespace Tsavorite.test.LogRecordTests
{
    static class RevivificationCoyoteTests
    {
        [Test]
        public static async Task PhysicalWalkerPreservesRecordBoundary()
        {
            var scenario = new RevivificationScenario();
            var preparationComplete = new TaskCompletionSource<bool>();

            var revivifier = Task.Run(() =>
            {
                scenario.PrepareForRevivification();
                preparationComplete.SetResult(true);
                SchedulingPoint.Interleave();
                scenario.InitializeReplacement();
            });

            var walker = Task.Run(async () =>
            {
                await preparationComplete.Task;
                Specification.Assert(scenario.AllocatedSize == scenario.OriginalAllocatedSize,
                    $"Physical walker advanced {scenario.AllocatedSize} bytes instead of {scenario.OriginalAllocatedSize}.");
            });

            await Task.WhenAll(revivifier, walker);
        }

        sealed unsafe class RevivificationScenario
        {
            const int KeyLength = 10;
            const int ValueLength = 8;
            const int ExtendedNamespaceLength = 34;
            const int ExplicitFillerLength = 32;

            readonly byte[] recordBuffer = GC.AllocateArray<byte>(512, pinned: true);
            readonly long physicalAddress;
            RecordSizeInfo replacementSizeInfo;

            internal RevivificationScenario()
            {
                physicalAddress = (long)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(recordBuffer));
                var oldSizeInfo = CreateSizeInfo(ExtendedNamespaceLength);
                OriginalAllocatedSize = LogRecord.GetExpectedIORecordSize(KeyLength, ValueLength, ExtendedNamespaceLength) + ExplicitFillerLength;
                oldSizeInfo.AllocatedInlineRecordSize = OriginalAllocatedSize;
                replacementSizeInfo = CreateSizeInfo(extendedNamespaceLength: 0);

                var dataHeader = default(RecordDataHeader);
                _ = dataHeader.Initialize(in oldSizeInfo, out _, out _, out _, physicalAddress);
                var logRecord = new LogRecord(physicalAddress);
                logRecord.InitializeHeadersForNewRecord(inNewVersion: false, previousAddress: 64);
                logRecord.SetDataHeader(dataHeader);
            }

            internal int OriginalAllocatedSize { get; }

            internal int AllocatedSize => new LogRecord(physicalAddress).AllocatedSize;

            internal void PrepareForRevivification() => new LogRecord(physicalAddress).PrepareForRevivification(ref replacementSizeInfo);

            internal void InitializeReplacement()
            {
                Span<byte> key = stackalloc byte[KeyLength];
                key.Fill(0x42);
                var logRecord = new LogRecord(physicalAddress);
                logRecord.InitializeHeadersForNewRecord(inNewVersion: false, previousAddress: 64);
                logRecord.InitializeRecord(TestSpanByteKey.FromPinnedSpan(key), in replacementSizeInfo);
            }

            static RecordSizeInfo CreateSizeInfo(int extendedNamespaceLength)
            {
                var sizeInfo = new RecordSizeInfo { FieldInfo = new() { KeySize = KeyLength, ValueSize = ValueLength, ExtendedNamespaceSize = extendedNamespaceLength } };
                sizeInfo.SetKeyIsInline();
                sizeInfo.SetValueIsInline();
                sizeInfo.MaxInlineValueSize = ValueLength;
                sizeInfo.CalculateSizes(KeyLength, ValueLength);
                return sizeInfo;
            }
        }
    }
}
