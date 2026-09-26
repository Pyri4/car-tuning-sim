using CarSim.Core.Common;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Damage;

/// <summary>Result of one damage update.</summary>
public sealed record DamageStep(IReadOnlyList<FailureReport> NewFailures, IReadOnlyList<EngineWarning> Warnings);

/// <summary>
/// Accumulates fatigue and wear on the installed parts from each step's operating point, triggers
/// failures with explanatory reports, and raises live warnings. Deterministic: damage is a function of
/// the load history only.
/// </summary>
public sealed class DamageModel
{
    // Special-process rates (documented in SIMULATION_SPEC.md).
    public const double KnockPistonDamagePerCycle = 1e-4;  // per knocking combustion per deg² of knock, at a 120 bar piston
    public const double KnockGasketDamagePerCycle = 2.5e-5;
    public const double KnockBearingDamagePerCycle = 1.5e-5;
    public const double StarvationWearRate = 0.05;         // per s at total oil loss, scaled by load
    public const double NormalBearingWearRate = 2e-7;      // per s
    public const double HotOilWearRate = 0.0005;           // per s per 10 K above 150 °C
    public const double OverheatGasketRate = 0.01;         // per s per 10 K above 115 °C
    public const double CoolantLossGasketRate = 0.05;      // per s at total coolant loss
    public const double CoolantLossHeadRate = 0.01;        // per s per 10 % of coolant lost beyond 20 %
    public const double SpringFloatWearRate = 0.1;         // per s per unit of (rpm/float − 1)
    public const double KnockRingWearRate = 0.0005;        // per s per degree of knock
    public const double NormalRingWearRate = 2e-7;         // per s
    public const double SurgeBearingWearRate = 0.002;      // per s at full surge depth, per unit of (PR − 1)

    /// <summary>
    /// Damage faster than this (life under 10 hours at the current load) counts as exposure in a part's
    /// damage ledger; slower ageing (hot but healthy parts) is accumulated but not logged.
    /// </summary>
    public const double ExposureThreshold = 1.0 / 36_000;

    /// <summary>Warn when a part would last less than this at the current load (s); danger below <see cref="DangerLifeSeconds"/>.</summary>
    public const double CautionLifeSeconds = 3600;
    public const double DangerLifeSeconds = 120;

    private readonly EngineConfiguration _c;
    private readonly List<FailureReport> _failures = new();
    private readonly Queue<EngineTelemetry> _recent = new();
    private readonly Dictionary<PartInstance, Dictionary<FailureMode, double>> _fatigueAtStart;
    private double _sinceSample;

    public DamageModel(EngineConfiguration config)
    {
        _c = config;
        // Fatigue carried in from earlier sessions, so a report can say how much of the damage is old.
        _fatigueAtStart = config.Assembly.AllParts.ToDictionary(p => p, p => p.Damage.Fatigue.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    public OperatingHistory History { get; } = new();
    public IReadOnlyList<FailureReport> Failures => _failures;
    public IReadOnlyList<EngineWarning> Warnings { get; private set; } = Array.Empty<EngineWarning>();

    /// <summary>Telemetry sampled every 0.1 s over the last 10 s (for failure analysis graphs).</summary>
    public IReadOnlyCollection<EngineTelemetry> RecentSamples => _recent;

    /// <summary>True once any catastrophic failure has happened: the engine cannot run.</summary>
    public bool Seized => IsSeized(_c.Assembly);

    /// <summary>Whether a catastrophic failure among <paramref name="engine"/>'s installed parts locks it.</summary>
    public static bool IsSeized(Engines.EngineAssembly engine) =>
        engine.AllParts.Any(p => p.Damage.Failure is { } f && FailureModeInfo.Of(f.Mode).Severity == FailureSeverity.Catastrophic);

    /// <summary>Whether a degraded failure of <paramref name="mode"/> is present on the relevant part.</summary>
    public bool HasFailure(FailureMode mode) =>
        _c.Assembly.AllParts.Any(p => p.Damage.Failure?.Mode == mode);

    public DamageStep Update(EngineTelemetry t, double dt, double revLimitRpm, double sumpG = 0.0)
    {
        History.Record(t, sumpG, dt);
        _sinceSample += dt;
        if (_sinceSample >= 0.1)
        {
            _sinceSample = 0;
            _recent.Enqueue(t);
            while (_recent.Count > 100) _recent.Dequeue();
        }

        var newFailures = new List<FailureReport>();
        var readings = StressEvaluator.Evaluate(_c, t);
        bool spinning = t.Rpm > 100;

        if (spinning && !Seized)
        {
            int cylinders = _c.Geometry.Cylinders;
            foreach (var r in readings)
            {
                var info = FailureModeInfo.Of(r.Mode);
                var part = _c.Assembly.FindByCategory(r.Category);
                if (part == null || part.IsFailed) continue;
                if (info.IsInstant(r.Ratio, r.Load, r.Rating))
                {
                    part.Damage.RecordExposure(r.Mode, r.Ratio, dt);
                    Fail(part, r.Mode, t, readings, revLimitRpm, newFailures);
                    continue;
                }
                double rate = info.DamageRate(r.Ratio, r.Load, r.Rating, t.Rpm, cylinders);
                if (rate > ExposureThreshold) part.Damage.RecordExposure(r.Mode, r.Ratio, dt);
                if (part.Damage.Accumulate(r.Mode, rate * dt))
                    Fail(part, r.Mode, t, readings, revLimitRpm, newFailures);
            }
            ApplySpecialProcesses(t, dt, readings, revLimitRpm, newFailures);
        }

        Warnings = BuildWarnings(t, readings, sumpG);
        _failures.AddRange(newFailures);
        return new DamageStep(newFailures, Warnings);
    }

    private void ApplySpecialProcesses(EngineTelemetry t, double dt, IReadOnlyList<StressReading> readings, double revLimit, List<FailureReport> failures)
    {
        var pistons = _c.Part(PartCategory.Pistons);
        var gasket = _c.Part(PartCategory.HeadGasket);
        var rodBearings = _c.Part(PartCategory.RodBearings);
        var mainBearings = _c.Part(PartCategory.MainBearings);
        var head = _c.Part(PartCategory.CylinderHead);
        var springs = _c.Part(PartCategory.ValveSprings);

        // Detonation: each knocking combustion erodes the crown, with the square of knock intensity;
        // stronger pistons tolerate it better.
        double ki = t.KnockIntensity;
        if (ki > 0 && t.Firing)
        {
            double knockingCycles = t.Rpm / 120.0 * dt;
            double pistonTolerance = Math.Pow(120.0 / _c.Pistons.MaxCylinderPressureBar, 1.5);
            TryAccumulate(pistons, FailureMode.Detonation, KnockPistonDamagePerCycle * ki * ki * pistonTolerance * knockingCycles, dt, t, readings, revLimit, failures);
            TryAccumulate(gasket, FailureMode.HeadGasketBreach, KnockGasketDamagePerCycle * ki * ki * knockingCycles, dt, t, readings, revLimit, failures);
            TryAccumulate(rodBearings, FailureMode.RodBearingFatigue, KnockBearingDamagePerCycle * ki * ki * knockingCycles, dt, t, readings, revLimit, failures);
            pistons.Wear += KnockRingWearRate * ki * dt;
        }
        pistons.Wear += NormalRingWearRate * dt;

        // Lubrication: bearings wear fast without an oil film, faster under load and with very hot oil.
        double deficit = t.OilPressureRequired > 0 ? Math.Max(0.0, 1.0 - t.OilPressure / t.OilPressureRequired) : 0.0;
        double rodLoad = readings.FirstOrDefault(r => r.Mode == FailureMode.RodBearingFatigue).Ratio;
        double mainLoad = readings.FirstOrDefault(r => r.Mode == FailureMode.MainBearingFatigue).Ratio;
        double hotOil = Math.Max(0.0, t.OilC - 150.0) / 10.0 * HotOilWearRate;
        rodBearings.Wear += (StarvationWearRate * deficit * deficit * (0.3 + rodLoad) + NormalBearingWearRate + hotOil) * dt;
        mainBearings.Wear += (StarvationWearRate * deficit * deficit * (0.3 + mainLoad) + NormalBearingWearRate + hotOil) * dt;
        if (!rodBearings.IsFailed && rodBearings.Wear >= 1.0)
            Fail(rodBearings, FailureMode.OilStarvation, t, readings, revLimit, failures);

        // Overheating: hot coolant loosens the gasket's clamp; boiled-off coolant leaves hot spots in the head.
        double coolantC = t.CoolantC;
        double lost = 1.0 - t.CoolantLevel;
        double gasketRate = Math.Max(0.0, coolantC - 115.0) / 10.0 * OverheatGasketRate + CoolantLossGasketRate * lost;
        TryAccumulate(gasket, FailureMode.HeadGasketBreach, gasketRate * dt, dt, t, readings, revLimit, failures);
        if (lost > 0.2)
            TryAccumulate(head, FailureMode.CylinderHeadWarp, CoolantLossHeadRate * (lost - 0.2) / 0.1 * dt, dt, t, readings, revLimit, failures);

        // Compressor surge reverses the flow through the wheel every few milliseconds, hammering the
        // turbo's thrust bearing; the worn bearing lets the shaft drag (slower spool).
        if (t.CompressorSurgeDepth > 0 && _c.Turbo != null && _c.Assembly.FindByCategory(PartCategory.Turbocharger) is { } turbo)
            turbo.Wear += SurgeBearingWearRate * t.CompressorSurgeDepth * Math.Max(0.0, t.CompressorPressureRatio - 1.0) * dt;

        // Valve float hammers and fatigues the springs.
        if (t.ValveFloat && t.ValveFloatRpm > 0)
            springs.Wear += SpringFloatWearRate * (t.Rpm / t.ValveFloatRpm - 1.0) * dt;
    }

    private void TryAccumulate(PartInstance part, FailureMode mode, double amount, double dt, EngineTelemetry t,
        IReadOnlyList<StressReading> readings, double revLimit, List<FailureReport> failures)
    {
        if (part.IsFailed || amount <= 0) return;
        if (amount > ExposureThreshold * dt) part.Damage.RecordExposure(mode, 0.0, dt);
        if (part.Damage.Accumulate(mode, amount)) Fail(part, mode, t, readings, revLimit, failures);
    }

    private void Fail(PartInstance part, FailureMode mode, EngineTelemetry t, IReadOnlyList<StressReading> readings,
        double revLimit, List<FailureReport> failures)
    {
        if (part.IsFailed) return;
        var slot = _c.Assembly.Definition.Slots.First(s => _c.Assembly.PartIn(s.Id) == part);
        // Diagnose from the state just before the failure, then apply collateral damage.
        double prior = _fatigueAtStart.TryGetValue(part, out var start) ? start.GetValueOrDefault(mode) : 0.0;
        var report = FailureDiagnostics.Build(new FailureContext(_c, part, slot, mode, t, readings, History, revLimit, Array.Empty<string>(), prior));
        part.Damage.Fail(mode, t.Time);
        var collateral = ApplyCollateral(mode, t.Time);
        failures.Add(report with { CollateralDamage = collateral });
    }

    /// <summary>What else a failure destroys. Collateral damage is part of what makes failures expensive.</summary>
    private IReadOnlyList<string> ApplyCollateral(FailureMode mode, double time)
    {
        var notes = new List<string>();
        void Destroy(string category, string note)
        {
            var p = _c.Assembly.FindByCategory(category);
            if (p == null || p.IsFailed) return;
            p.Damage.Fail(mode, time, collateral: true, note: note);
            notes.Add($"{p.Definition.Name}: {note}");
        }
        void Wear(string category, double amount, string note)
        {
            var p = _c.Assembly.FindByCategory(category);
            if (p == null || p.IsFailed) return;
            p.Wear += amount;
            notes.Add($"{p.Definition.Name}: {note}");
        }
        switch (mode)
        {
            case FailureMode.RodTensileOverload:
            case FailureMode.RodCompressiveOverload:
                Destroy(PartCategory.Pistons, "destroyed when the rod let go");
                Destroy(PartCategory.Block, "holed by the broken connecting rod");
                Wear(PartCategory.Crankshaft, 0.6, "rod journal gouged");
                break;
            case FailureMode.PistonPressureOverload:
            case FailureMode.PistonCrownOverheat:
            case FailureMode.Detonation:
                Wear(PartCategory.Block, 0.3, "cylinder bore scored by piston debris");
                break;
            case FailureMode.RodBearingFatigue:
            case FailureMode.OilStarvation:
            case FailureMode.MainBearingFatigue:
                Wear(PartCategory.Crankshaft, 0.8, "journals scored and overheated (needs regrinding or replacement)");
                Wear(PartCategory.ConnectingRods, 0.5, "big ends overheated");
                break;
            case FailureMode.ValvePistonContact:
                Wear(PartCategory.Pistons, 0.4, "valve impressions in the crowns");
                Wear(PartCategory.ValveSprings, 0.5, "damaged by the valve strike");
                break;
            case FailureMode.CrankshaftOverspeed:
            case FailureMode.CrankshaftTorsion:
                Wear(PartCategory.Block, 0.5, "main bearing saddles damaged");
                break;
            case FailureMode.BlockDeckFailure:
                Destroy(PartCategory.HeadGasket, "blown when the deck lifted");
                break;
        }
        return notes;
    }

    /// <summary>Remaining life in words ("about 4 min left").</summary>
    public static string LifeText(double seconds) => seconds switch
    {
        double.PositiveInfinity => "no fatigue",
        < 1 => "failing now",
        < 120 => $"about {seconds:F0} s left",
        < 7200 => $"about {seconds / 60:F0} min left",
        _ => $"about {seconds / 3600:F0} h left",
    };

    private IReadOnlyList<EngineWarning> BuildWarnings(EngineTelemetry t, IReadOnlyList<StressReading> readings, double sumpG)
    {
        var w = new List<EngineWarning>();
        if (!t.Running && t.Rpm < 100) return w;
        if (t.KnockIntensity > 0.3)
            w.Add(new("knock", t.KnockIntensity > 2 ? WarningLevel.Danger : WarningLevel.Caution,
                $"Knock: timing {t.KnockIntensity:F1}° past the knock limit ({t.KnockLimitAdvance:F1}° BTDC)."));
        if (t.Rpm > 500 && t.OilPressure < t.OilPressureRequired)
            w.Add(new("oil_pressure", WarningLevel.Danger,
                $"Low oil pressure: {t.OilPressureBar:F2} bar, bearings need {Units.PaToBar(t.OilPressureRequired):F2} bar at {t.Rpm:F0} rpm."));
        if (sumpG > _c.OilPan.MaxSustainedG)
            w.Add(new("oil_surge", WarningLevel.Danger, $"Oil surge: {sumpG:F2} g exceeds what the oil pan can hold ({_c.OilPan.MaxSustainedG:F2} g)."));
        if (t.CoolantC > 105)
            w.Add(new("coolant_temp", t.CoolantC > 115 ? WarningLevel.Danger : WarningLevel.Caution, $"Coolant temperature {t.CoolantC:F0} °C."));
        if (t.CoolantLevel < 0.97)
            w.Add(new("coolant_loss", WarningLevel.Danger, $"Coolant boiling off: {t.CoolantLevel:P0} left."));
        if (t.OilC > 135)
            w.Add(new("oil_temp", t.OilC > 150 ? WarningLevel.Danger : WarningLevel.Caution, $"Oil temperature {t.OilC:F0} °C."));
        if (t.Firing && t.ManifoldPressure > 90_000 && t.Lambda > t.TargetLambda + 0.07)
            w.Add(new("lean", t.Lambda > 1.0 ? WarningLevel.Danger : WarningLevel.Caution,
                $"Running lean under load: λ {t.Lambda:F2} (target {t.TargetLambda:F2})."));
        if (t.FuelLimit != FuelLimit.None)
            w.Add(new("fuel_limit", WarningLevel.Caution, t.FuelLimit == FuelLimit.InjectorCapacity
                ? $"Injectors static: commanded duty {t.InjectorDuty:P0}."
                : $"Fuel pump cannot hold pressure: rail {Units.PaToKpa(t.FuelRailPressure):F0} kPa."));
        if (t.MapSensorSaturated)
            w.Add(new("map_saturated", WarningLevel.Danger,
                $"Manifold pressure {t.MapKpa:F0} kPa is beyond the ECU's MAP sensor ({Units.PaToKpa(t.EcuMapReading):F0} kPa): fuel and timing are wrong."));
        if (t.ValveFloat)
            w.Add(new("valve_float", WarningLevel.Danger, $"Valve float above {t.ValveFloatRpm:F0} rpm."));
        if (t.CompressorSurge)
            w.Add(new("surge", WarningLevel.Caution, "Compressor surge: the flow through the compressor is reversing, hammering the turbo's thrust bearing."));
        if (t.EgtC > 950)
            w.Add(new("egt", WarningLevel.Caution, $"Exhaust gas temperature {t.EgtC:F0} °C."));
        // Stress warnings say how long the part would last at this load, from the same law that damages it.
        foreach (var r in readings)
        {
            var info = FailureModeInfo.Of(r.Mode);
            var part = _c.Assembly.FindByCategory(r.Category);
            if (part == null || part.IsFailed) continue;
            double rate = info.DamageRate(r.Ratio, r.Load, r.Rating, t.Rpm, _c.Geometry.Cylinders);
            double life = info.IsInstant(r.Ratio, r.Load, r.Rating) ? 0.0
                : rate > 0 ? (1.0 - part.Damage.FatigueOf(r.Mode)) / rate : double.PositiveInfinity;
            if (r.Ratio < 1.0 && life > CautionLifeSeconds) continue;
            w.Add(new($"stress_{r.Mode}", r.Ratio >= 1.0 || life < DangerLifeSeconds ? WarningLevel.Danger : WarningLevel.Caution,
                $"{r.Quantity} {r.Load:F0} {r.Unit} is {r.Ratio:P0} of the rating ({r.Rating:F0} {r.Unit}): {LifeText(life)} at this load."));
        }
        return w;
    }
}
