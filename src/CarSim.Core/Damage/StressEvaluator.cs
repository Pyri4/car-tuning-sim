using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Damage;

/// <summary>
/// One component's load against its rating, in player-facing units.
/// <see cref="Ratio"/> = load / rating (1.0 = at the rating).
/// </summary>
public readonly record struct StressReading(FailureMode Mode, string Category, double Ratio, double Load, double Rating, string Quantity, string Unit)
{
    public override string ToString() => $"{Quantity}: {Load:F1} {Unit} / {Rating:F1} {Unit} ({Ratio:P0})";
}

/// <summary>Computes component stress ratios from the current operating point. Pure function of its inputs.</summary>
public static class StressEvaluator
{
    /// <summary>Temperatures are compared in °C so ratios read naturally ("280 °C of a 300 °C rating").</summary>
    public static IReadOnlyList<StressReading> Evaluate(EngineConfiguration c, EngineTelemetry t)
    {
        var list = new List<StressReading>(16);
        void Add(FailureMode mode, string category, double load, double rating, string quantity, string unit)
        {
            if (rating > 0) list.Add(new StressReading(mode, category, load / rating, load, rating, quantity, unit));
        }

        Add(FailureMode.RodTensileOverload, "connecting_rods", t.RodTensileLoad / 1000, c.Rods.MaxTensileLoad / 1000, "Rod tensile load", "kN");
        Add(FailureMode.RodCompressiveOverload, "connecting_rods", t.RodCompressiveLoad / 1000, c.Rods.MaxCompressiveLoad / 1000, "Rod compressive load", "kN");
        Add(FailureMode.PistonPressureOverload, "pistons", t.PeakCylinderPressureBar, c.Pistons.MaxCylinderPressureBar, "Peak cylinder pressure (piston)", "bar");
        Add(FailureMode.PistonCrownOverheat, "pistons", Units.KToC(t.PistonCrownTemperature), c.Pistons.MaxCrownTemperatureC, "Piston crown temperature", "°C");
        Add(FailureMode.HeadGasketBreach, "head_gasket", t.PeakCylinderPressureBar, c.HeadGasket.MaxCylinderPressureBar, "Peak cylinder pressure (head gasket)", "bar");
        Add(FailureMode.BlockDeckFailure, "block", t.PeakCylinderPressureBar, c.Block.MaxCylinderPressureBar, "Peak cylinder pressure (block)", "bar");
        Add(FailureMode.CrankshaftOverspeed, "crankshaft", t.Rpm, c.Crankshaft.MaxRpm, "Engine speed (crankshaft)", "rpm");
        Add(FailureMode.CrankshaftTorsion, "crankshaft", Math.Abs(t.Torque), c.Crankshaft.MaxTorqueNm, "Crankshaft torque", "N·m");
        Add(FailureMode.FlywheelBurst, "flywheel", t.Rpm, c.Flywheel.MaxRpm, "Engine speed (flywheel)", "rpm");
        Add(FailureMode.RodBearingFatigue, "rod_bearings", t.BearingLoad / 1000, c.RodBearings.MaxLoad / 1000, "Rod bearing load", "kN");
        Add(FailureMode.MainBearingFatigue, "main_bearings", MainBearingShare * t.BearingLoad / 1000, c.MainBearings.MaxLoad / 1000, "Main bearing load", "kN");
        // Valves are rated to 110 % of the float speed: contact starts a few percent past float.
        Add(FailureMode.ValvePistonContact, "cylinder_head", t.Rpm, 1.10 * t.ValveFloatRpm, "Engine speed vs valve-contact limit (110 % of float speed)", "rpm");
        if (c.Turbo != null)
        {
            Add(FailureMode.TurboOverspeed, "turbocharger", t.TurboRpm, c.Turbo.MaxShaftRpm, "Turbo shaft speed", "rpm");
            Add(FailureMode.TurbineOverTemperature, "turbocharger", Units.KToC(t.TurbineInletTemperature), c.Turbo.MaxTurbineInletTemperatureC, "Turbine inlet temperature", "°C");
        }
        return list;
    }

    /// <summary>Share of the peak rod force carried by one main bearing (shared between neighbours).</summary>
    public const double MainBearingShare = 0.6;
}
