using CarSim.Core.Common;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Damage;

/// <summary>
/// One component's load against its rating, in player-facing units.
/// <see cref="Ratio"/> = load / rating (1.0 = at the rating). <see cref="Part"/> is the installed part loaded — for a
/// bank-scoped category (a head, a gasket, a turbo) the one serving the bank whose operating point this is.
/// </summary>
public readonly record struct StressReading(FailureMode Mode, string Category, double Ratio, double Load, double Rating, string Quantity, string Unit,
    PartInstance Part)
{
    public override string ToString() => $"{Quantity}: {Load:F1} {Unit} / {Rating:F1} {Unit} ({Ratio:P0})";
}

/// <summary>Computes component stress ratios from the current operating point. Pure function of its inputs.</summary>
public static class StressEvaluator
{
    /// <summary>
    /// Temperatures are reported in °C so ratios read naturally ("280 °C of a 300 °C rating"); the thermal
    /// damage law converts to kelvin (<see cref="DamageLaw.Thermal"/>).
    /// </summary>
    public static IReadOnlyList<StressReading> Evaluate(EngineConfiguration c, EngineTelemetry t)
    {
        var list = new List<StressReading>(16);
        void Add(FailureMode mode, PartInstance part, double load, double rating, string quantity, string unit)
        {
            if (rating > 0) list.Add(new StressReading(mode, part.Category, load / rating, load, rating, quantity, unit, part));
        }
        void Engine(FailureMode mode, string category, double load, double rating, string quantity, string unit) =>
            Add(mode, c.Part(category), load, rating, quantity, unit);
        // A bank's channels (telemetry without per-bank channels reads as one bank).
        double BankPcpBar(int b) => Units.PaToBar(t.Banks.Count > b ? t.Banks[b].PeakCylinderPressure : t.PeakCylinderPressure);
        double BankFloatRpm(int b) => t.Banks.Count > b ? t.Banks[b].ValveFloatRpm : t.ValveFloatRpm;
        string Suffix(IReadOnlyList<int> banks) => c.Banks.Count > 1 ? $", bank {string.Join("/", banks.Select(b => $"'{c.Banks[b].Definition.Id}'"))}" : "";

        Engine(FailureMode.RodTensileOverload, "connecting_rods", t.RodTensileLoad / 1000, c.Rods.MaxTensileLoad / 1000, "Rod tensile load", "kN");
        Engine(FailureMode.RodCompressiveOverload, "connecting_rods", t.RodCompressiveLoad / 1000, c.Rods.MaxCompressiveLoad / 1000, "Rod compressive load", "kN");
        Engine(FailureMode.PistonPressureOverload, "pistons", t.PeakCylinderPressureBar, c.Pistons.MaxCylinderPressureBar, "Peak cylinder pressure (piston)", "bar");
        Engine(FailureMode.PistonCrownOverheat, "pistons", Units.KToC(t.PistonCrownTemperature), c.Pistons.MaxCrownTemperatureC, "Piston crown temperature", "°C");
        // Each head gasket against the highest peak pressure of the banks it seals.
        foreach (var (slot, gasket) in c.Assembly.PartsOf(PartCategory.HeadGasket))
        {
            var banks = c.Assembly.Definition.BanksServedBy(slot);
            Add(FailureMode.HeadGasketBreach, gasket, banks.Max(BankPcpBar), gasket.Spec<Parts.Specs.HeadGasketSpec>().MaxCylinderPressureBar,
                "Peak cylinder pressure (head gasket" + Suffix(banks) + ")", "bar");
        }
        Engine(FailureMode.BlockDeckFailure, "block", t.PeakCylinderPressureBar, c.Block.MaxCylinderPressureBar, "Peak cylinder pressure (block)", "bar");
        Engine(FailureMode.CrankshaftOverspeed, "crankshaft", t.Rpm, c.Crankshaft.MaxRpm, "Engine speed (crankshaft)", "rpm");
        Engine(FailureMode.CrankshaftTorsion, "crankshaft", Math.Abs(t.Torque), c.Crankshaft.MaxTorqueNm, "Crankshaft torque", "N·m");
        Engine(FailureMode.FlywheelBurst, "flywheel", t.Rpm, c.Flywheel.MaxRpm, "Engine speed (flywheel)", "rpm");
        Engine(FailureMode.RodBearingFatigue, "rod_bearings", t.BearingLoad / 1000, c.RodBearings.MaxLoad / 1000, "Rod bearing load", "kN");
        Engine(FailureMode.MainBearingFatigue, "main_bearings", MainBearingShare * t.BearingLoad / 1000, c.MainBearings.MaxLoad / 1000, "Main bearing load", "kN");
        // Valves are rated to 110 % of the float speed: contact starts a few percent past float (each head at its banks' float speed).
        foreach (var (slot, head) in c.Assembly.PartsOf(PartCategory.CylinderHead))
        {
            var banks = c.Assembly.Definition.BanksServedBy(slot);
            Add(FailureMode.ValvePistonContact, head, t.Rpm, 1.10 * banks.Min(BankFloatRpm),
                "Engine speed vs valve-contact limit (110 % of float speed" + Suffix(banks) + ")", "rpm");
        }
        foreach (var turbo in c.Turbos)
        {
            string which = c.Turbos.Count > 1 ? $" ({turbo.Slot.Label})" : "";
            double shaftRpm = t.Turbos.Count > turbo.Index ? t.Turbos[turbo.Index].ShaftRpm : t.TurboRpm;
            double inletT = t.Turbos.Count > turbo.Index ? t.Turbos[turbo.Index].TurbineInletTemperature : t.TurbineInletTemperature;
            Add(FailureMode.TurboOverspeed, turbo.Part, shaftRpm, turbo.Spec.MaxShaftRpm, "Turbo shaft speed" + which, "rpm");
            Add(FailureMode.TurbineOverTemperature, turbo.Part, Units.KToC(inletT), turbo.Spec.MaxTurbineInletTemperatureC, "Turbine inlet temperature" + which, "°C");
        }
        return list;
    }

    /// <summary>Share of the peak rod force carried by one main bearing (shared between neighbours).</summary>
    public const double MainBearingShare = 0.6;
}
