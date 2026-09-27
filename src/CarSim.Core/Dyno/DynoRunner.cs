using System.Globalization;
using System.Text;
using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Simulation;

namespace CarSim.Core.Dyno;

public enum DynoMode
{
    /// <summary>Full-throttle pull with engine speed ramping at a fixed rate (shows turbo lag).</summary>
    Sweep,
    /// <summary>Hold each speed step until readings settle (steady-state map).</summary>
    SteadyState,
}

public sealed record DynoSettings
{
    public DynoMode Mode { get; init; } = DynoMode.Sweep;
    public double StartRpm { get; init; } = 2000;
    public double EndRpm { get; init; } = 7500;
    public double RampRpmPerSecond { get; init; } = 500;
    public double StepRpm { get; init; } = 250;
    public double SettleSeconds { get; init; } = 1.0;
    public double PreSettleSeconds { get; init; } = 2.0;
    public double PrePullThrottle { get; init; } = 0.15;
    public double Throttle { get; init; } = 1.0;

    /// <summary>Test-cell coolant control; null runs on the engine's own radiator with the dyno fan.</summary>
    public double? CoolantTemperatureK { get; init; } = 363.15;

    /// <summary>Dyno fan air speed through the radiator, m/s.</summary>
    public double FanAirSpeed { get; init; } = 10.0;

    public double SampleIntervalSeconds { get; init; } = 0.05;
    public double TimeStep { get; init; } = 0.005;
}

public enum DynoPhase { Ready, Stabilising, Pulling, Finished, Aborted }

/// <summary>
/// Runs a dyno test incrementally so a UI can animate it (<see cref="Advance"/> per frame) or all at
/// once (<see cref="RunToCompletion"/>). A run stops if the engine fails.
/// </summary>
public sealed class DynoRunner
{
    private readonly List<EngineTelemetry> _samples = new();
    private double _phaseTime;
    private double _sinceSample;
    private double _targetRpm;
    private EngineTelemetry? _stepLast;
    private readonly int _failuresAtStart;

    public DynoRunner(EngineSimulation sim, DynoSettings settings, string name = "Run")
    {
        Sim = sim;
        Settings = settings;
        Name = name;
        _targetRpm = settings.StartRpm;
        _failuresAtStart = sim.Damage.Failures.Count;
    }

    public EngineSimulation Sim { get; }
    public DynoSettings Settings { get; }
    public string Name { get; }
    public DynoPhase Phase { get; private set; } = DynoPhase.Ready;
    public IReadOnlyList<EngineTelemetry> Samples => _samples;
    public EngineTelemetry? Current { get; private set; }
    public string AbortReason { get; private set; } = "";

    /// <summary>Why a completed run ended early (e.g. the rev limiter), empty if it reached the end speed.</summary>
    public string Note { get; private set; } = "";
    public double SimulatedSeconds { get; private set; }

    public bool IsDone => Phase is DynoPhase.Finished or DynoPhase.Aborted;

    /// <summary>0–1 progress through the speed range.</summary>
    public double Progress => IsDone ? 1.0 : MathUtil.InverseLerp(Settings.StartRpm, Settings.EndRpm, _targetRpm);

    /// <summary>Advances the test by <paramref name="simSeconds"/> of simulated time.</summary>
    public void Advance(double simSeconds)
    {
        if (IsDone) return;
        if (Sim.Damage.Seized)
        {
            Abort("The engine is seized; repair it before testing.");
            return;
        }
        if (Phase == DynoPhase.Ready) Phase = DynoPhase.Stabilising;
        double dt = Settings.TimeStep;
        int steps = Math.Max(1, (int)Math.Round(simSeconds / dt));
        for (int i = 0; i < steps && !IsDone; i++) StepOnce(dt);
    }

    public DynoRun RunToCompletion(double maxSimSeconds = 600)
    {
        while (!IsDone && SimulatedSeconds < maxSimSeconds) Advance(0.5);
        if (!IsDone) Abort("Time limit reached.");
        return Result();
    }

    private void StepOnce(double dt)
    {
        var s = Settings;
        bool pulling = Phase == DynoPhase.Pulling;
        double throttle = Phase == DynoPhase.Stabilising && s.Mode == DynoMode.Sweep ? s.PrePullThrottle : s.Throttle;
        var input = new EngineInputs
        {
            Throttle = throttle,
            SpeedMode = SpeedMode.Held,
            HeldRpm = _targetRpm,
            CoolantTemperatureOverride = s.CoolantTemperatureK,
            CoolingAirSpeed = s.FanAirSpeed,
        };
        var t = Sim.Step(dt, input);
        Current = t;
        SimulatedSeconds += dt;
        _phaseTime += dt;

        if (Sim.Damage.Failures.Count > _failuresAtStart)
        {
            if (pulling || s.Mode == DynoMode.SteadyState) _samples.Add(t);
            var f = Sim.Damage.Failures[^1];
            if (Sim.Damage.Seized)
            {
                Abort($"{f.Title} at {t.Rpm:F0} rpm.");
                return;
            }
        }

        switch (s.Mode)
        {
            case DynoMode.Sweep:
                if (Phase == DynoPhase.Stabilising)
                {
                    if (_phaseTime >= s.PreSettleSeconds) { Phase = DynoPhase.Pulling; _phaseTime = 0; _sinceSample = s.SampleIntervalSeconds; }
                    return;
                }
                if (t.RevLimiterActive)
                {
                    // An absorption dyno can only load the engine: the pull ends where the engine stops accelerating.
                    Note = $"Reached the rev limiter at {t.Rpm:F0} rpm.";
                    Phase = DynoPhase.Finished;
                    return;
                }
                _sinceSample += dt;
                if (_sinceSample >= s.SampleIntervalSeconds - 1e-9)
                {
                    _samples.Add(t);
                    _sinceSample = 0;
                }
                _targetRpm += s.RampRpmPerSecond * dt;
                if (_targetRpm > s.EndRpm) Phase = DynoPhase.Finished;
                break;

            case DynoMode.SteadyState:
                if (Phase == DynoPhase.Stabilising) Phase = DynoPhase.Pulling;
                _stepLast = t;
                if (t.RevLimiterActive)
                {
                    Note = $"Reached the rev limiter at {t.Rpm:F0} rpm.";
                    Phase = DynoPhase.Finished;
                    return;
                }
                if (_phaseTime >= s.SettleSeconds)
                {
                    _samples.Add(_stepLast);
                    _phaseTime = 0;
                    _targetRpm += s.StepRpm;
                    if (_targetRpm > s.EndRpm + 1e-6) Phase = DynoPhase.Finished;
                }
                break;
        }
    }

    private void Abort(string reason)
    {
        Phase = DynoPhase.Aborted;
        AbortReason = reason;
    }

    public DynoRun Result() => new(
        Name,
        Settings,
        _samples.ToList(),
        Sim.Damage.Failures.Skip(_failuresAtStart).ToList(),
        Sim.Config.Assembly.Definition.Slots
            .Where(sl => Sim.Config.Assembly.PartIn(sl.Id) != null)
            .Select(sl => $"{sl.Label}: {Sim.Config.Assembly.PartIn(sl.Id)!.Definition.Name}").ToList(),
        Sim.Ecu.Tune.Name,
        Sim.Config.Fuel.Name,
        Phase == DynoPhase.Finished,
        AbortReason,
        Note);
}

/// <summary>A finished (or aborted) dyno test.</summary>
public sealed record DynoRun(
    string Name,
    DynoSettings Settings,
    IReadOnlyList<EngineTelemetry> Samples,
    IReadOnlyList<FailureReport> Failures,
    IReadOnlyList<string> Build,
    string TuneName,
    string FuelName,
    bool Completed,
    string AbortReason,
    string Note = "")
{
    public EngineTelemetry? PeakPower => Samples.Where(s => s.Firing).MaxBy(s => s.Power);
    public EngineTelemetry? PeakTorque => Samples.Where(s => s.Firing).MaxBy(s => s.Torque);

    /// <summary>Linear interpolation of a channel against rpm (samples are ordered by rpm).</summary>
    public double? At(double rpm, Func<EngineTelemetry, double> channel)
    {
        if (Samples.Count == 0 || rpm < Samples[0].Rpm || rpm > Samples[^1].Rpm) return null;
        for (int i = 1; i < Samples.Count; i++)
        {
            if (Samples[i].Rpm >= rpm)
            {
                var a = Samples[i - 1];
                var b = Samples[i];
                double span = b.Rpm - a.Rpm;
                double t = span > 1e-9 ? (rpm - a.Rpm) / span : 0.0;
                return channel(a) + (channel(b) - channel(a)) * t;
            }
        }
        return channel(Samples[^1]);
    }

    /// <summary>
    /// Logged channels — what a dyno cell and its data logger can measure. Model internals (best-torque
    /// timing, the knock-limited advance) are deliberately not exported; players find them by experiment.
    /// </summary>
    public static readonly (string Header, Func<EngineTelemetry, double> Value)[] CsvChannels =
    {
        ("time_s", t => t.Time), ("rpm", t => t.Rpm), ("torque_nm", t => t.Torque), ("power_kw", t => t.PowerKw),
        ("power_hp", t => t.PowerHp), ("map_kpa", t => t.MapKpa), ("boost_kpa", t => t.BoostKpa), ("lambda", t => t.Lambda),
        ("afr", t => t.Afr), ("target_lambda", t => t.TargetLambda), ("injector_duty", t => t.InjectorDuty),
        ("ignition_deg", t => t.IgnitionAdvance), ("intake_cam_deg", t => t.IntakeCamAdvance), ("knock_level", t => (double)t.KnockSensorLevel), ("knock_retard_deg", t => t.KnockRetard), ("ve", t => t.VolumetricEfficiency),
        ("pcp_bar", t => t.PeakCylinderPressureBar), ("egt_c", t => t.EgtC), ("coolant_c", t => t.CoolantC), ("oil_c", t => t.OilC),
        ("oil_bar", t => t.OilPressureBar), ("iat_c", t => Units.KToC(t.ManifoldTemperature)), ("turbo_rpm", t => t.TurboRpm),
        ("wastegate", t => t.WastegateOpening),
    };

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvChannels.Select(c => c.Header)));
        foreach (var s in Samples)
            sb.AppendLine(string.Join(",", CsvChannels.Select(c => c.Value(s).ToString("G6", CultureInfo.InvariantCulture))));
        return sb.ToString();
    }
}

/// <summary>Differences between two runs — the core of "change one thing, re-run, compare".</summary>
public static class DynoComparison
{
    public sealed record Summary(double PeakPowerDeltaW, double PeakTorqueDeltaNm, double PeakPowerRpmA, double PeakPowerRpmB,
        double AreaUnderPowerDeltaPercent, IReadOnlyList<(double Rpm, double TorqueA, double TorqueB)> Points);

    public static Summary Compare(DynoRun a, DynoRun b, double stepRpm = 250)
    {
        var points = new List<(double, double, double)>();
        double from = Math.Max(a.Samples.FirstOrDefault()?.Rpm ?? 0, b.Samples.FirstOrDefault()?.Rpm ?? 0);
        double to = Math.Min(a.Samples.LastOrDefault()?.Rpm ?? 0, b.Samples.LastOrDefault()?.Rpm ?? 0);
        double areaA = 0, areaB = 0;
        for (double rpm = Math.Ceiling(from / stepRpm) * stepRpm; rpm <= to; rpm += stepRpm)
        {
            double ta = a.At(rpm, t => t.Torque) ?? 0, tb = b.At(rpm, t => t.Torque) ?? 0;
            points.Add((rpm, ta, tb));
            areaA += ta * rpm;
            areaB += tb * rpm;
        }
        return new Summary(
            (b.PeakPower?.Power ?? 0) - (a.PeakPower?.Power ?? 0),
            (b.PeakTorque?.Torque ?? 0) - (a.PeakTorque?.Torque ?? 0),
            a.PeakPower?.Rpm ?? 0, b.PeakPower?.Rpm ?? 0,
            areaA > 0 ? (areaB / areaA - 1.0) * 100.0 : 0.0,
            points);
    }
}
