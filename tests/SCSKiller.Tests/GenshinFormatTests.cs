using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.Unity;
using Xunit;

namespace SCSKiller.Tests;

public sealed class GenshinFormatTests
{
    [Fact]
    public void Object_scan_finds_cross_window_magic_header_and_payload_once()
    {
        var shader = GenshinReaderTests.Shader(false);
        foreach (int offset in new[] { 0, 1, 30, 31, 32, 60, 63, 64 })
        {
            byte[] bytes = new byte[offset].Concat(shader).Concat(new byte[13]).ToArray();
            var found = GenshinReader.Containers((at, n) => bytes.AsSpan((int)at, n).ToArray(), 0, bytes.Length, default, 32).ToArray();
            Assert.Single(found); Assert.Equal(offset, found[0].Offset); Assert.Equal(shader, found[0].Code);
        }
    }

    [Fact]
    public void Object_scan_never_reaches_into_the_next_object()
    {
        var shader = GenshinReaderTests.Shader(false);
        Assert.Empty(GenshinReader.Containers((at, n) => shader.AsSpan((int)at, n).ToArray(), 0, shader.Length - 1, default, 32));
    }

    [Fact]
    public void Header_uses_its_declared_container_size_not_the_physical_file_size()
    {
        var bytes = Header();
        var h = GenshinBlb.ParseHeader(bytes, new byte[16], 1000, 5000);
        Assert.Equal(1000, h.Offset); Assert.Equal(256, h.Size);
        Assert.Equal(1000 + 28 + bytes.Length, h.Blocks[0].Offset);
        Assert.Equal("CAB-test", h.Nodes[0].Name);
        Put(bytes, 56, 200); // Fits the physical file, but exceeds this container's payload.
        Assert.Throws<InvalidDataException>(() => GenshinBlb.ParseHeader(bytes, new byte[16], 1000, 5000));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(4, 262145)]
    [InlineData(24, int.MaxValue)]
    [InlineData(28, int.MaxValue)]
    [InlineData(60, 262145)]
    [InlineData(64, 262145)]
    public void Rejects_inconsistent_counts_lengths_and_node_ranges(int at, int value)
    {
        var bytes = Header(); Put(bytes, at, value);
        Assert.Throws<InvalidDataException>(() => GenshinBlb.ParseHeader(bytes, new byte[16], 0, 256));
    }

    [Fact]
    public void Rejects_overflowing_relative_pointers()
    {
        var bytes = Header(); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), long.MaxValue);
        Assert.Throws<InvalidDataException>(() => GenshinBlb.ParseHeader(bytes, new byte[16], 0, 256));
    }

    [Fact]
    public void Index_stamp_tracks_new_and_updated_persistent_packages()
    {
        using var f = new GenshinReaderTests.Install(); IEngineReader reader = new UnityReader();
        var first = reader.IndexStamp(f.Game, f.Engine);
        string dir = Path.Combine(f.Data, "Persistent", "AssetBundles", "blocks"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "patch.blk"); File.WriteAllBytes(path, new byte[10]);
        var added = reader.IndexStamp(f.Game, f.Engine); Assert.NotEqual(first, added);
        File.WriteAllBytes(path, new byte[11]); Assert.NotEqual(added, reader.IndexStamp(f.Game, f.Engine));
    }

    [Fact]
    public void Standard_Unity_still_requires_UnityPlayer_and_keeps_its_reader()
    {
        using var f = new GenshinReaderTests.Install();
        string exe = Path.Combine(f.Root, "Other.exe"); File.WriteAllBytes(exe, []);
        var game = f.Game with { ExePath = exe };
        Assert.Null(UnityReader.Find(game));
        File.WriteAllBytes(Path.Combine(f.Root, "UnityPlayer.dll"), []);
        Assert.NotNull(UnityReader.Find(game));
        Assert.Null(new UnityReader().Detect(game)!.Fork);
    }

    static byte[] Header()
    {
        var bytes = new byte[96]; Put(bytes, 0, 256); Put(bytes, 4, 262144); bytes[20] = 9; bytes[21] = 18;
        Put(bytes, 24, 1); Put(bytes, 28, 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), 24); // block table at 56
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), 20); // node table at 60
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), 28); // flags at 76
        Put(bytes, 56, 64); Put(bytes, 60, 0); Put(bytes, 64, 262144);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(68), 12); // name at 80
        "CAB-test\0"u8.CopyTo(bytes.AsSpan(80)); return bytes;
    }

    static void Put(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
}
