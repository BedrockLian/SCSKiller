using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace SCSKiller.Core.Unity;
// ShaderPlatformInfos layout: AnimeStudio 68700c7d885a, Shader.cs. See ThirdParty/AnimeStudio-LICENSE.txt.
internal static class StarRailShaderBlob
{
    internal sealed record Info(int Platform, uint[] Offsets, uint[] Compressed, uint[] Decompressed, byte[] Dictionary, ulong[] Mask, byte[] Blob);
    public static IEnumerable<byte[]> Decode(byte[] obj)
    {
        var infos = Find(obj);
        if (infos == null)
            yield break;
        foreach (var info in infos.Where(i => i.Platform == 4))
            for (int i = 0; i < info.Offsets.Length; i++)
            {
                var packed = info.Blob.AsSpan((int)info.Offsets[i], (int)info.Compressed[i]);
                var decoded = new byte[info.Decompressed[i]];
                if (info.Compressed[i] == info.Decompressed[i])
                    packed.CopyTo(decoded);
                else if (LZ4Codec.Decode(packed, decoded, info.Dictionary) != decoded.Length)
                    throw new InvalidDataException("Star Rail shader code decoded length mismatch");
                yield return decoded;
            }
    }

    static List<Info>? Find(byte[] obj)
    {
        for (int at = 0; at + 12 <= obj.Length; at += 4)
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(obj.AsSpan(at));
            if (n is < 1 or > 8)
                continue;
            try
            {
                var r = new Cursor(obj, at);
                var platforms = r.UIntArray(8);
                if (!platforms.Contains(4u) || platforms.Any(p => p > 64) || r.U32() != n)
                    continue;
                var infos = new List<Info>();
                foreach (uint platform in platforms)
                {
                    var infoOffsets = r.UIntArray(65536);
                    var infoLengths = r.UIntArray(65536);
                    r.Bytes();
                    var dictionary = r.Bytes();
                    r.Align();
                    if (infoOffsets.Length != infoLengths.Length)
                        throw new InvalidDataException();
                    var offsets = r.UIntArray(65536);
                    var compressed = r.UIntArray(65536);
                    var decompressed = r.UIntArray(65536);
                    var mask = r.ULongArray();
                    var blob = r.Bytes();
                    r.Align();
                    if (offsets.Length == 0 || offsets.Length != compressed.Length || offsets.Length != decompressed.Length)
                        throw new InvalidDataException();
                    for (int i = 0; i < offsets.Length; i++)
                        if ((long)offsets[i] + compressed[i] > blob.Length || decompressed[i] is 0 or > 64 << 20)
                            throw new InvalidDataException();
                    infos.Add(new((int)platform, offsets, compressed, decompressed, dictionary, mask, blob));
                }

                return infos;
            }
            catch (InvalidDataException)
            {
            }
        }

        return null;
    }

    sealed class Cursor(byte[] bytes, int position)
    {
        int at = position;
        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > bytes.Length - at)
                throw new InvalidDataException();
            var span = bytes.AsSpan(at, n);
            at += n;
            return span;
        }

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public uint[] UIntArray(int maximum)
        {
            uint n = U32();
            if (n > maximum || n > (bytes.Length - at) / 4)
                throw new InvalidDataException();
            var result = new uint[n];
            for (int i = 0; i < n; i++)
                result[i] = U32();
            return result;
        }

        public ulong[] ULongArray()
        {
            uint n = U32();
            if (n > 65536 || n > (bytes.Length - at) / 8)
                throw new InvalidDataException();
            var result = new ulong[n];
            for (int i = 0; i < n; i++)
                result[i] = BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
            return result;
        }

        public byte[] Bytes()
        {
            uint n = U32();
            if (n > bytes.Length - at)
                throw new InvalidDataException();
            return Take((int)n).ToArray();
        }

        public void Align()
        {
            at = (at + 3) & ~3;
            if (at > bytes.Length)
                throw new InvalidDataException();
        }
    }
}
