using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unity;
using Xunit;

namespace SCSKiller.Tests;
public sealed class HoyoIntegrationTests
{
    public sealed class LocalHoyoFactAttribute : FactAttribute
    {
        public LocalHoyoFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCSK_HOYO_ROOT")))
                Skip = "Set SCSK_HOYO_ROOT for read-only installed-resource verification.";
        }
    }

    [LocalHoyoFact, Trait("Needs", "Game")]
    public void Product_reader_preserves_reference_hashes_and_materializes_a_DX11_plan()
    {
        string root = Environment.GetEnvironmentVariable("SCSK_HOYO_ROOT")!, output = Environment.GetEnvironmentVariable("SCSK_HOYO_OUTPUT")!;
        Directory.CreateDirectory(output);
        bool rail = File.Exists(Path.Combine(root, "StarRail.exe"));
        var game = new Game("test:hoyo-expansion", rail ? "Star Rail" : "Zenless", Store.Manual, root, Path.Combine(root, rail ? "StarRail.exe" : "ZenlessZoneZero.exe"));
        var previous = Codecs.DirOverride;
        Codecs.DirOverride = Path.Combine(output, "codecs");
        Directory.CreateDirectory(Codecs.DirOverride);
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("SCSK_HOYO_OODLE")!, Path.Combine(Codecs.DirOverride, OodleHelper.OodleFileName), true);
            var reader = new UnityReader();
            var engine = reader.Detect(game);
            Assert.NotNull(engine);
            Assert.Equal(rail ? "StarRail-ENCR" : "Zenless-mhy1", engine.Fork);
            var logger = new GenshinReaderTests.InlineProgress<string>(line => File.AppendAllText(Path.Combine(output, "progress.log"), line + Environment.NewLine));
            var index = reader.Index(game, engine, logger, default);
            Assert.NotEmpty(index.Shaders);
            var expected = new HashSet<string>();
            using (var baseline = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SCSK_HOYO_BASELINE")!)))
                foreach (var shader in baseline.RootElement.GetProperty("shaders").EnumerateArray())
                    expected.Add(shader.GetProperty("Sha1").GetString()!);
            using (var packed = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SCSK_HOYO_REFERENCE")!)))
                foreach (var shader in packed.RootElement.GetProperty("shaders").EnumerateArray())
                    expected.Add(shader.GetProperty("sha1").GetString()!);
            var missing = expected.Except(index.Shaders.Keys).ToArray();
            File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new { expected = expected.Count, actual = index.Shaders.Count, missing, extra = index.Shaders.Keys.Except(expected).ToArray() }));
            Assert.Empty(missing);
            Assert.All(index.Maps, m => Assert.All(m.Shaders, s => Assert.Equal(index.Shaders[s].ShaderModel.Contains("_6_") ? "PCD3D_SM6" : "PCD3D_SM5", m.Platform)));
            var wanted = index.Shaders.Values.GroupBy(s => s.ShaderModel).SelectMany(g => g.Take(8)).Select(s => s.Sha1).ToHashSet();
            var sent = new HashSet<string>();
            reader.ReadShaders(game, engine, wanted, (sha, b) =>
            {
                Assert.Equal(sha, Convert.ToHexStringLower(SHA1.HashData(b)));
                Assert.True(sent.Add(sha));
            }, default);
            Assert.True(sent.SetEquals(wanted));
            var planner = new Planner();
            var dx11 = engine with
            {
                GraphicsApi = "D3D11"
            };
            var caps = new VendorCaps("nvidia-1", true, true, false);
            var plan = planner.Build(game, dx11, index, null, caps, Path.Combine(output, "plan"), logger, default, rayTracing: false);
            long independent = index.Shaders.Values.LongCount(s => Planner.IsD3D11(s) && s.Stage is not (Stage.Hull or Stage.Domain));
            Assert.InRange(plan.Stats.D3D11Shaders, independent, index.Shaders.Values.LongCount(Planner.IsD3D11));
            planner.Materialize(plan, game, dx11, reader, null, Path.Combine(output, "work"), default);
            Assert.Equal("0 0", File.ReadAllText(Path.Combine(output, "work", Planner.SkippedFile)));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { engine, unique = index.Shaders.Count, maps = index.Maps.Count, models = index.Shaders.Values.GroupBy(s => s.ShaderModel).ToDictionary(g => g.Key, g => g.Count()), referenceHashes = expected.Count, missing = missing.Length, sampleMaterialized = sent.Count, simulatedNvidiaDx11Plan = plan.Stats, scope = "Read-only indexing, hash checks and plan preparation; no GPU execution." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Codecs.DirOverride = previous;
        }
    }
}
