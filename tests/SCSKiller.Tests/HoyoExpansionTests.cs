using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unity;
using Xunit;

namespace SCSKiller.Tests;
public sealed class HoyoExpansionTests
{
    [Theory]
    [InlineData("StarRail", "Asb", ".block", "ENCR", "StarRail-ENCR")]
    [InlineData("ZenlessZoneZero", "Blocks", ".blk", "mhy1", "Zenless-mhy1")]
    public void Detection_checks_past_an_unsupported_first_resource(string exe, string folder, string extension, string signature, string fork)
    {
        using var fixture = new GenshinReaderTests.Install();
        var game = fixture.Game with { ExePath = Path.Combine(fixture.Root, exe + ".exe") };
        File.WriteAllBytes(game.ExePath, []);
        File.WriteAllBytes(Path.Combine(fixture.Root, "UnityPlayer.dll"), []);
        string data = Path.Combine(fixture.Root, exe + "_Data"), blocks = Path.Combine(data, "StreamingAssets", folder);
        Directory.CreateDirectory(blocks);
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"), GenshinReaderTests.Serialized((141, new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 })));
        File.WriteAllBytes(Path.Combine(blocks, "a" + extension), []);
        File.WriteAllBytes(Path.Combine(blocks, "b" + extension), []);
        var paths = SCSKiller.Core.Games.GameFiles.EnumerateFiles(blocks, "*" + extension,
            new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).ToArray();
        File.WriteAllBytes(paths[0], "BBL\0\x01\0\0\0"u8.ToArray());
        File.WriteAllBytes(paths[1], Encoding.ASCII.GetBytes(signature + "\0\0\0\0"));

        Assert.Equal(fork, new UnityReader().Detect(game)!.Fork);
    }

    [Fact]
    public void StarRail_code_segments_use_their_external_LZ4_dictionary()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Array(params uint[] values)
        {
            writer.Write(values.Length);
            foreach (var value in values) writer.Write(value);
        }
        void Bytes(byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
            while (stream.Position % 4 != 0) writer.Write((byte)0);
        }
        Array(4);
        writer.Write(1);
        Array();
        Array();
        Bytes([]);
        Bytes("ABCD"u8.ToArray());
        Array(0);
        Array(9);
        Array(13);
        writer.Write(0); // No masks; bytecode decoding is independent of variant selection.
        Bytes([0x04, 4, 0, 0x50, (byte)'x', (byte)'y', (byte)'z', (byte)'a', (byte)'b']);
        Assert.Equal("ABCDABCDxyzab"u8.ToArray(), Assert.Single(StarRailShaderBlob.Decode(stream.ToArray())));
    }

    [Fact]
    public void StarRail_UnityFS_v7_metadata_follows_the_unaligned_header()
    {
        using var fixture = new GenshinReaderTests.Install();
        var cab = GenshinReaderTests.Serialized((72, GenshinReaderTests.Shader(false)));
        string path = Path.Combine(fixture.Root, "data.unity3d");
        File.WriteAllBytes(path, RailUnityFs(cab));
        using var archive = new HoyoArchive(path, false);
        var node = Assert.Single(archive.Nodes(default));
        Assert.Equal(cab, node.Read(0, cab.Length));
        Assert.Equal(cab, archive.NodeAt(0, node.Name)!.Read(0, cab.Length));
    }

    [Fact]
    public void StarRail_indexes_and_reads_an_extensionless_SDK_bundle()
    {
        using var fixture = new RailInstall();
        var reader = new UnityReader();
        var engine = reader.Detect(fixture.Game)!;
        var before = reader.IndexStamp(fixture.Game, engine);
        var code = Dxbc.Container("SHDR"u8, [0x50, 0, 1, 0, 2, 0, 0, 0]);
        var sdk = Path.Combine(fixture.Game.InstallDir, "StarRail_Data", "StreamingAssets", "MiHoYoSDKRes", "PC");
        Directory.CreateDirectory(sdk);
        File.WriteAllBytes(Path.Combine(sdk, "mihoyo_sdk_res"), RailUnityFs(GenshinReaderTests.Serialized((72, code))));

        Assert.NotEqual(before, reader.IndexStamp(fixture.Game, engine));
        AssertIndexedAndRead(reader, fixture.Game, engine, code, Stage.Vertex, "vs_5_0");
    }

    [Theory]
    [InlineData(false, 48, Stage.Pixel, "ps_6_0")]
    [InlineData(true, 825902497, Stage.Library, "lib_6_5")]
    public void Zenless_indexes_and_reads_platform_25_and_ray_libraries(bool ray, int classId, Stage stage, string model)
    {
        using var fixture = new GenshinReaderTests.Install();
        string data = Path.Combine(fixture.Root, "ZenlessZoneZero_Data");
        Directory.CreateDirectory(data);
        var game = fixture.Game with { ExePath = Path.Combine(fixture.Root, "ZenlessZoneZero.exe") };
        File.WriteAllBytes(game.ExePath, []);
        File.WriteAllBytes(Path.Combine(fixture.Root, "UnityPlayer.dll"), []);
        File.Copy(Path.Combine(fixture.Data, "globalgamemanagers"), Path.Combine(data, "globalgamemanagers"));
        var code = ray ? Dxbc.Container("DXIL"u8, [0x65, 0, 6, 0, 6, 0, 0, 0, .. new byte[16]]) : GenshinReaderTests.Shader(true);
        File.WriteAllBytes(Path.Combine(data, "globalgamemanagers.assets"), GenshinReaderTests.Serialized((classId, ray ? code : Platform25Blob(code))));
        var engine = new EngineInfo("Unity", "2019.4.40f1", "Zenless-mhy1", "D3D12", false, null);

        AssertIndexedAndRead(new UnityReader(), game, engine, code, stage, model);
    }

    static void AssertIndexedAndRead(UnityReader reader, Game game, EngineInfo engine, byte[] code, Stage stage, string model)
    {
        var sha = Convert.ToHexStringLower(SHA1.HashData(code));
        var index = reader.Index(game, engine, null, default);
        Assert.True(index.Shaders.TryGetValue(sha, out var shader), $"Missing {model} bytecode");
        Assert.Equal((stage, model), (shader.Stage, shader.ShaderModel));
        var received = new List<byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { sha }, (_, bytes) => received.Add(bytes), default);
        Assert.Equal(code, Assert.Single(received));
    }

    static byte[] Platform25Blob(byte[] code)
    {
        var decoded = new byte[1024];
        code.CopyTo(decoded, 32);
        byte[] packed = new byte[LZ4Codec.MaximumOutputSize(decoded.Length)];
        int length = LZ4Codec.Encode(decoded, packed);
        Assert.InRange(length, 1, decoded.Length - 1);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (int word in new[] { 1, 25, 1, 1, 0, 1, 1, length, 1, 1, decoded.Length, length })
            writer.Write(word);
        writer.Write(packed, 0, length);
        return stream.ToArray();
    }

    static byte[] RailUnityFs(byte[] cab)
    {
        var encr = Encr(cab);
        int tableSize = BinaryPrimitives.ReadInt32BigEndian(encr.AsSpan(13));
        using var stream = new MemoryStream();
        stream.Write("UnityFS\0"u8);
        void Big(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            stream.Write(b);
        }
        Big(7);
        stream.Write("5.x.x\0"u8);
        stream.Write("2019.4.34f1\0"u8);
        long sizeAt = stream.Position;
        stream.Write(new byte[8]);
        Big(tableSize + 16);
        Big(tableSize + 16);
        Big(0x40);
        Assert.NotEqual(0, stream.Position % 16);
        stream.Write(new byte[16]);
        stream.Write(encr.AsSpan(25));
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan((int)sizeAt), bytes.Length);
        return bytes;
    }

    [Fact]
    public void Zenless_BBL_resources_are_explicitly_unsupported()
    {
        using var fixture = new GenshinReaderTests.Install();
        string path = Path.Combine(fixture.Root, "silence.blk");
        File.WriteAllBytes(path, "BBL\0\x01\0\0\0"u8.ToArray());
        using var archive = new HoyoArchive(path, true);
        var error = Assert.Throws<NotSupportedException>(() => archive.Nodes(default).ToArray());
        Assert.Contains("silence.blk", error.Message);
        Assert.Contains("42424C00", error.Message);
    }

    [Fact]
    public void Unity_blob_accepts_more_than_sixteen_valid_segments()
    {
        var code = GenshinReaderTests.Shader(false);
        using var b = new MemoryStream();
        using var w = new BinaryWriter(b);
        w.Write(1);
        w.Write(4);
        for (int a = 0; a < 3; a++)
        {
            w.Write(1);
            w.Write(17);
            for (int i = 0; i < 17; i++)
                w.Write(a == 0 ? i * code.Length : code.Length);
        }

        w.Write(code.Length * 17);
        for (int i = 0; i < 17; i++)
            w.Write(code);
        var segments = UnityFiles.ShaderBlob(b.ToArray());
        Assert.NotNull(segments);
        Assert.Equal(17, segments.Count);
        Assert.All(segments, s => Assert.Equal(code, s));
    }

    [Fact]
    public void StarRail_detection_selects_the_ENCR_reader()
    {
        using var f = new RailInstall();
        Assert.Equal("StarRail-ENCR", new UnityReader().Detect(f.Game)!.Fork);
    }

    [Fact]
    public void StarRail_indexes_consecutive_ENCR_containers_and_rehydrates_them()
    {
        using var f = new RailInstall();
        var reader = new UnityReader();
        var engine = new EngineInfo("Unity", "2019.4.34f1", "StarRail-ENCR", "D3D11", false, null);
        var index = reader.Index(f.Game, engine, null, default);
        Assert.Equal(2, index.Shaders.Count);
        var sent = new HashSet<string>();
        reader.ReadShaders(f.Game, engine, index.Shaders.Keys.ToHashSet(), (sha, bytes) => Assert.True(sent.Add(sha)), default);
        Assert.Equal(2, sent.Count);
    }

    internal sealed class RailInstall : IDisposable
    {
        readonly GenshinReaderTests.Install fixture = new();
        public Game Game => new("test:rail", "Synthetic Star Rail", Store.Manual, fixture.Root, Path.Combine(fixture.Root, "StarRail.exe"));

        public RailInstall()
        {
            File.WriteAllBytes(Game.ExePath, []);
            File.WriteAllBytes(Path.Combine(fixture.Root, "UnityPlayer.dll"), []);
            string data = Path.Combine(fixture.Root, "StarRail_Data"), blocks = Path.Combine(data, "StreamingAssets", "Asb", "Windows");
            Directory.CreateDirectory(blocks);
            File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"), GenshinReaderTests.Serialized((141, new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 })));
            var a = Encr(GenshinReaderTests.Serialized((72, GenshinReaderTests.Shader(false))));
            var b = Encr(GenshinReaderTests.Serialized((72, GenshinReaderTests.Shader(true))));
            File.WriteAllBytes(Path.Combine(blocks, "test.block"), a.Concat(b).ToArray());
        }

        public void Dispose() => fixture.Dispose();
    }

    internal static byte[] Encr(byte[] cab)
    {
        using var meta = new MemoryStream();
        void M(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            meta.Write(b);
        }

        void L(long value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            meta.Write(b);
        }

        M(1);
        M(cab.Length);
        M(cab.Length);
        meta.Write(new byte[2]);
        M(1);
        L(0);
        L(cab.Length);
        M(4);
        meta.Write("CAB-test\0"u8);
        byte[] table = meta.ToArray(), result = new byte[25 + table.Length + cab.Length];
        "ENCR\0"u8.CopyTo(result);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(5), result.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(13), table.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(17), table.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(21), 0x40);
        table.CopyTo(result, 25);
        cab.CopyTo(result, 25 + table.Length);
        return result;
    }
}
