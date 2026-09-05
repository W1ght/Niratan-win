using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Video;

internal sealed class AniDbEd2kHasher : IAniDbEd2kHasher
{
    internal const int ChunkSize = 9_728_000;

    public async Task<AniDbEd2kHash> HashAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var before = new FileInfo(fullPath);
        if (!before.Exists)
            throw new FileNotFoundException("The video file was not found.", fullPath);

        var chunk = new byte[ChunkSize];
        var digests = new List<byte[]>();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc32 = new Crc32();
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                var read = 0;
                while (read < chunk.Length)
                {
                    var count = await stream.ReadAsync(chunk.AsMemory(read), ct);
                    if (count == 0)
                        break;
                    md5.AppendData(chunk.AsSpan(read, count));
                    sha1.AppendData(chunk.AsSpan(read, count));
                    crc32.Append(chunk.AsSpan(read, count));
                    read += count;
                }

                if (read == 0 && digests.Count > 0)
                    break;
                digests.Add(Md4.ComputeHash(chunk.AsSpan(0, read)));
                if (read < chunk.Length)
                    break;
            }
        }

        var after = new FileInfo(fullPath);
        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            throw new IOException("The video changed while its AniDB hash was being calculated.");

        var digest = digests.Count == 1
            ? digests[0]
            : Md4.ComputeHash(Join(digests));
        return new AniDbEd2kHash(
            Convert.ToHexStringLower(digest),
            after.Length,
            new DateTimeOffset(after.LastWriteTimeUtc, TimeSpan.Zero),
            DateTimeOffset.UtcNow)
        {
            Crc32 = crc32.Value.ToString("x8", CultureInfo.InvariantCulture),
            Md5 = Convert.ToHexStringLower(md5.GetHashAndReset()),
            Sha1 = Convert.ToHexStringLower(sha1.GetHashAndReset()),
        };
    }

    private static byte[] Join(IReadOnlyList<byte[]> values)
    {
        var result = new byte[values.Count * 16];
        for (var index = 0; index < values.Count; index++)
            values[index].CopyTo(result, index * 16);
        return result;
    }

    private sealed class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        private uint _state = uint.MaxValue;

        public uint Value => ~_state;

        public void Append(ReadOnlySpan<byte> input)
        {
            foreach (var value in input)
                _state = Table[(byte)(_state ^ value)] ^ (_state >> 8);
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }

    // RFC 1320 MD4. ED2K hashes each 9,728,000-byte chunk with MD4 and,
    // for multi-chunk files, hashes the concatenated 16-byte chunk digests.
    private static class Md4
    {
        public static byte[] ComputeHash(ReadOnlySpan<byte> input)
        {
            var bitLength = checked((ulong)input.Length * 8);
            var paddingLength = 56 - ((input.Length + 1) & 63);
            if (paddingLength < 0)
                paddingLength += 64;
            var padded = new byte[input.Length + 1 + paddingLength + 8];
            input.CopyTo(padded);
            padded[input.Length] = 0x80;
            BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), bitLength);

            uint a = 0x67452301;
            uint b = 0xefcdab89;
            uint c = 0x98badcfe;
            uint d = 0x10325476;
            Span<uint> x = stackalloc uint[16];
            for (var offset = 0; offset < padded.Length; offset += 64)
            {
                for (var index = 0; index < 16; index++)
                    x[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                        padded.AsSpan(offset + index * 4, 4));
                var aa = a;
                var bb = b;
                var cc = c;
                var dd = d;

                Round1(ref a, b, c, d, x[0], 3); Round1(ref d, a, b, c, x[1], 7);
                Round1(ref c, d, a, b, x[2], 11); Round1(ref b, c, d, a, x[3], 19);
                Round1(ref a, b, c, d, x[4], 3); Round1(ref d, a, b, c, x[5], 7);
                Round1(ref c, d, a, b, x[6], 11); Round1(ref b, c, d, a, x[7], 19);
                Round1(ref a, b, c, d, x[8], 3); Round1(ref d, a, b, c, x[9], 7);
                Round1(ref c, d, a, b, x[10], 11); Round1(ref b, c, d, a, x[11], 19);
                Round1(ref a, b, c, d, x[12], 3); Round1(ref d, a, b, c, x[13], 7);
                Round1(ref c, d, a, b, x[14], 11); Round1(ref b, c, d, a, x[15], 19);

                Round2(ref a, b, c, d, x[0], 3); Round2(ref d, a, b, c, x[4], 5);
                Round2(ref c, d, a, b, x[8], 9); Round2(ref b, c, d, a, x[12], 13);
                Round2(ref a, b, c, d, x[1], 3); Round2(ref d, a, b, c, x[5], 5);
                Round2(ref c, d, a, b, x[9], 9); Round2(ref b, c, d, a, x[13], 13);
                Round2(ref a, b, c, d, x[2], 3); Round2(ref d, a, b, c, x[6], 5);
                Round2(ref c, d, a, b, x[10], 9); Round2(ref b, c, d, a, x[14], 13);
                Round2(ref a, b, c, d, x[3], 3); Round2(ref d, a, b, c, x[7], 5);
                Round2(ref c, d, a, b, x[11], 9); Round2(ref b, c, d, a, x[15], 13);

                Round3(ref a, b, c, d, x[0], 3); Round3(ref d, a, b, c, x[8], 9);
                Round3(ref c, d, a, b, x[4], 11); Round3(ref b, c, d, a, x[12], 15);
                Round3(ref a, b, c, d, x[2], 3); Round3(ref d, a, b, c, x[10], 9);
                Round3(ref c, d, a, b, x[6], 11); Round3(ref b, c, d, a, x[14], 15);
                Round3(ref a, b, c, d, x[1], 3); Round3(ref d, a, b, c, x[9], 9);
                Round3(ref c, d, a, b, x[5], 11); Round3(ref b, c, d, a, x[13], 15);
                Round3(ref a, b, c, d, x[3], 3); Round3(ref d, a, b, c, x[11], 9);
                Round3(ref c, d, a, b, x[7], 11); Round3(ref b, c, d, a, x[15], 15);

                a = unchecked(a + aa);
                b = unchecked(b + bb);
                c = unchecked(c + cc);
                d = unchecked(d + dd);
            }

            var result = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), a);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), b);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), c);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), d);
            return result;
        }

        private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
        private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
        private static uint H(uint x, uint y, uint z) => x ^ y ^ z;
        private static uint Rotate(uint value, int count) =>
            value << count | value >> (32 - count);

        private static void Round1(ref uint a, uint b, uint c, uint d, uint x, int s) =>
            a = Rotate(unchecked(a + F(b, c, d) + x), s);
        private static void Round2(ref uint a, uint b, uint c, uint d, uint x, int s) =>
            a = Rotate(unchecked(a + G(b, c, d) + x + 0x5a827999), s);
        private static void Round3(ref uint a, uint b, uint c, uint d, uint x, int s) =>
            a = Rotate(unchecked(a + H(b, c, d) + x + 0x6ed9eba1), s);
    }
}
