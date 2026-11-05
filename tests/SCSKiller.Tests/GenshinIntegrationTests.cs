using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unity;
using Xunit;

namespace SCSKiller.Tests;

// Explicit local paths only. No game data, native libraries, or machine-specific paths are part of the test source.
public sealed class GenshinIntegrationTests
{
    public sealed class LocalGameFactAttribute : FactAttribute
    {
        public LocalGameFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCSK_GENSHIN_TEST_ROOT")))
                Skip = "Set SCSK_GENSHIN_TEST_ROOT for the read-only local resource check.";
        }
    }

    [LocalGameFact, Trait("Needs", "Game")]
    public void Product_reader_indexes_and_materializes_local_resources()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("SCSK_GENSHIN_TEST_ROOT")!);
        var game = new Game("test:genshin-integration", "Genshin local resource check", Store.Manual, root,
            Path.Combine(root, File.Exists(Path.Combine(root, "YuanShen.exe")) ? "YuanShen.exe" : "GenshinImpact.exe"));
        string output = Environment.GetEnvironmentVariable("SCSK_GENSHIN_TEST_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "genshin-check");
        Directory.CreateDirectory(output);
        var oldDir = Codecs.DirOverride;
        try
        {
            Codecs.DirOverride = Path.Combine(output, "codecs");
            if (Environment.GetEnvironmentVariable("SCSK_GENSHIN_OODLE_LIBRARY") is { Length: > 0 } library)
            {
                Directory.CreateDirectory(Codecs.DirOverride);
                File.Copy(library, Path.Combine(Codecs.DirOverride, OodleHelper.OodleFileName), overwrite: true);
            }
            var reader = new UnityReader(); var engine = reader.Detect(game);
            Assert.NotNull(engine); Assert.Equal("Genshin-BLB3", engine.Fork); Assert.Null(engine.Unsupported);
            var lines = new List<string>();
            var logger = new GenshinReaderTests.InlineProgress<string>(line =>
            {
                lines.Add(line); File.AppendAllText(Path.Combine(output, "progress.log"), line + Environment.NewLine);
            });
            var index = reader.Index(game, engine, logger, default);
            Assert.NotEmpty(index.Shaders);
            Assert.All(index.Maps, map => Assert.All(map.Shaders, sha =>
                Assert.Equal(index.Shaders[sha].ShaderModel.Contains("_6_") ? "PCD3D_SM6" : "PCD3D_SM5", map.Platform)));
            if (Environment.GetEnvironmentVariable("SCSK_GENSHIN_EXPECTED_INDEX") is { Length: > 0 } expectedPath)
            {
                using var expected = JsonDocument.Parse(File.ReadAllText(expectedPath));
                var hashes = expected.RootElement.GetProperty("shaders").EnumerateArray().Select(s => s.GetProperty("Sha1").GetString()!).ToHashSet();
                Assert.True(hashes.SetEquals(index.Shaders.Keys), $"Expected {hashes.Count}, got {index.Shaders.Count} shaders");
            }
            // Every extracted hash for a fixture; a representative set per stage/model for a whole installation.
            var requested = Environment.GetEnvironmentVariable("SCSK_GENSHIN_EXPECTED_INDEX") is { Length: > 0 }
                ? index.Shaders.Keys.ToHashSet()
                : index.Shaders.Values.GroupBy(s => s.ShaderModel).SelectMany(g => g.Take(8)).Select(s => s.Sha1).ToHashSet();
            var sent = new HashSet<string>();
            reader.ReadShaders(game, engine, requested, (sha, bytes) =>
            {
                Assert.True(sent.Add(sha)); Assert.Equal(sha, Convert.ToHexStringLower(SHA1.HashData(bytes)));
            }, default);
            Assert.True(sent.SetEquals(requested));
            Plan? plan = null;
            if (Environment.GetEnvironmentVariable("SCSK_GENSHIN_BUILD_PLAN") == "1")
            {
                // Simulated vendor capabilities only; neither Build nor Materialize creates a GPU device.
                var planner = new Planner(); var caps = new VendorCaps("nvidia-1", true, true, false);
                var dx11 = engine with { GraphicsApi = "D3D11" };
                Assert.Equal(Readiness.Ready, planner.Check(game, dx11, null, caps).Readiness);
                plan = planner.Build(game, dx11, index, null, caps, Path.Combine(output, "plan"), logger, default, rayTracing: false);
                long independent = index.Shaders.Values.LongCount(s => Planner.IsD3D11(s) && s.Stage is not (Stage.Hull or Stage.Domain));
                Assert.InRange(plan.Stats.D3D11Shaders, independent, index.Shaders.Values.LongCount(Planner.IsD3D11));
                planner.Materialize(plan, game, dx11, reader, null, Path.Combine(output, "work"), default);
            }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                engine, index.ContentHash, uniqueShaders = index.Shaders.Count, maps = index.Maps.Count,
                models = index.Shaders.Values.GroupBy(s => s.ShaderModel).ToDictionary(g => g.Key, g => g.Count()),
                materializedAndIdentityChecked = sent.Count,
                simulatedNvidiaDx11Plan = plan?.Stats,
                shaders = index.Shaders.Values.Select(s => new { s.Sha1, s.Stage, s.ShaderModel }),
                scope = "Product-reader indexing and bytecode identity only. No game launch or GPU execution."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Codecs.DirOverride = oldDir; }
    }
}
