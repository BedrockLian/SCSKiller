using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Unity;
using Xunit;

namespace SCSKiller.Tests;

public sealed class GenshinReaderTests
{
    [Fact]
    public void Detects_verified_layout_without_UnityPlayer()
    {
        using var f = new Install();
        var engine = new UnityReader().Detect(f.Game);
        Assert.NotNull(engine);
        Assert.Equal("Unity", engine.Family);
        Assert.Equal("Genshin-BLB3", engine.Fork);
        Assert.Null(engine.Unsupported);
        Assert.Equal("D3D11", engine.GraphicsApi);
        Assert.True(engine.ApiGuessed);
    }

    [Fact]
    public void Does_not_admit_an_unrelated_executable_without_UnityPlayer()
    {
        using var f = new Install();
        Assert.Null(new UnityReader().Detect(f.Game with { ExePath = Path.Combine(f.Root, "Other.exe") }));
    }

    [Fact]
    public void Indexes_raw_objects_and_keeps_SM5_and_SM6_in_separate_maps()
    {
        using var f = new Install();
        var index = new UnityReader().Index(f.Game, f.Engine, null, default);
        Assert.Equal(2, index.Shaders.Count);
        Assert.Equal(2, index.Maps.Count);
        Assert.All(index.Maps, map => Assert.All(map.Shaders, sha =>
            Assert.Equal(index.Shaders[sha].ShaderModel.Contains("_6_") ? "PCD3D_SM6" : "PCD3D_SM5", map.Platform)));
    }

    [Fact]
    public void Materializes_each_requested_hash_once_and_rejects_changed_bytes()
    {
        using var f = new Install();
        var reader = new UnityReader();
        var index = reader.Index(f.Game, f.Engine, null, default);
        Assert.Equal(2, index.Shaders.Count);
        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(f.Game, f.Engine, index.Shaders.Keys.ToHashSet(), (sha, code) => got.Add(sha, code), default);
        Assert.Equal(2, got.Count);
        Assert.All(got, item => Assert.Equal(item.Key, Convert.ToHexStringLower(SHA1.HashData(item.Value))));
        File.WriteAllBytes(f.Assets, Serialized((48, new byte[256])));
        got.Clear();
        reader.ReadShaders(f.Game, f.Engine, index.Shaders.Keys.ToHashSet(), (sha, code) => got.Add(sha, code), default);
        Assert.Empty(got);
    }

    [Fact]
    public void Rejects_bad_container_checksums()
    {
        using var f = new Install();
        var bad = Shader(false); bad[4] ^= 1;
        File.WriteAllBytes(f.Assets, Serialized((48, bad)));
        Assert.Empty(new UnityReader().Index(f.Game, f.Engine, null, default).Shaders);
    }

    [Fact]
    public void Cancelled_scan_does_not_start_reading()
    {
        using var f = new Install();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new UnityReader().Index(f.Game, f.Engine, null, cts.Token));
    }

    [Fact]
    public void Missing_source_does_not_emit_stale_shader_bytes()
    {
        using var f = new Install(); var reader = new UnityReader();
        var index = reader.Index(f.Game, f.Engine, null, default);
        File.Delete(f.Assets);
        reader.ReadShaders(f.Game, f.Engine, index.Shaders.Keys.ToHashSet(), (_, _) => Assert.Fail("Stale source emitted"), default);
    }

    [Fact]
    public void Cancellation_at_final_progress_is_observed()
    {
        using var f = new Install(); using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<(int Done, int Total)>(p => { if (p.Done == p.Total) cts.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => new UnityReader().Index(f.Game, f.Engine, null, progress, cts.Token));
    }

    internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void Counts_files_without_a_progress_callback()
    {
        using var f = new Install(); var lines = new List<string>();
        new UnityReader().Index(f.Game, f.Engine, new InlineProgress<string>(lines.Add), default);
        Assert.Contains("2 files", lines.Last());
    }

    internal static byte[] Shader(bool dxil)
    {
        byte[] program = new byte[dxil ? 24 : 8];
        BinaryPrimitives.WriteUInt32LittleEndian(program, dxil ? 0x60u : 0x50u);
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(4), (uint)program.Length / 4);
        return Dxbc.Container(dxil ? "DXIL"u8 : "SHDR"u8, program);
    }

    internal static byte[] Serialized(params (int ClassId, byte[] Bytes)[] objects)
    {
        using var meta = new MemoryStream(); using var w = new BinaryWriter(meta);
        w.Write(Encoding.UTF8.GetBytes("2017.4.30f1\0")); w.Write(19); w.Write((byte)0); w.Write(objects.Length);
        foreach (var obj in objects) { w.Write(obj.ClassId); w.Write((byte)0); w.Write((short)-1); w.Write(new byte[16]); }
        w.Write(objects.Length); uint offset = 0;
        for (int i = 0; i < objects.Length; i++)
        {
            while (meta.Position % 4 != 0) w.Write((byte)0);
            w.Write((long)i + 1); w.Write(offset); w.Write(objects[i].Bytes.Length); w.Write(i); offset += (uint)objects[i].Bytes.Length;
        }
        int dataAt = 20 + (int)meta.Length; byte[] result = new byte[dataAt + offset];
        void Be(int at, int value) => BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at), value);
        Be(0, (int)meta.Length); Be(4, result.Length); Be(8, 17); Be(12, dataAt);
        meta.ToArray().CopyTo(result, 20);
        foreach (var obj in objects) { obj.Bytes.CopyTo(result, dataAt); dataAt += obj.Bytes.Length; }
        return result;
    }

    internal sealed class Install : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "installs", Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "YuanShen_Data");
        public string Assets => Path.Combine(Data, "globalgamemanagers.assets");
        public Game Game => new("test:genshin", "Synthetic Genshin fixture", Store.Manual, Root, Path.Combine(Root, "YuanShen.exe"));
        public EngineInfo Engine => new("Unity", "2017.4.30f1", "Genshin-BLB3", "D3D11", false, null);
        public Install()
        {
            Directory.CreateDirectory(Path.Combine(Data, "StreamingAssets", "AssetBundles", "blocks"));
            File.WriteAllBytes(Game.ExePath, []);
            File.WriteAllBytes(Path.Combine(Data, "globalgamemanagers"), Serialized((141, new byte[] { 2, 0, 0, 0, 2, 0, 0, 0, 18, 0, 0, 0 })));
            File.WriteAllBytes(Assets, Serialized((48, new byte[13].Concat(Shader(false)).Concat(Shader(true)).ToArray())));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
