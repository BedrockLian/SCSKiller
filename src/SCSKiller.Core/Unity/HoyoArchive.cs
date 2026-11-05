using System.Buffers.Binary;
using System.Text;
using CUE4Parse.Compression;
using SCSKiller.Core.App;
using static SCSKiller.Core.Unity.UnityFiles;

namespace SCSKiller.Core.Unity;
internal sealed record HoyoNode(long Container, string Name, long Size, ReadAt Read);
internal interface IHoyoArchive : IDisposable
{
    IEnumerable<HoyoNode> Nodes(CancellationToken ct);
    HoyoNode? NodeAt(long container, string name);
}

internal sealed class GenshinArchive(string path) : IHoyoArchive
{
    readonly GenshinBlb archive = new(path);
    public IEnumerable<HoyoNode> Nodes(CancellationToken ct)
    {
        foreach (var h in archive.Headers(ct))
            foreach (var n in h.Nodes)
                yield return new(h.Offset, n.Name, n.Size, (off, count) => archive.Read(h, n, off, count));
    }

    public HoyoNode? NodeAt(long container, string name)
    {
        var h = archive.HeaderAt(container);
        var n = h.Nodes.FirstOrDefault(n => n.Name == name);
        return n == null ? null : new(container, name, n.Size, (off, count) => archive.Read(h, n, off, count));
    }

    public void Dispose() => archive.Dispose();
}

/// <summary>Lazy ENCR (Star Rail) and mhy1 (Zenless) containers. Every source stream is read-only; at most 16 decoded
/// blocks/32 MiB are cached, except that one larger block is retained to avoid repeatedly decoding it.</summary>
internal sealed class HoyoArchive(string path, bool zenless) : IHoyoArchive
{
    sealed record Node(string Name, long Offset, long Size);
    sealed record Block(long Offset, int Compressed, int Size, int Compression);
    sealed record Header(long Offset, long Size, Block[] Blocks, long[] Starts, Node[] Nodes);
    readonly FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    readonly Dictionary<(long, int), byte[]> cache = new();
    readonly Queue<(long, int)> order = new();
    long cachedBytes;
    public IEnumerable<HoyoNode> Nodes(CancellationToken ct)
    {
        for (long offset = 0; offset < file.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var h = HeaderAt(offset);
            foreach (var n in h.Nodes)
                yield return Source(h, n);
            offset += h.Size;
            if (zenless)
                while (offset < file.Length && At(offset, 1)[0] == 0)
                    offset++;
        }
    }

    public HoyoNode? NodeAt(long container, string name)
    {
        var h = HeaderAt(container);
        var n = h.Nodes.FirstOrDefault(n => n.Name == name);
        return n == null ? null : Source(h, n);
    }

    HoyoNode Source(Header h, Node n) => new(h.Offset, n.Name, n.Size, (off, count) => Read(h, n, off, count));
    Header HeaderAt(long offset)
    {
        var prefix = At(offset, zenless ? 8 : 25);
        bool unity = !zenless && prefix.AsSpan().StartsWith("UnityFS\0"u8);
        if (zenless && !prefix.AsSpan().StartsWith("mhy1"u8) || !zenless && !unity && !prefix.AsSpan().StartsWith("ENCR\0"u8))
            throw new NotSupportedException($"Unsupported resource signature in {Path.GetFileName(path)} at {offset}: {Convert.ToHexString(prefix.AsSpan(0, 4))}");
        int cmeta, umeta, flags;
        long size, data;
        byte[] metadata;
        if (zenless)
        {
            cmeta = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
            if (cmeta is < 55 or > 16 << 20)
                throw new InvalidDataException("Invalid mhy1 header length");
            var bytes = At(offset + 8, cmeta);
            HoyoCrypto.Mhy1(bytes, true);
            var mr = new Cursor(bytes[48..], false);
            umeta = mr.MhyUInt();
            if (umeta is < 1 or > 64 << 20)
                throw new InvalidDataException("Invalid mhy1 metadata size");
            var packed = bytes.AsSpan(55);
            flags = packed[0] == 0x8c ? 9 : 3;
            metadata = Decode(packed, umeta, flags, false);
            data = offset + 8 + cmeta;
            size = 0;
        }
        else
        {
            int headerSize = 25;
            if (unity)
            {
                var header = new Cursor(At(offset, (int)Math.Min(512, file.Length - offset)), true);
                header.String();
                header.I32();
                header.String();
                header.String();
                size = header.I64();
                cmeta = header.I32();
                umeta = header.I32();
                flags = header.I32();
                headerSize = header.Position;
            }
            else
            {
                size = BinaryPrimitives.ReadInt64BigEndian(prefix.AsSpan(5));
                cmeta = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(13));
                umeta = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(17));
                flags = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(21));
            }

            if (size < headerSize || size > file.Length - offset || cmeta is < 1 or > 16 << 20 || umeta is < 1 or > 64 << 20 || cmeta > size - headerSize)
                throw new InvalidDataException("Invalid ENCR header bounds");
            // This SR fork omits stock Unity 7's 16-byte header alignment, including in data.unity3d.
            long at = (flags & 0x80) != 0 ? offset + size - cmeta : offset + headerSize;
            metadata = Decode(At(at, cmeta), umeta, flags & 63, true);
            data = offset + headerSize + ((flags & 0x80) != 0 ? 0 : cmeta);
            if ((flags & 0x200) != 0)
                data = (data - offset + 15) / 16 * 16 + offset;
        }

        var r = new Cursor(metadata, !zenless);
        var nodes = new List<Node>();
        var blocks = new List<Block>();
        if (zenless)
        {
            int nn = r.MhyInt();
            r.Count(nn, 275);
            for (int i = 0; i < nn; i++)
            {
                string name = r.MhyString();
                r.Byte();
                nodes.Add(new(name, r.MhyInt(), r.MhyUInt()));
            }

            int nb = r.MhyInt();
            r.Count(nb, 13);
            for (int i = 0; i < nb; i++)
            {
                int c = r.MhyInt(), u = r.MhyUInt();
                blocks.Add(new(data, c, u, flags));
                data += c;
            }

            size = data - offset;
        }
        else
        {
            if (unity)
                r.Skip(16);
            int nb = r.I32();
            r.Count(nb, 10);
            for (int i = 0; i < nb; i++)
            {
                int u = r.I32(), c = r.I32(), comp = r.U16() & 63;
                blocks.Add(new(data, c, u, comp));
                data += c;
            }

            int nn = r.I32();
            r.Count(nn, 21);
            for (int i = 0; i < nn; i++)
            {
                long start = r.I64(), length = r.I64();
                r.I32();
                nodes.Add(new(r.String(), start, length));
            }
        }

        if (size <= 0 || size > file.Length - offset || data > offset + size || blocks.Count == 0)
            throw new InvalidDataException("Archive blocks exceed container");
        var starts = new long[blocks.Count];
        long total = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Compressed is < 1 or > 256 << 20 || b.Size is < 1 or > 256 << 20)
                throw new InvalidDataException("Unsupported archive block size");
            starts[i] = total;
            total += b.Size;
        }

        foreach (var n in nodes)
            if (n.Offset < 0 || n.Size < 0 || n.Offset > total || n.Size > total - n.Offset)
                throw new InvalidDataException("Node outside decoded archive");
        return new(offset, size, blocks.ToArray(), starts, nodes.ToArray());
    }

    byte[] Read(Header h, Node node, long offset, int count)
    {
        if (offset < 0 || offset > node.Size || count < 0)
            throw new InvalidDataException("Read outside archive node");
        var bytes = new byte[(int)Math.Min(count, node.Size - offset)];
        for (int done = 0; done < bytes.Length;)
        {
            long pos = node.Offset + offset + done;
            int index = Array.BinarySearch(h.Starts, pos);
            if (index < 0)
                index = ~index - 1;
            var block = BlockAt(h, index);
            int from = (int)(pos - h.Starts[index]), n = Math.Min(bytes.Length - done, block.Length - from);
            block.AsSpan(from, n).CopyTo(bytes.AsSpan(done));
            done += n;
        }

        return bytes;
    }

    byte[] BlockAt(Header h, int index)
    {
        var key = (h.Offset, index);
        if (cache.TryGetValue(key, out var bytes))
            return bytes;
        var b = h.Blocks[index];
        bytes = At(b.Offset, b.Compressed);
        if (zenless)
        {
            HoyoCrypto.Mhy1(bytes, false);
            bytes = Decode(bytes.AsSpan(28), b.Size, b.Compression, false);
        }
        else
            bytes = Decode(bytes, b.Size, b.Compression, true);
        cache.Add(key, bytes);
        order.Enqueue(key);
        cachedBytes += bytes.Length;
        while (order.Count > 1 && (order.Count > 16 || cachedBytes > 32 << 20))
        {
            var old = order.Dequeue();
            cachedBytes -= cache[old].Length;
            cache.Remove(old);
        }

        return bytes;
    }

    static byte[] Decode(ReadOnlySpan<byte> packed, int size, int comp, bool mr0k)
    {
        if (mr0k && comp is 5 or 7)
        {
            var copy = packed.ToArray();
            packed = HoyoCrypto.Mr0k(copy);
        }

        if (comp is 6 or 7 or 9)
        {
            Codecs.Load(zlib: false);
            if (OodleHelper.Instance is not { } oodle)
                throw new InvalidDataException(Codecs.NativeOodle ? Codecs.NoOodleDll : Codecs.NoNativeOodle);
            var result = new byte[size];
            if (oodle.Decompress(packed, result) != size)
                throw new InvalidDataException("Oodle decoded length mismatch");
            return result;
        }

        if (comp == 5)
            comp = 3;
        var decoded = Decompress(packed.ToArray(), size, comp);
        return decoded.Length == size ? decoded : throw new InvalidDataException("Archive decoded length mismatch");
    }

    byte[] At(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > file.Length || count > file.Length - offset)
            throw new InvalidDataException("Archive read outside file");
        file.Position = offset;
        var b = new byte[count];
        file.ReadExactly(b);
        return b;
    }

    public void Dispose()
    {
        file.Dispose();
        cache.Clear();
    }

    sealed class Cursor(byte[] bytes, bool big)
    {
        public int Position => at;

        public void Skip(int n) => Take(n);
        int at;
        ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > bytes.Length - at)
                throw new InvalidDataException("Truncated archive metadata");
            var b = bytes.AsSpan(at, n);
            at += n;
            return b;
        }

        public byte Byte() => Take(1)[0];
        public int I32() => big ? BinaryPrimitives.ReadInt32BigEndian(Take(4)) : BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long I64() => big ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public int U16() => big ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int MhyInt()
        {
            var b = Take(6);
            return b[2] | b[4] << 8 | b[0] << 16 | b[5] << 24;
        }

        public int MhyUInt()
        {
            var b = Take(7);
            uint n = (uint)(b[1] | b[6] << 8 | b[3] << 16 | b[2] << 24);
            return n <= int.MaxValue ? (int)n : throw new InvalidDataException("mhy1 length exceeds supported range");
        }

        public string MhyString()
        {
            var b = Take(261);
            int n = b.IndexOf((byte)0);
            return n >= 0 ? Encoding.UTF8.GetString(b[..n]) : throw new InvalidDataException("Unterminated mhy1 name");
        }

        public string String()
        {
            int n = bytes.AsSpan(at, Math.Min(1025, bytes.Length - at)).IndexOf((byte)0);
            if (n < 0)
                throw new InvalidDataException("Unterminated node name");
            string s = Encoding.UTF8.GetString(Take(n));
            Take(1);
            return s;
        }

        public void Count(int n, int stride)
        {
            if (n < 0 || n > (bytes.Length - at) / stride)
                throw new InvalidDataException("Archive table count exceeds metadata");
        }
    }
}
