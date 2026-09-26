using CarSim.Core.Content;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Tests.Content;

public class ModTests
{
    private const string Clutch = """
        { "id": "clutch.x", "name": "X", "category": "clutch", "price": 1, "mass_kg": 1, "spec": { "max_torque_nm": 300 } }
        """;

    private static ContentLoadResult Layers(params (string source, string json)[][] layers) => ContentLoader.LoadFromLayers(layers);

    [Fact]
    public void AModCanAddAndRedefineContent()
    {
        var r = Layers(
            new[] { ("base/parts.json", $$"""{ "parts": [ {{Clutch}} ] }""") },
            new[] { ("mods/a/parts.json", $$"""{ "parts": [ {{Clutch.Replace("300", "450")}}, {{Clutch.Replace("clutch.x", "clutch.y")}} ] }""") });
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(450, ((ClutchSpec)r.Database.GetPart("clutch.x").Spec).MaxTorqueNm);
        Assert.NotNull(r.Database.GetPart("clutch.y"));
        var ov = Assert.Single(r.Overrides);
        Assert.Contains("mods/a/parts.json", ov);
        Assert.Contains("clutch.x", ov);
    }

    [Fact]
    public void DuplicatesWithinOneLayerAreStillErrors()
    {
        var r = Layers(
            new[] { ("base/parts.json", $$"""{ "parts": [ {{Clutch}} ] }""") },
            new[] { ("mods/a/one.json", $$"""{ "parts": [ {{Clutch}} ] }"""), ("mods/a/two.json", $$"""{ "parts": [ {{Clutch}} ] }""") });
        Assert.Contains(r.Errors, e => e.Source == "mods/a/two.json" && e.Message.Contains("Duplicate part id"));
    }

    [Fact]
    public void ModContentIsValidatedLikeBaseContent()
    {
        // Mod parts go through the same spec validation as the base game.
        var r = Layers(
            new[] { ("base/parts.json", $$"""{ "parts": [ {{Clutch}} ] }""") },
            new[] { ("mods/a/parts.json", """{ "parts": [ { "id": "clutch.z", "name": "Z", "category": "clutch", "spec": { "max_torque_nm": 5 } } ] }""") });
        Assert.Contains(r.Errors, e => e.ItemId == "clutch.z" && e.Message.Contains("max_torque_nm"));
    }

    [Fact]
    public void TheExampleModLoadsOnTopOfTheBaseGame()
    {
        string mods = Path.Combine(TestContent.RepoRoot, "docs", "example-mod");
        var r = ContentLoader.LoadWithMods(TestContent.BaseContentPath, mods);
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(new[] { "club_clutch_pack" }, r.Mods);
        Assert.Equal(420, ((ClutchSpec)r.Database.GetPart("clutch.stage2_cerametallic").Spec).MaxTorqueNm);
        Assert.Contains(r.Overrides, o => o.Contains("tires.street_225_50r16"));
        Assert.Equal(140, ((TireSpec)r.Database.GetPart("tires.street_225_50r16").Spec).TreadLifeMj);
        // Everything else is untouched.
        Assert.Equal(TestContent.Database.Parts.Count + 1, r.Database.Parts.Count);
    }

    [Fact]
    public void NoModsFolderMeansNoMods()
    {
        var r = ContentLoader.LoadWithMods(TestContent.BaseContentPath, Path.Combine(Path.GetTempPath(), "carsim-no-such-dir"));
        Assert.True(r.Success);
        Assert.Empty(r.Mods);
        Assert.Empty(r.Overrides);
    }
}
