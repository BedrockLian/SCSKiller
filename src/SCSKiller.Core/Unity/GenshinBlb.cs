using System.Buffers.Binary;
using System.Text;
using CUE4Parse.Compression;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unity;

/// <summary>A physical .blk file contains consecutive BLB3 containers. Headers are streamed; only requested data
/// blocks are decrypted/decoded. No target code is loaded and the file is opened for reading only.</summary>
internal sealed class GenshinBlb(string path) : IDisposable
{
    internal sealed record Node(string Name, long Offset, long Size);
    internal sealed record Block(long Offset, int Compressed, int Uncompressed);
    internal sealed record Header(long Offset, long Size, int Compression, int BlockSize, byte[] Seed, Block[] Blocks, Node[] Nodes);

    readonly FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    readonly Dictionary<(long, int), byte[]> cache = new();
    readonly Queue<(long, int)> order = new();
    public long BlocksDecoded { get; private set; }

    public IEnumerable<Header> Headers(CancellationToken ct)
    {
        for (long offset = 0; offset < file.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var header = HeaderAt(offset);
            yield return header;
            offset += header.Size;
        }
    }

    public Header HeaderAt(long offset)
    {
        if (offset < 0 || offset > file.Length - 28) throw new InvalidDataException("BLB3 prefix outside file");
        var prefix = At(offset, 28);
        if (!prefix.AsSpan().StartsWith("Blb\x03"u8)) throw new InvalidDataException($"Not BLB3 at {offset}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (size is < 56 or > 16 << 20 || size > file.Length - offset - 28) throw new InvalidDataException("BLB3 header size outside file");
        var seed = prefix[12..28]; var bytes = At(offset + 28, size);
        BlbCrypto.Decrypt(seed, bytes);
        return ParseHeader(bytes, seed, offset, file.Length);
    }

    // Separate from decryption so structural rejection can be tested without shipping game bytes.
    internal static Header ParseHeader(byte[] bytes, byte[] seed, long offset, long fileLength)
    {
        if (bytes.Length < 56 || seed.Length != 16 || offset < 0 || offset > fileLength) throw new InvalidDataException("Truncated BLB3 metadata");
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        int Relative(int at)
        {
            long delta = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
            if (delta < -at || delta > bytes.Length - at) throw new InvalidDataException("BLB3 relative pointer outside header");
            return (int)(at + delta);
        }
        long size = U(0), dataOffset = 28L + bytes.Length;
        int compression = bytes[20], power = bytes[21], count = I(24), nodeCount = I(28);
        if (size < dataOffset || size > fileLength - offset || compression is not (3 or 9) || power > 24)
            throw new InvalidDataException("Unsupported or out-of-bounds BLB3 container");
        int blockSize = 1 << power, blockAt = Relative(32), nodeAt = Relative(40), flagsAt = Relative(48);
        if (count < 1 || nodeCount < 1 || count > (bytes.Length - blockAt) / 4 || nodeCount > (bytes.Length - nodeAt) / 16
            || flagsAt > bytes.Length - 4 || U(4) is 0 || U(4) > blockSize)
            throw new InvalidDataException("BLB3 tables outside header");
        var blocks = new Block[count]; long previous = 0;
        for (int i = 0; i < count; i++)
        {
            long end = U(blockAt + 4 * i), compressed = end - previous;
            if (compressed is <= 0 or > 64 << 20 || end > size - dataOffset) throw new InvalidDataException("BLB3 block outside container");
            blocks[i] = new(offset + dataOffset + previous, (int)compressed, i == count - 1 ? (int)U(4) : blockSize);
            previous = end;
        }
        long decodedSize = (count - 1L) * blockSize + U(4);
        var nodes = new Node[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            int at = nodeAt + 16 * i, start = I(at), length = I(at + 4), nameAt = Relative(at + 8);
            if (start < 0 || length < 0 || length > decodedSize - start) throw new InvalidDataException("BLB3 node outside decoded blocks");
            int nameSize = bytes.AsSpan(nameAt, Math.Min(513, bytes.Length - nameAt)).IndexOf((byte)0);
            if (nameSize is <= 0 or > 512) throw new InvalidDataException("Invalid BLB3 node name");
            nodes[i] = new(Encoding.UTF8.GetString(bytes, nameAt, nameSize), start, length);
        }
        return new(offset, size, compression, blockSize, seed, blocks, nodes);
    }

    public byte[] Read(Header header, Node node, long offset, int count)
    {
        if (offset < 0 || offset > node.Size || count < 0) throw new InvalidDataException("Read outside BLB3 node");
        var result = new byte[(int)Math.Min(count, node.Size - offset)];
        for (int done = 0; done < result.Length;)
        {
            long at = node.Offset + offset + done;
            int index = (int)(at / header.BlockSize), from = (int)(at % header.BlockSize);
            var block = Decode(header, index);
            int n = Math.Min(result.Length - done, block.Length - from);
            block.AsSpan(from, n).CopyTo(result.AsSpan(done)); done += n;
        }
        return result;
    }

    byte[] Decode(Header header, int index)
    {
        var key = (header.Offset, index);
        if (cache.TryGetValue(key, out var ready)) return ready;
        var entry = header.Blocks[index]; var bytes = At(entry.Offset, entry.Compressed);
        if (header.Compression != 9 || bytes.Length > 6) BlbCrypto.Decrypt(header.Seed, bytes);
        if (!(index > 0 && entry.Compressed == entry.Uncompressed))
        {
            if (header.Compression == 3) bytes = UnityFiles.Decompress(bytes, entry.Uncompressed, 3);
            else
            {
                Codecs.Load(zlib: false);
                if (OodleHelper.Instance is not { } native) throw new InvalidDataException(Codecs.NativeOodle ? Codecs.NoOodleDll : Codecs.NoNativeOodle);
                var decoded = new byte[entry.Uncompressed];
                if (native.Decompress(bytes, decoded) != decoded.Length) throw new InvalidDataException("BLB3 Oodle block doesn't decode to its declared size");
                bytes = decoded;
            }
        }
        BlocksDecoded++;
        cache.Add(key, bytes); order.Enqueue(key);
        if (order.Count > 16) cache.Remove(order.Dequeue());
        return bytes;
    }

    byte[] At(long offset, int count)
    {
        file.Position = offset; var bytes = new byte[count]; file.ReadExactly(bytes); return bytes;
    }

    public void Dispose() { file.Dispose(); cache.Clear(); }
}
