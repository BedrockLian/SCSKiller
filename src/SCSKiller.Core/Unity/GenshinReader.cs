using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Unity.UnityFiles;

namespace SCSKiller.Core.Unity;
/// <summary>Verified HoYo resource forks, routed through UnityReader: Genshin BLB3, Star Rail ENCR and Zenless mhy1.
/// Base and Persistent variants are both indexed; no runtime mount precedence is inferred.</summary>
internal sealed class GenshinReader
{
    public const string Fork = "Genshin-BLB3";
    public const string Note = "Experimental offline shader reader; driver-cache effectiveness has not been verified. Base and updated asset variants are both included.";
    public const string StarRail = "StarRail-ENCR", Zenless = "Zenless-mhy1";
    const int RayTracingShaderClass = 825902497;
    const uint ZenlessDxilPlatform = 25;
    public static bool Handles(EngineInfo engine) => engine.Fork is Fork or StarRail or Zenless;
    sealed record Location(string Path, long? Container, string? Node, long Offset, int Size, bool Blob = false);
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Location>> located = new();
    public static string? FindData(Game game)
    {
        string exe = Path.GetFileNameWithoutExtension(game.ExePath);
        if (!(exe.Equals("YuanShen", StringComparison.OrdinalIgnoreCase) || exe.Equals("GenshinImpact", StringComparison.OrdinalIgnoreCase)) || !File.Exists(game.ExePath))
            return null;
        Span<byte> head = stackalloc byte[48];
        foreach (var dir in new[]
        {
            Path.GetDirectoryName(game.ExePath),
            game.InstallDir
        }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var data = GameFiles.Find(dir, exe + "_Data");
            if (!Directory.Exists(Path.Combine(data, "StreamingAssets", "AssetBundles", "blocks")))
                continue;
            var manager = GameFiles.Find(data, "globalgamemanagers");
            try
            {
                using var file = new FileStream(manager, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int n = file.ReadAtLeast(head, 32, false);
                if (n >= 32 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[8..]) == 17 && head[20..n].StartsWith("2017.4.30f1"u8))
                    return data;
            }
            catch (Exception e)when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public static string? ForkOf(Game game, string data)
    {
        string exe = Path.GetFileNameWithoutExtension(game.ExePath);
        if (FindData(game) != null)
            return Fork;
        string? kind = exe.Equals("StarRail", StringComparison.OrdinalIgnoreCase) ? StarRail : exe.Equals("ZenlessZoneZero", StringComparison.OrdinalIgnoreCase) ? Zenless : null;
        if (kind == null)
            return null;
        string folder = Path.Combine(data, "StreamingAssets", kind == StarRail ? "Asb" : "Blocks");
        if (!Directory.Exists(folder))
            return null;
        Span<byte> head = stackalloc byte[4];
        // BBL patch manifests can precede mhy1 archives in the same directory.
        foreach (var path in GameFiles.EnumerateFiles(folder, kind == StarRail ? "*.block" : "*.blk", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
        {
            using var file = File.OpenRead(path);
            if (file.Read(head) == 4 && head.SequenceEqual(kind == StarRail ? "ENCR"u8 : "mhy1"u8))
                return kind;
        }
        return null;
    }

    static bool BlockPack(string path) => Path.GetExtension(path).Equals(".blk", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".block", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Paths(string data)
    {
        string resources = Path.Combine(data, "Resources"), sdk = Path.Combine(data, "StreamingAssets", "MiHoYoSDKRes", "PC");
        return GameFiles.EnumerateFiles(data, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(p => BlockPack(p) || Path.GetDirectoryName(p) == data || Path.GetDirectoryName(p) == resources
                || (Path.GetDirectoryName(p)?.Equals(sdk, StringComparison.OrdinalIgnoreCase) == true
                    && (Path.GetFileName(p).Equals("PC", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(p).Equals("mihoyo_sdk_res", StringComparison.OrdinalIgnoreCase))))
            .Where(p => !Resource(p)).Order(GameFiles.PathOrder);
    }
    public static string Stamp(string data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var path in Paths(data))
        {
            var f = new FileInfo(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{GameFiles.Rel(data, path)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static IHoyoArchive Archive(string path, EngineInfo engine) => engine.Fork == Fork ? new GenshinArchive(path) : new HoyoArchive(path, engine.Fork == Zenless);
    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string data = UnityReader.Find(game)?.Data ?? throw new DirectoryNotFoundException("HoYo Unity data not found");
        var watch = Stopwatch.StartNew();
        var locations = new Dictionary<string, Location>();
        var shaders = new Dictionary<string, ShaderInfo>();
        var shared = new ShaderContainer.SharedLists();
        var maps = new List<ShaderMap>();
        var mapHashes = new HashSet<string>();
        var paths = Paths(data).ToArray();
        int done = 0, containers = 0, objects = 0, bad = 0, skipped = 0;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            content.AppendData(Encoding.UTF8.GetBytes(ContentStamps.Of(GameFiles.Rel(data, path), path)));
            if (BlockPack(path))
            {
                using var archive = Archive(path, engine);
                try
                {
                    ScanArchive(path, archive);
                }
                catch (NotSupportedException e)
                {
                    skipped++;
                    log?.Report($"{engine.Fork}: resource skipped: {e.Message}");
                }
            }
            else
            {
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = RandomAccess.GetLength(file);
                ReadAt read = (off, n) => ReadFile(file, length, off, n);
                if (ReadSerialized(read, length) != null)
                    Scan(read, length, path, null, null);
                else if (engine.Fork == Fork && read(0, 4).AsSpan().SequenceEqual("Blb\x03"u8))
                {
                    using var archive = new GenshinArchive(path);
                    ScanArchive(path, archive);
                }
                else if (engine.Fork == StarRail && Bundle.IsBundle(read(0, 8)))
                {
                    using var archive = new HoyoArchive(path, false);
                    ScanArchive(path, archive);
                }
                else
                {
                    using var bundle = Bundle.Open(path);
                    if (bundle != null)
                        foreach (var node in bundle.Nodes.Where(n => !Resource(n.Path)))
                            Scan(bundle.Reader(node), node.Size, path, null, node.Path);
                }
            }

            done++;
            progress?.Report((done, paths.Length));
            if (done % 100 == 0)
                log?.Report($"{engine.Fork}: {done}/{paths.Length} files, {containers} containers, {shaders.Count} shaders");
        }

        ct.ThrowIfCancellationRequested();
        located[game.Id] = locations;
        log?.Report($"{engine.Fork}: {done} files, {containers} containers, {objects} shader objects -> {shaders.Count} shaders, {maps.Count} maps, {skipped} skipped resources, {bad} unparseable containers ({watch.Elapsed.TotalSeconds:F1}s)");
        return new(Convert.ToHexStringLower(content.GetHashAndReset()), maps.Select(m => m.Platform).Distinct().Order(StringComparer.Ordinal).ToList(), shaders, maps);
        void ScanArchive(string path, IHoyoArchive archive)
        {
            long previous = -1;
            foreach (var node in archive.Nodes(ct))
            {
                if (node.Container != previous)
                {
                    containers++;
                    previous = node.Container;
                }

                if (!Resource(node.Name))
                    Scan(node.Read, node.Size, path, node.Container, node.Name);
            }
        }

        void Scan(ReadAt read, long length, string path, long? container, string? node)
        {
            ct.ThrowIfCancellationRequested();
            var sf = ReadSerialized(read, length);
            if (sf == null)
                return;
            foreach (var obj in sf.Objects.Where(o => o.ClassId is ShaderClass or ComputeShaderClass or RayTracingShaderClass))
            {
                if (obj.Start < 0 || obj.Size < 0 || obj.Start > length || obj.Size > length - obj.Start)
                    throw new InvalidDataException("Shader object outside serialized node");
                objects++;
                var hashes = new HashSet<string>();
                foreach (var(offset, code)in Containers(read, obj.Start, obj.Size, ct))
                    Add(code, new(path, container, node, obj.Start + offset, code.Length));
                if (engine.Fork != Fork && obj.ClassId == ShaderClass)
                {
                    if (obj.Size > int.MaxValue)
                        throw new InvalidDataException("Shader object too large");
                    var bytes = read(obj.Start, (int)obj.Size);
                    foreach (var code in BlobPrograms(bytes, engine))
                        Add(code, new(path, container, node, obj.Start, (int)obj.Size, true));
                }

                foreach (var map in UnityReader.ObjectMaps(GameFiles.Rel(data, path) + $"|{container?.ToString() ?? "loose"}|{node}|{obj.PathId}", hashes, shaders))
                    if (mapHashes.Add(map.Hash))
                        maps.Add(map);
                void Add(byte[] code, Location location)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Dxbc.Valid(code) || !Dxbc.Checksum(code).AsSpan().SequenceEqual(code.AsSpan(4, 16)))
                        return;
                    var sha = Convert.ToHexStringLower(SHA1.HashData(code));
                    if (!shaders.ContainsKey(sha))
                    {
                        ShaderInfo? info;
                        try
                        {
                            info = ShaderContainer.Parse(code, sha, new(0, 0, 0, 0));
                        }
                        catch (Exception e)when (e is ArgumentException or IndexOutOfRangeException)
                        {
                            bad++;
                            return;
                        }

                        if (info == null)
                        {
                            bad++;
                            return;
                        }

                        shaders.Add(sha, shared.Share(info with { Counts = CarvedReader.Counts(info.Bindings) }));
                        locations.Add(sha, location);
                    }

                    hashes.Add(sha);
                }
            }
        }
    }

    static IEnumerable<byte[]> BlobPrograms(byte[] bytes, EngineInfo engine)
    {
        IEnumerable<byte[]> blobs = ShaderBlob(bytes) ?? Enumerable.Empty<byte[]>();
        if (engine.Fork == StarRail)
            blobs = StarRailShaderBlob.Decode(bytes).Concat(blobs);
        var programs = blobs.SelectMany(b => Dxbc.Containers(b).Select(c => c.Container));
        if (engine.Fork == Zenless && ShaderBlob(bytes, ZenlessDxilPlatform) is { } dxil)
            programs = programs.Concat(dxil.SelectMany(b => Dxbc.Containers(b).Select(c => c.Container))
                .Where(code => Dxbc.DxilShaderModel(code) is { Major: 6 }));
        return programs;
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> hashes, Action<string, byte[]> sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (hashes.Count == 0)
            return;
        if (!located.TryGetValue(game.Id, out var locations))
        {
            Index(game, engine, null, null, ct);
            locations = located[game.Id];
        }

        foreach (var group in hashes.Where(locations.ContainsKey).GroupBy(sha => locations[sha].Path))
        {
            ct.ThrowIfCancellationRequested();
            IHoyoArchive? archive = null;
            Bundle? bundle = null;
            Microsoft.Win32.SafeHandles.SafeFileHandle? file = null;
            try
            {
                if (group.Any(sha => locations[sha].Container != null))
                    archive = Archive(group.Key, engine);
                else if (group.Any(sha => locations[sha].Node != null))
                    bundle = Bundle.Open(group.Key);
                else
                    file = File.OpenHandle(group.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e)when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }

            using (archive)
            using (bundle)
            using (file)
                foreach (var chunk in group.GroupBy(sha => (locations[sha].Container, locations[sha].Node)))
                {
                    ct.ThrowIfCancellationRequested();
                    ReadAt? read = null;
                    try
                    {
                        if (chunk.Key.Container is { } at)
                            read = archive!.NodeAt(at, chunk.Key.Node!)?.Read;
                        else if (chunk.Key.Node != null && bundle?.Nodes.FirstOrDefault(n => n.Path == chunk.Key.Node)is { } n)
                            read = bundle.Reader(n);
                        else if (file != null)
                        {
                            long length = RandomAccess.GetLength(file);
                            read = (off, count) => ReadFile(file, length, off, count);
                        }
                    }
                    catch (Exception e)when (e is IOException or InvalidDataException or NotSupportedException)
                    {
                        continue;
                    }

                    if (read == null)
                        continue;
                    foreach (var same in chunk.GroupBy(sha => (locations[sha].Offset, locations[sha].Size, locations[sha].Blob)))
                    {
                        ct.ThrowIfCancellationRequested();
                        List<byte[]> codes;
                        try
                        {
                            var bytes = read(same.Key.Offset, same.Key.Size);
                            codes = same.Key.Blob ? BlobPrograms(bytes, engine).ToList() : [bytes];
                        }
                        catch (Exception e)when (e is IOException or InvalidDataException)
                        {
                            continue;
                        }

                        var wanted = same.ToHashSet();
                        foreach (var code in codes)
                        {
                            var sha = Convert.ToHexStringLower(SHA1.HashData(code));
                            if (wanted.Remove(sha))
                                sink(sha, code);
                        }
                    }
                }
        }
    }

    static bool Resource(string name) => name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
    static byte[] ReadFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, long length, long offset, int count)
    {
        if (offset < 0 || offset > length || count < 0)
            throw new InvalidDataException("Read outside serialized file");
        var bytes = new byte[(int)Math.Min(count, length - offset)];
        for (int done = 0; done < bytes.Length;)
            done += RandomAccess.Read(file, bytes.AsSpan(done), offset + done)is> 0 and var n ? n : throw new EndOfStreamException();
        return bytes;
    }

    internal static IEnumerable<(long Offset, byte[] Code)> Containers(ReadAt read, long start, long length, CancellationToken ct, int windowSize = 8 << 20)
    {
        for (long position = 0; position < length;)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(windowSize, length - position);
            var window = read(start + position, count);
            if (window.Length != count)
                throw new EndOfStreamException("Truncated shader object");
            long next = position + count < length && count > 3 ? position + count - 3 : position + count;
            for (int cursor = 0; cursor < count;)
            {
                ct.ThrowIfCancellationRequested();
                int hit = window.AsSpan(cursor).IndexOf("DXBC"u8);
                if (hit < 0)
                    break;
                int local = cursor + hit;
                long offset = position + local;
                cursor = local + 1;
                if (offset + 32 > length)
                    continue;
                var head = local + 32 <= count ? window.AsSpan(local, 32).ToArray() : read(start + offset, 32);
                int size = Dxbc.HeaderSize(head);
                if (size == 0 || size > length - offset)
                    continue;
                var code = local + size <= count ? window.AsSpan(local, size).ToArray() : read(start + offset, size);
                if (!Dxbc.Valid(code) || !Dxbc.Checksum(code).AsSpan().SequenceEqual(code.AsSpan(4, 16)) || Dxbc.Kind(code) < 0)
                    continue;
                cursor = Math.Min(local + size, count);
                next = Math.Max(next, offset + size);
                yield return (offset, code);
            }

            position = next;
        }
    }
}
