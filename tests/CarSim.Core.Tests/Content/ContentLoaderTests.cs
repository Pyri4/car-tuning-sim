using CarSim.Core.Content;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Tests.Content;

public class ContentLoaderTests
{
    [Fact]
    public void BaseContentLoadsWithoutErrors()
    {
        var result = ContentLoader.LoadDirectory(TestContent.BaseContentPath);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(TestContent.K20, result.Database.Engines.Keys);
        Assert.True(result.Database.Parts.Count >= 30);
        Assert.Contains("gasoline_95", result.Database.Fuels.Keys);
    }

    [Fact]
    public void EveryBaseEngineHasACompleteStockBuild()
    {
        foreach (var engine in TestContent.Database.Engines.Values)
        {
            foreach (var slot in engine.Slots.Where(s => s.Required))
                Assert.True(engine.StockParts.ContainsKey(slot.Id), $"{engine.Id} has no stock part for required slot {slot.Id}");
        }
    }

    [Fact]
    public void SpecsConvertAuthoringUnitsToSi()
    {
        var piston = TestContent.Database.GetPart("k20.pistons.oem").GetSpec<PistonSpec>();
        Assert.Equal(0.086, piston.Bore, 12);
        Assert.Equal(0.030, piston.CompressionHeight, 12);
        Assert.Equal(-2e-6, piston.DishVolume, 15);
        Assert.Equal(120e5, piston.MaxCylinderPressure, 6);
        Assert.Equal(573.15, piston.MaxCrownTemperature, 9);
    }

    private static ContentLoadResult Load(string json) => ContentLoader.LoadFromStrings(new[] { ("test.json", json) });

    private const string ValidPiston = """
        { "id": "p1", "name": "P", "category": "pistons", "price": 1, "mass_kg": 1,
          "spec": { "count": 4, "bore_mm": 86, "pin_diameter_mm": 21, "compression_height_mm": 30, "dish_volume_cc": 0,
                    "mass_each_g": 380, "max_cylinder_pressure_bar": 120, "max_crown_temperature_c": 300 } }
        """;

    [Fact]
    public void ValidPartLoads()
    {
        var r = Load($$"""{ "parts": [ {{ValidPiston}} ] }""");
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.IsType<PistonSpec>(r.Database.GetPart("p1").Spec);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        var r = Load($$"""{ /* comment */ "parts": [ {{ValidPiston}}, ], }""");
        Assert.True(r.Success, string.Join("\n", r.Errors));
    }

    [Fact]
    public void UnknownSpecFieldIsRejected()
    {
        var r = Load($$"""{ "parts": [ {{ValidPiston.Replace("\"count\": 4", "\"count\": 4, \"bore_m\": 1")}} ] }""");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.ItemId == "p1" && e.Message.Contains("bore_m"));
    }

    [Fact]
    public void MissingRequiredSpecFieldIsRejected()
    {
        var r = Load($$"""{ "parts": [ {{ValidPiston.Replace("\"bore_mm\": 86,", "")}} ] }""");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.ItemId == "p1" && e.Message.Contains("bore_mm"));
    }

    [Fact]
    public void OutOfRangeSpecValueIsReported()
    {
        var r = Load($$"""{ "parts": [ {{ValidPiston.Replace("\"bore_mm\": 86", "\"bore_mm\": 860")}} ] }""");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("bore_mm"));
    }

    [Fact]
    public void UnknownCategoryIsReported()
    {
        var r = Load("""{ "parts": [ { "id": "x", "name": "X", "category": "flux_capacitor", "spec": {} } ] }""");
        Assert.Contains(r.Errors, e => e.ItemId == "x" && e.Message.Contains("Unknown category"));
    }

    [Fact]
    public void DuplicateIdsAreReported()
    {
        var r = Load($$"""{ "parts": [ {{ValidPiston}}, {{ValidPiston}} ] }""");
        Assert.Contains(r.Errors, e => e.Message.Contains("Duplicate"));
    }

    [Fact]
    public void AllErrorsAreCollectedNotJustTheFirst()
    {
        var bad1 = ValidPiston.Replace("\"id\": \"p1\"", "\"id\": \"a\"").Replace("\"bore_mm\": 86", "\"bore_mm\": 1");
        var bad2 = ValidPiston.Replace("\"id\": \"p1\"", "\"id\": \"b\"").Replace("\"count\": 4", "\"count\": 0");
        var r = Load($$"""{ "parts": [ {{bad1}}, {{bad2}} ] }""");
        Assert.Contains(r.Errors, e => e.ItemId == "a");
        Assert.Contains(r.Errors, e => e.ItemId == "b");
    }

    [Fact]
    public void EngineSlotCycleIsReported()
    {
        var r = Load("""
            { "engines": [ { "id": "e", "name": "E", "cylinders": 4, "slots": [
                { "id": "a", "category": "block", "install_after": ["b"] },
                { "id": "b", "category": "pistons", "install_after": ["a"] } ] } ] }
            """);
        Assert.Contains(r.Errors, e => e.ItemId == "e" && e.Message.Contains("cycle"));
    }

    [Fact]
    public void StockPartWithWrongCategoryIsReported()
    {
        var r = Load($$"""
            { "parts": [ {{ValidPiston}} ],
              "engines": [ { "id": "e", "name": "E", "cylinders": 4,
                 "slots": [ { "id": "block", "category": "block" } ],
                 "stock_parts": { "block": "p1" } } ] }
            """);
        Assert.Contains(r.Errors, e => e.ItemId == "e" && e.Message.Contains("slot takes 'block'"));
    }

    [Fact]
    public void InvalidJsonIsReportedWithSource()
    {
        var r = Load("{ \"parts\": [ ");
        Assert.False(r.Success);
        Assert.Equal("test.json", r.Errors[0].Source);
    }

    [Fact]
    public void FuelWithBadValuesIsReported()
    {
        var r = Load("""{ "fuels": [ { "id": "f", "name": "F", "octane_ron": 300, "stoichiometric_afr": 14.7, "lower_heating_value_mj_kg": 43, "density_kg_l": 0.75 } ] }""");
        Assert.Contains(r.Errors, e => e.ItemId == "f" && e.Message.Contains("octane"));
    }
}

public class SpecDescriberTests
{
    [Fact]
    public void DescribesAuthoredFieldsWithUnits()
    {
        var rows = CarSim.Core.Parts.SpecDescriber.Describe(TestContent.Database.GetPart("k20.pistons.oem").Spec);
        Assert.Contains(rows, r => r.Label == "Bore" && r.Value == "86 mm");
        Assert.Contains(rows, r => r.Label == "Max crown temperature" && r.Value == "300°C");
        Assert.Contains(rows, r => r.Label == "Dish volume" && r.Value == "-2 cc");
        Assert.DoesNotContain(rows, r => r.Label.Contains("Mass each") && r.Value.Contains("kg"));
        var susp = CarSim.Core.Parts.SpecDescriber.Describe(TestContent.Database.GetPart("suspension.oem").Spec);
        Assert.Contains(susp, r => r.Label == "Front arb" && r.Value == "500 N·m/°");
        Assert.Contains(susp, r => r.Label == "Front spring" && r.Value == "45 N/mm");
        Assert.Contains(susp, r => r.Label == "Front damper" && r.Value == "2,400 N·s/m");
        Assert.Equal("°", CarSim.Core.Parts.SpecDescriber.UnitOfField("front_camber_deg"));
        Assert.Equal("kPa", CarSim.Core.Parts.SpecDescriber.UnitOfField("pressure_kpa"));
        Assert.Equal("", CarSim.Core.Parts.SpecDescriber.UnitOfField("rear_pressure_factor"));
    }

    [Fact]
    public void EveryPartCanBeDescribed()
    {
        foreach (var p in TestContent.Database.Parts.Values)
            Assert.NotEmpty(CarSim.Core.Parts.SpecDescriber.Describe(p.Spec));
    }
}
