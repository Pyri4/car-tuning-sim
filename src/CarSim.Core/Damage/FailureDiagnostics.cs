using System.Globalization;
using CarSim.Core.Common;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Damage;

/// <summary>Builds <see cref="FailureReport"/>s: rules per failure mode, reading only measured values and specs.</summary>
public static class FailureDiagnostics
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N0(double v) => v.ToString("N0", Inv);
    private static string F1(double v) => v.ToString("F1", Inv);
    private static string F2(double v) => v.ToString("F2", Inv);

    public static FailureReport Build(FailureContext x)
    {
        var info = FailureModeInfo.Of(x.Mode);
        var m = new List<ReportLine>();
        var f = new List<string>();
        var r = new List<string>();
        var t = x.T;
        var c = x.Config;
        var reading = x.Readings.FirstOrDefault(s => s.Mode == x.Mode);

        void Measure(string label, string value) => m.Add(new ReportLine(label, value));
        void MeasureReading(StressReading s) =>
            Measure(s.Quantity, $"{F1(s.Load)} {s.Unit} (rated {F1(s.Rating)} {s.Unit}, {(s.Ratio * 100).ToString("F0", Inv)} %)");

        Measure("Engine speed", $"{N0(t.Rpm)} rpm");
        if (reading.Rating > 0) MeasureReading(reading);

        switch (x.Mode)
        {
            case FailureMode.RodTensileOverload:
            {
                var g = c.Geometry;
                double safeRpm = SafeRpmForRods(c);
                Measure("Reciprocating mass per cylinder", $"{N0(g.ReciprocatingMass * 1000)} g");
                Measure("Rev limit", $"{N0(x.RevLimitRpm)} rpm");
                if (t.Rpm > x.RevLimitRpm + 100)
                    f.Add($"Engine speed ({N0(t.Rpm)} rpm) was above the rev limiter ({N0(x.RevLimitRpm)} rpm): the engine was driven past the limiter (missed shift, or the dyno/drivetrain forced it).");
                else if (safeRpm < x.RevLimitRpm)
                    f.Add($"The rev limit ({N0(x.RevLimitRpm)} rpm) is set above what these rods survive for long (about {N0(safeRpm)} rpm).");
                if (x.History.SecondsValveFloat > 0)
                    f.Add($"The valves floated for {F1(x.History.SecondsValveFloat)} s: the engine was revved past its valvetrain as well.");
                f.Add($"Rod inertia load is proportional to reciprocating mass × rpm²: {N0(g.ReciprocatingMass * 1000)} g per cylinder at {N0(t.Rpm)} rpm.");
                r.Add($"Keep engine speed below about {N0(safeRpm)} rpm with these rods (set the rev limiter accordingly).");
                r.Add($"Fit connecting rods rated for at least {F1(1.25 * reading.Load)} kN tensile.");
                r.Add("Lighter pistons or rods reduce the load in direct proportion to their mass.");
                break;
            }
            case FailureMode.RodCompressiveOverload:
                PressureFactors(x, f, m);
                r.Add($"Bring peak cylinder pressure below about {N0(0.8 * c.Rods.MaxCompressiveLoad / c.Geometry.PistonArea / 1e5)} bar (less boost or spark advance), or");
                r.Add($"fit connecting rods rated for at least {F1(1.25 * reading.Load)} kN compressive (e.g. forged H-beam).");
                break;
            case FailureMode.PistonPressureOverload:
                PressureFactors(x, f, m);
                r.Add($"Fit pistons rated for at least {N0(1.2 * t.PeakCylinderPressureBar)} bar (forged).");
                r.Add($"Or reduce peak cylinder pressure below about {N0(0.8 * c.Pistons.MaxCylinderPressureBar)} bar with less boost or spark advance.");
                break;
            case FailureMode.PistonCrownOverheat:
                Measure("Mixture", $"λ {F2(t.Lambda)} (target {F2(t.TargetLambda)})");
                Measure("Exhaust gas temperature", $"{N0(t.EgtC)} °C");
                LeanFactors(x, f, r);
                if (t.KnockIntensity > 0.3) f.Add($"Knock ({F1(t.KnockIntensity)}° past the limit) scrubs the crown's protective boundary layer and adds heat.");
                if (t.BoostKpa > 20) f.Add($"Boost ({N0(t.BoostKpa)} kPa) raises the heat flux through the crown.");
                r.Add($"Fit pistons with a higher crown-temperature rating (forged; this set is rated {N0(c.Pistons.MaxCrownTemperatureC)} °C).");
                break;
            case FailureMode.Detonation:
                DetonationFactors(x, f, m, r);
                break;
            case FailureMode.HeadGasketBreach:
            {
                Measure("Peak cylinder pressure", $"{N0(t.PeakCylinderPressureBar)} bar (gasket rated {N0(c.HeadGasket.MaxCylinderPressureBar)} bar)");
                Measure("Maximum coolant temperature", $"{N0(x.History.MaxCoolantC)} °C");
                bool pressure = t.PeakCylinderPressureBar > 0.85 * c.HeadGasket.MaxCylinderPressureBar;
                bool overheat = x.History.MaxCoolantC > 115;
                if (pressure) PressureFactors(x, f, m);
                if (overheat) f.Add($"Coolant reached {N0(x.History.MaxCoolantC)} °C (boiling): the head and block expanded and the gasket lost clamp.");
                if (x.History.MinCoolantLevel < 0.97) f.Add($"{N0(100 * (1 - x.History.MinCoolantLevel))} % of the coolant boiled away, leaving hot spots around the combustion chambers.");
                if (x.History.KnockSeconds > 0.5) f.Add($"Knock hammered the fire rings for {F1(x.History.KnockSeconds)} s.");
                if (pressure) r.Add($"Fit a head gasket rated for at least {N0(1.2 * t.PeakCylinderPressureBar)} bar (copper-ringed MLS), or reduce boost/advance.");
                if (overheat) r.Add("Fix the cooling problem before rebuilding: larger radiator, more airflow, check the heat load.");
                r.Add("Check the cylinder head for warping before refitting it. Symptoms of a blown gasket: coolant loss, white exhaust smoke, low compression.");
                break;
            }
            case FailureMode.BlockDeckFailure:
                PressureFactors(x, f, m);
                r.Add($"The block deck and head bolts are rated {N0(c.Block.MaxCylinderPressureBar)} bar. Fit a closed-deck/sleeved block for more, or reduce boost and advance.");
                break;
            case FailureMode.CrankshaftOverspeed:
                if (t.Rpm > x.RevLimitRpm + 100) f.Add($"Engine speed ({N0(t.Rpm)} rpm) was above the rev limiter ({N0(x.RevLimitRpm)} rpm).");
                else f.Add($"The rev limit ({N0(x.RevLimitRpm)} rpm) is above the crankshaft's rating ({N0(c.Crankshaft.MaxRpm)} rpm).");
                r.Add($"Keep engine speed below {N0(0.95 * c.Crankshaft.MaxRpm)} rpm, or fit a forged crankshaft rated for more.");
                break;
            case FailureMode.CrankshaftTorsion:
                f.Add($"Engine torque {N0(Math.Abs(t.Torque))} N·m against a {N0(c.Crankshaft.MaxTorqueNm)} N·m rating.");
                r.Add("Fit a forged crankshaft, or reduce torque (boost).");
                break;
            case FailureMode.FlywheelBurst:
                f.Add($"The flywheel is rated to {N0(c.Flywheel.MaxRpm)} rpm.");
                r.Add("Fit a flywheel rated above the engine's maximum speed (chromoly), and respect the rev limit.");
                break;
            case FailureMode.RodBearingFatigue:
            case FailureMode.MainBearingFatigue:
                Measure("Peak cylinder pressure", $"{N0(t.PeakCylinderPressureBar)} bar");
                if (x.History.KnockSeconds > 0.5) f.Add($"Knock loaded the bearings with pressure spikes for {F1(x.History.KnockSeconds)} s.");
                if (t.PeakCylinderPressureBar > 90) f.Add($"High peak cylinder pressure ({N0(t.PeakCylinderPressureBar)} bar) loads the bearings on every firing stroke.");
                if (x.History.MinOilPressureMargin < 1.0) f.Add($"Oil pressure dropped to {N0(100 * x.History.MinOilPressureMargin)} % of the requirement: the oil film was thin.");
                r.Add("Fit tri-metal performance bearings rated for the load, and have the crankshaft journals checked.");
                r.Add("Reduce peak cylinder pressure (boost, advance) and eliminate knock.");
                break;
            case FailureMode.OilStarvation:
                OilFactors(x, f, m, r);
                break;
            case FailureMode.ValvePistonContact:
            {
                double springWear = c.Part(PartCategory.ValveSprings).Wear;
                double springForce = ValvetrainModel.SpringForceN(c.Springs, springWear);
                Measure("Valve float speed", $"{N0(t.ValveFloatRpm)} rpm");
                Measure("Valve spring force at full lift", springWear > 0.01 ? $"{N0(springForce)} N (new {N0(c.Springs.OpenForceN)} N)" : $"{N0(springForce)} N");
                if (t.Rpm > x.RevLimitRpm + 100)
                    f.Add($"Engine speed ({N0(t.Rpm)} rpm) was above the rev limiter ({N0(x.RevLimitRpm)} rpm): over-rev (missed shift or forced by the dyno/drivetrain).");
                else
                    f.Add($"The rev limit ({N0(x.RevLimitRpm)} rpm) allows the engine past its valve-float speed ({N0(t.ValveFloatRpm)} rpm).");
                f.Add($"Cam lift {F1(c.Cams.MaxLiftMm)} mm with {N0(c.Head.ValveMovingMassG)} g valves needs more spring force than {N0(springForce)} N at this speed.");
                if (springWear > 0.2) f.Add($"The valve springs were worn ({N0(100 * springWear)} %), which cost {N0(c.Springs.OpenForceN - springForce)} N of spring force.");
                r.Add("Fit stiffer valve springs (and lighter valves), or lower the rev limit below the float speed.");
                r.Add("The head needs new valves and guides; check the pistons for valve strikes.");
                break;
            }
            case FailureMode.CylinderHeadWarp:
                Measure("Maximum coolant temperature", $"{N0(x.History.MaxCoolantC)} °C");
                f.Add($"Coolant reached {N0(x.History.MaxCoolantC)} °C and {N0(100 * (1 - x.History.MinCoolantLevel))} % of it boiled away. Heat into the coolant exceeded what the radiator ({N0(c.Radiator.HeatRejectionWPerK)} W/K) could reject with the available airflow.");
                r.Add("Have the head resurfaced or replaced; replace the head gasket.");
                r.Add("Increase cooling capacity (larger radiator, better airflow) or reduce sustained load.");
                break;
            case FailureMode.TurboOverspeed:
            {
                Measure("Turbo shaft speed", $"{N0(t.TurboRpm)} rpm (rated {N0(c.Turbo!.MaxShaftRpm)} rpm)");
                Measure("Compressor flow", $"{N0(100 * t.CompressorChokeRatio)} % of choke, efficiency {N0(100 * t.CompressorEfficiency)} %");
                f.Add("The compressor was too small for the airflow: near choke it must spin ever faster to hold the boost target.");
                r.Add("Fit a larger compressor, or reduce the boost target at high rpm.");
                break;
            }
            case FailureMode.TurbineOverTemperature:
                Measure("Turbine inlet temperature", $"{N0(Units.KToC(t.TurbineInletTemperature))} °C (rated {N0(c.Turbo!.MaxTurbineInletTemperatureC)} °C)");
                Measure("Mixture", $"λ {F2(t.Lambda)}");
                LeanFactors(x, f, r);
                if (t.KnockRetard > 3) f.Add($"Knock control had retarded timing {F1(t.KnockRetard)}°: late combustion sends heat into the exhaust.");
                r.Add("Run richer under boost (λ 0.76–0.80) and avoid heavy timing retard.");
                break;
        }

        return new FailureReport(t.Time, x.Mode, info.Severity, info.Title, info.Cause, x.Slot.Label, x.Part.Definition.Name,
            m, f, r, x.Collateral);
    }

    /// <summary>Speed at which rod inertia load reaches the fatigue (endurance) threshold.</summary>
    public static double SafeRpmForRods(EngineConfiguration c)
    {
        var g = c.Geometry;
        double endurance = FailureModeInfo.Of(FailureMode.RodTensileOverload).Endurance;
        double k = g.ReciprocatingMass * g.CrankRadius * (1.0 + g.CrankRadius / g.RodLength);
        return Units.RadPerSecToRpm(Math.Sqrt(endurance * c.Rods.MaxTensileLoad / k));
    }

    private static void PressureFactors(FailureContext x, List<string> f, List<ReportLine> m)
    {
        var t = x.T;
        m.Add(new ReportLine("Peak cylinder pressure", $"{N0(t.PeakCylinderPressureBar)} bar"));
        m.Add(new ReportLine("Spark advance", $"{F1(t.IgnitionAdvance)}° BTDC (MBT {F1(t.MbtAdvance)}°)"));
        if (t.BoostKpa > 10) m.Add(new ReportLine("Boost", $"{N0(t.BoostKpa)} kPa"));
        if (t.BoostKpa > 10) f.Add($"Boost of {N0(t.BoostKpa)} kPa packs more charge into the cylinder: peak pressure scales with it.");
        if (t.IgnitionAdvance > t.MbtAdvance + 2) f.Add($"Spark advance ({F1(t.IgnitionAdvance)}°) is past MBT ({F1(t.MbtAdvance)}°): extra advance raises peak pressure without adding torque.");
        if (t.KnockIntensity > 0.3) f.Add($"Knock ({F1(t.KnockIntensity)}° past the limit) adds pressure spikes on top of normal combustion.");
        if (x.Config.Geometry.CompressionRatio > 10 && t.BoostKpa > 20) f.Add($"Compression ratio {F1(x.Config.Geometry.CompressionRatio)}:1 is high for boost.");
        if (t.MapSensorSaturated) f.Add("The ECU's MAP sensor was saturated, so it used timing meant for a much lighter load.");
    }

    private static void LeanFactors(FailureContext x, List<string> f, List<string> r)
    {
        var t = x.T;
        if (t.Lambda > t.TargetLambda + 0.05)
        {
            f.Add($"The engine ran leaner than commanded (λ {F2(t.Lambda)} vs target {F2(t.TargetLambda)}).");
            if (t.FuelLimit == FuelLimit.InjectorCapacity) { f.Add($"The injectors were static (commanded duty {N0(100 * t.InjectorDuty)} %)."); r.Add("Fit larger injectors and rescale the ECU's injector setting."); }
            if (t.FuelLimit == FuelLimit.PumpCapacity) { f.Add($"The fuel pump could not hold rail pressure ({N0(Units.PaToKpa(t.FuelRailPressure))} kPa)."); r.Add("Fit a higher-flow fuel pump."); }
            if (t.MapSensorSaturated) { f.Add($"The ECU's MAP sensor was saturated at {N0(Units.PaToKpa(t.EcuMapReading))} kPa: it could not see the boost and under-fuelled."); r.Add("Fit an ECU with a MAP sensor that covers the boost level (standalone ECU)."); }
            if (Math.Abs(x.Config.Fuel.StoichiometricAfr - 14.7) > 0.5) r.Add($"Check the ECU's fuel calibration for {x.Config.Fuel.Name}.");
        }
        else if (t.TargetLambda > 0.9 && t.BoostKpa > 20)
        {
            f.Add($"The target mixture (λ {F2(t.TargetLambda)}) is too lean for boost.");
            r.Add("Command a richer mixture under boost (λ 0.76–0.82).");
        }
        else r.Add("Run a slightly richer mixture under full load to cool the combustion chamber.");
    }

    private static void DetonationFactors(FailureContext x, List<string> f, List<ReportLine> m, List<string> r)
    {
        var t = x.T;
        var c = x.Config;
        var h = x.History;
        m.Add(new ReportLine("Knock", $"{F1(h.MaxKnockIntensity)}° past the knock limit (peak), {F1(h.KnockSeconds)} s of knocking"));
        m.Add(new ReportLine("Spark advance", $"{F1(t.IgnitionAdvance)}° BTDC vs knock limit {F1(t.KnockLimitAdvance)}°"));
        m.Add(new ReportLine("Fuel", $"{c.Fuel.Name} (RON {N0(c.Fuel.OctaneRon)})"));
        m.Add(new ReportLine("Compression ratio", $"{F1(c.Geometry.CompressionRatio)}:1"));
        m.Add(new ReportLine("Charge temperature", $"{N0(Units.KToC(t.ChargeTemperature))} °C"));
        f.Add($"Spark timing ({F1(t.IgnitionAdvance)}°) was beyond what the fuel tolerates under these conditions ({F1(t.KnockLimitAdvance)}°).");
        if (c.Fuel.OctaneRon < 97) f.Add($"RON {N0(c.Fuel.OctaneRon)} fuel has limited knock resistance.");
        if (c.Geometry.CompressionRatio > 10.5) f.Add($"High compression ({F1(c.Geometry.CompressionRatio)}:1) raises end-gas temperature and pressure.");
        if (t.BoostKpa > 20) f.Add($"Boost ({N0(t.BoostKpa)} kPa) raises cylinder pressure and end-gas temperature.");
        if (t.ChargeTemperature > Units.CToK(55)) f.Add($"Hot intake charge ({N0(Units.KToC(t.ChargeTemperature))} °C).");
        if (t.CoolantC > 100) f.Add($"Hot coolant ({N0(t.CoolantC)} °C).");
        if (t.Lambda > 0.9 && t.ManifoldPressure > 90_000) f.Add($"A lean mixture (λ {F2(t.Lambda)}) under load is knock-prone.");
        if (t.MapSensorSaturated) f.Add("The ECU's MAP sensor was saturated, so it used timing meant for a much lighter load.");
        if (!c.Ecu.KnockControl) f.Add("The ECU has no knock control to pull timing.");
        else if (h.MaxKnockRetard >= Ecu.EcuController.MaxKnockRetardDeg - 0.01) f.Add($"Knock control was at its {N0(Ecu.EcuController.MaxKnockRetardDeg)}° limit and could not remove more timing.");
        else if (t.KnockRetard < 0.01 && h.MaxKnockRetard < 0.01) f.Add("Knock control was disabled in the tune.");
        r.Add($"Remove at least {N0(Math.Ceiling(h.MaxKnockIntensity + 2))}° of timing around {N0(t.Rpm)} rpm / {N0(t.MapKpa)} kPa.");
        r.Add("Use higher-octane fuel (RON 98, race fuel or E85 with a matching calibration).");
        if (c.Geometry.CompressionRatio > 10 && t.BoostKpa > 20) r.Add("Lower the compression ratio (dished pistons or a thicker gasket) for boost.");
        if (c.Turbo != null && c.Intercooler == null) r.Add("Fit an intercooler.");
        r.Add("Replace the pistons (and inspect the head gasket and rod bearings).");
    }

    private static void OilFactors(FailureContext x, List<string> f, List<ReportLine> m, List<string> r)
    {
        var t = x.T;
        var h = x.History;
        var c = x.Config;
        m.Add(new ReportLine("Oil pressure", $"{F2(t.OilPressureBar)} bar (bearings need {F2(Units.PaToBar(t.OilPressureRequired))} bar)"));
        m.Add(new ReportLine("Oil temperature", $"{N0(t.OilC)} °C (max {N0(h.MaxOilC)} °C)"));
        if (h.SecondsBelowRequiredOilPressure > 0)
            f.Add($"Oil pressure fell as low as {N0(100 * h.MinOilPressureMargin)} % of the requirement and stayed below it for {F1(h.SecondsBelowRequiredOilPressure)} s.");
        if (h.MaxOilC > 130) { f.Add($"Oil temperature reached {N0(h.MaxOilC)} °C: hot oil is thin and leaks away through the bearings."); r.Add("Keep oil temperature below ~130 °C (cooling, less sustained load)."); }
        if (h.MaxSumpG > c.OilPan.MaxSustainedG) { f.Add($"Sustained {F2(h.MaxSumpG)} g exceeded the oil pan's {F2(c.OilPan.MaxSustainedG)} g: oil sloshed away from the pickup."); r.Add("Fit a baffled oil pan."); }
        double mainWear = c.Part(PartCategory.MainBearings).Wear;
        if (mainWear > 0.3) f.Add($"Bearing clearance had grown with wear (mains {N0(100 * mainWear)} % worn), bleeding off even more oil pressure.");
        if (t.Rpm > 5000) f.Add($"High engine speed ({N0(t.Rpm)} rpm) raises the pressure the bearings need.");
        r.Add("Replace the bearings and regrind or replace the crankshaft.");
        r.Add("A high-volume oil pump raises pressure at every speed; watch the oil pressure gauge and back off when it drops.");
    }
}
