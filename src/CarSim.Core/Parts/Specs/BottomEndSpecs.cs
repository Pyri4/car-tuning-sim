using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

/// <summary>Cylinder block: sets cylinder count, bore, deck height and structural limits.</summary>
public sealed class BlockSpec : PartSpec
{
    public required int Cylinders { get; init; }
    public required double BoreMm { get; init; }

    /// <summary>Distance from crank centreline to the deck surface.</summary>
    public required double DeckHeightMm { get; init; }

    /// <summary>Main bearing saddle (crank main journal) diameter.</summary>
    public required double MainJournalDiameterMm { get; init; }

    /// <summary>Peak cylinder pressure the deck / head-bolt clamping can hold.</summary>
    public required double MaxCylinderPressureBar { get; init; }

    /// <summary>Coolant held in the block and head jackets.</summary>
    public double CoolantCapacityL { get; init; } = 3.0;

    public string Material { get; init; } = "aluminium";

    [JsonIgnore] public double Bore => Units.MmToM(BoreMm);
    [JsonIgnore] public double DeckHeight => Units.MmToM(DeckHeightMm);
    [JsonIgnore] public double MainJournalDiameter => Units.MmToM(MainJournalDiameterMm);
    [JsonIgnore] public double MaxCylinderPressure => Units.BarToPa(MaxCylinderPressureBar);

    protected override void Validate(SpecChecker check)
    {
        check.Range("cylinders", Cylinders, 1, 16);
        check.Range("bore_mm", BoreMm, 40, 150);
        check.Range("deck_height_mm", DeckHeightMm, 100, 400);
        check.Positive("main_journal_diameter_mm", MainJournalDiameterMm);
        check.Range("max_cylinder_pressure_bar", MaxCylinderPressureBar, 30, 500);
        check.Positive("coolant_capacity_l", CoolantCapacityL);
    }
}

/// <summary>Crankshaft: stroke, journal sizes and mechanical limits.</summary>
public sealed class CrankshaftSpec : PartSpec
{
    public required double StrokeMm { get; init; }
    public required double MainJournalDiameterMm { get; init; }
    public required double RodJournalDiameterMm { get; init; }

    /// <summary>Speed above which torsional vibration / bending fatigue becomes significant.</summary>
    public required double MaxRpm { get; init; }

    /// <summary>Torsional strength rating (fatigue begins below this, failure above it).</summary>
    public required double MaxTorqueNm { get; init; }

    public required double InertiaKgM2 { get; init; }
    public string Material { get; init; } = "cast";

    [JsonIgnore] public double Stroke => Units.MmToM(StrokeMm);
    [JsonIgnore] public double MainJournalDiameter => Units.MmToM(MainJournalDiameterMm);
    [JsonIgnore] public double RodJournalDiameter => Units.MmToM(RodJournalDiameterMm);

    protected override void Validate(SpecChecker check)
    {
        check.Range("stroke_mm", StrokeMm, 30, 150);
        check.Positive("main_journal_diameter_mm", MainJournalDiameterMm);
        check.Positive("rod_journal_diameter_mm", RodJournalDiameterMm);
        check.Range("max_rpm", MaxRpm, 1000, 25000);
        check.Positive("max_torque_nm", MaxTorqueNm);
        check.Range("inertia_kg_m2", InertiaKgM2, 0.001, 2.0);
    }
}

/// <summary>Plain bearing set (used for both main and rod bearings).</summary>
public sealed class BearingSpec : PartSpec
{
    public required double JournalDiameterMm { get; init; }

    /// <summary>Oil clearance when new. Wear increases it, which lowers oil pressure.</summary>
    public required double ClearanceMm { get; init; }

    /// <summary>Peak bearing load the shells tolerate with an adequate oil film, per bearing.</summary>
    public required double MaxLoadKn { get; init; }

    public string Material { get; init; } = "bi-metal";

    [JsonIgnore] public double JournalDiameter => Units.MmToM(JournalDiameterMm);
    [JsonIgnore] public double Clearance => Units.MmToM(ClearanceMm);
    [JsonIgnore] public double MaxLoad => MaxLoadKn * 1000.0;

    protected override void Validate(SpecChecker check)
    {
        check.Positive("journal_diameter_mm", JournalDiameterMm);
        check.Range("clearance_mm", ClearanceMm, 0.005, 0.2);
        check.Positive("max_load_kn", MaxLoadKn);
    }
}

/// <summary>Connecting rod set.</summary>
public sealed class ConnectingRodSpec : PartSpec
{
    public required int Count { get; init; }

    /// <summary>Centre-to-centre length.</summary>
    public required double LengthMm { get; init; }

    public required double BigEndDiameterMm { get; init; }
    public required double PinDiameterMm { get; init; }
    public required double MassEachG { get; init; }

    /// <summary>Tensile load rating (inertia load at TDC of the exhaust stroke is what pulls rods apart).</summary>
    public required double MaxTensileLoadKn { get; init; }

    /// <summary>Compressive/buckling load rating (peak gas force).</summary>
    public required double MaxCompressiveLoadKn { get; init; }

    public string Beam { get; init; } = "i-beam";

    [JsonIgnore] public double Length => Units.MmToM(LengthMm);
    [JsonIgnore] public double BigEndDiameter => Units.MmToM(BigEndDiameterMm);
    [JsonIgnore] public double PinDiameter => Units.MmToM(PinDiameterMm);
    [JsonIgnore] public double MassEach => Units.GToKg(MassEachG);
    [JsonIgnore] public double MaxTensileLoad => MaxTensileLoadKn * 1000.0;
    [JsonIgnore] public double MaxCompressiveLoad => MaxCompressiveLoadKn * 1000.0;

    protected override void Validate(SpecChecker check)
    {
        check.PositiveInt("count", Count);
        check.Range("length_mm", LengthMm, 60, 300);
        check.Positive("big_end_diameter_mm", BigEndDiameterMm);
        check.Positive("pin_diameter_mm", PinDiameterMm);
        check.Range("mass_each_g", MassEachG, 50, 3000);
        check.Positive("max_tensile_load_kn", MaxTensileLoadKn);
        check.Positive("max_compressive_load_kn", MaxCompressiveLoadKn);
    }
}

/// <summary>Piston set (piston + pin + rings).</summary>
public sealed class PistonSpec : PartSpec
{
    public required int Count { get; init; }

    /// <summary>Cylinder bore the pistons are made for.</summary>
    public required double BoreMm { get; init; }

    public required double PinDiameterMm { get; init; }

    /// <summary>Pin centre to crown deck surface.</summary>
    public required double CompressionHeightMm { get; init; }

    /// <summary>
    /// Volume the crown adds to the combustion chamber at TDC: positive for a dish / valve reliefs,
    /// negative for a dome (which raises compression).
    /// </summary>
    public required double DishVolumeCc { get; init; }

    /// <summary>Mass of one piston assembly including pin and rings.</summary>
    public required double MassEachG { get; init; }

    public required double MaxCylinderPressureBar { get; init; }

    /// <summary>Crown temperature above which the alloy loses strength rapidly.</summary>
    public required double MaxCrownTemperatureC { get; init; }

    public string Material { get; init; } = "cast";

    [JsonIgnore] public double Bore => Units.MmToM(BoreMm);
    [JsonIgnore] public double PinDiameter => Units.MmToM(PinDiameterMm);
    [JsonIgnore] public double CompressionHeight => Units.MmToM(CompressionHeightMm);
    [JsonIgnore] public double DishVolume => Units.CcToM3(DishVolumeCc);
    [JsonIgnore] public double MassEach => Units.GToKg(MassEachG);
    [JsonIgnore] public double MaxCylinderPressure => Units.BarToPa(MaxCylinderPressureBar);
    [JsonIgnore] public double MaxCrownTemperature => Units.CToK(MaxCrownTemperatureC);

    protected override void Validate(SpecChecker check)
    {
        check.PositiveInt("count", Count);
        check.Range("bore_mm", BoreMm, 40, 150);
        check.Positive("pin_diameter_mm", PinDiameterMm);
        check.Range("compression_height_mm", CompressionHeightMm, 10, 80);
        check.Range("dish_volume_cc", DishVolumeCc, -60, 100);
        check.Range("mass_each_g", MassEachG, 50, 3000);
        check.Range("max_cylinder_pressure_bar", MaxCylinderPressureBar, 30, 500);
        check.Range("max_crown_temperature_c", MaxCrownTemperatureC, 150, 700);
    }
}

/// <summary>Flywheel.</summary>
public sealed class FlywheelSpec : PartSpec
{
    public required double InertiaKgM2 { get; init; }

    /// <summary>Burst/fatigue speed rating.</summary>
    public required double MaxRpm { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Range("inertia_kg_m2", InertiaKgM2, 0.005, 5.0);
        check.Range("max_rpm", MaxRpm, 1000, 25000);
    }
}
