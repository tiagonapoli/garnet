// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;
using System.IO.Compression;
using Garnet.cluster;
using NUnit.Framework;

namespace Garnet.test.cluster
{
    [TestFixture]
    public class RangeIndexChunkCodecTests
    {
        [Test]
        public void CompressibleChunkIsDeflated()
        {
            var chunk = new byte[RangeIndexFileDataSource.DefaultChunkSize];
            for (var i = 0; i < chunk.Length; i++)
                chunk[i] = (byte)(i % 61 < 40 ? 0 : i % 7);
            var frame = new byte[chunk.Length + 1];

            var length = RangeIndexFileTransmitSource.EncodeChunk(chunk, frame);

            Assert.That(frame[0], Is.EqualTo((byte)RangeIndexFileDataSource.ChunkCodec.Deflate));
            Assert.That(length, Is.LessThan(chunk.Length / 4));
            using var inflater = new DeflateStream(new MemoryStream(frame, 1, length - 1), CompressionMode.Decompress);
            var decoded = new byte[chunk.Length];
            inflater.ReadExactly(decoded);
            Assert.That(decoded, Is.EqualTo(chunk));
            Assert.That(inflater.Read(new byte[1]), Is.Zero);
        }

        [Test]
        public void IncompressibleChunkIsSentRaw()
        {
            var chunk = new byte[RangeIndexFileDataSource.DefaultChunkSize];
            new Random(7).NextBytes(chunk);
            var frame = new byte[chunk.Length + 1];

            var length = RangeIndexFileTransmitSource.EncodeChunk(chunk, frame);

            Assert.That(frame[0], Is.EqualTo((byte)RangeIndexFileDataSource.ChunkCodec.Raw));
            Assert.That(length, Is.EqualTo(chunk.Length + 1));
            Assert.That(frame.AsSpan(1).SequenceEqual(chunk), Is.True);
        }
    }
}