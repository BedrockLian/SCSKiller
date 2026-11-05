// Adapted from AnimeStudio 68700c7d885a68a444886bd4b7f7827e77789e12 (MIT).
// See ThirdParty/AnimeStudio-LICENSE.txt. No game DLL is loaded.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static SCSKiller.Core.Unity.HoyoCryptoTables;

namespace SCSKiller.Core.Unity;
internal static class HoyoCrypto
{
    public static Span<byte> Mr0k(Span<byte> data)
    {
        if (!data.StartsWith("mr0k"u8))
            return data;
        if (data.Length < 0x94)
            throw new InvalidDataException("Truncated mr0k block");
        byte[] key1 = data.Slice(4, 16).ToArray(), key2 = data.Slice(0x74, 16).ToArray(), key3 = data.Slice(0x84, 16).ToArray();
        for (int i = 0; i < 16; i++)
            key2[i] ^= Mr0kInitVector[i];
        Mr0kAes.Decrypt(key1, Mr0kExpansionKey);
        Mr0kAes.Decrypt(key3, Mr0kExpansionKey);
        for (int i = 0; i < 16; i++)
            key1[i] ^= key3[i];
        key1.CopyTo(data.Slice(0x84, 16));
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(key2), b = BinaryPrimitives.ReadUInt64LittleEndian(key3);
        ulong seed = b ^ a ^ (a + (uint)data.Length - 20);
        int count = Math.Min(16 * ((data.Length - 0x94) >> 7), 0x400);
        for (int i = 0; i < count; i++)
            data[0x94 + i] ^= (byte)((byte)(seed >> (8 * (i % 8))) ^ Mr0kBlockKey[i % Mr0kBlockKey.Length]);
        return data[20..];
    }

    public static void Mhy1(Span<byte> bytes, bool header)
    {
        int size = Math.Min(bytes.Length, 128), entry = header ? 28 : 8, rounded = (entry + 15) / 16 * 16;
        if (size < 20)
            throw new InvalidDataException("Truncated mhy1 encrypted prefix");
        Chunk(bytes.Slice(4, 16));
        if (!bytes.Slice(4, 8).SequenceEqual("mhynewec"u8))
            throw new InvalidDataException("mhy1 prefix key/signature mismatch");
        if (size <= 35)
            return;
        Chunk(bytes.Slice(20, 16));
        byte[] seed = bytes[..16].ToArray(), data = bytes.Slice(20, 16).ToArray();
        using var aes = Aes.Create();
        aes.Key = seed;
        aes.EncryptEcb(data, data, PaddingMode.None);
        data.CopyTo(bytes[20..]);
        for (int i = 0; i < 4; i++)
            bytes[i] ^= data[i];
        Rc4(bytes.Slice(20 + rounded, size - 20 - rounded), bytes.Slice(20, 8), bytes.Slice(28, 8));
    }

    static void Chunk(Span<byte> bytes)
    {
        Span<byte> next = stackalloc byte[16];
        for (int round = 0; round < 3; round++)
        {
            for (int j = 0; j < 16; j++)
            {
                int a = GIMhyMul[j % 8], b = bytes[GIMhyShiftRow[(2 - round) * 16 + j] % 16];
                int product = a == 0 || b == 0 ? 0 : BlbTables.GF256Exp[(BlbTables.GF256Log[a] + BlbTables.GF256Log[b]) % 255];
                next[j] = (byte)(GIMhyKey[j % 8] ^ GISBox[(j % 4 * 256) | product]);
            }

            next.CopyTo(bytes);
        }
    }

    static void Rc4(Span<byte> bytes, ReadOnlySpan<byte> key, ReadOnlySpan<byte> operation)
    {
        byte[] state = (byte[])BlbTables.Blb3RC4Key.Clone();
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) % 256;
            (state[i], state[j]) = (state[j], state[i]);
        }

        int x = 0;
        j = 0;
        for (int at = 0; at < bytes.Length; at++)
        {
            x = (x + 1) % 256;
            j = (j + state[x]) % 256;
            (state[x], state[j]) = (state[j], state[x]);
            byte k = state[(state[x] + state[j]) % 256];
            switch (operation[x % operation.Length] % 3)
            {
                case 0:
                    bytes[at] ^= k;
                    break;
                case 1:
                    bytes[at] -= k;
                    break;
                case 2:
                    bytes[at] += k;
                    break;
            }
        }
    }
}
