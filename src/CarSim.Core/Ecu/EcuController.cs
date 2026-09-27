using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Ecu;

/// <summary>
/// Runtime ECU behaviour: sensor limits, table lookups, fuel metering, rev limiter, idle control and
/// knock control. The ECU only knows what its sensors and calibration tell it — that gap between
/// belief and reality is where many tuning mistakes come from.
/// </summary>
public sealed class EcuController
{
    public const double RevLimiterHysteresisRpm = 150.0;
    public const double MaxKnockRetardDeg = 10.0;
    public const double KnockRetardRateDegPerSecPerDeg = 6.0;
    public const double KnockRecoveryDegPerSec = 1.0;
    public const double IdleValveBase = 0.25;

    private double _idleIntegral;

    public EcuController(EcuSpec hardware, EcuTune tune)
    {
        Hardware = hardware;
        Tune = tune;
    }

    public EcuSpec Hardware { get; }
    public EcuTune Tune { get; set; }

    public double KnockRetard { get; private set; }
    public bool RevLimiterActive { get; private set; }
    public double IdleValve { get; private set; } = IdleValveBase;

    /// <summary>Rev limit actually enforced: the tune's, capped by what the hardware allows.</summary>
    public double EffectiveRevLimit => Math.Min(Tune.RevLimitRpm, Hardware.MaxRevLimitRpm);

    public bool KnockControlActive => Hardware.KnockControl && Tune.KnockControlEnabled;

    /// <summary>Manifold pressure as the ECU's MAP sensor reports it (clipped at the sensor's range).</summary>
    public double ReadMap(double manifoldPressure) => Math.Min(manifoldPressure, Hardware.MapSensorMax);

    public double TargetLambda(double rpm, double mapReading) => Tune.LambdaAt(rpm, Units.PaToKpa(mapReading));

    /// <summary>
    /// Intake cam advance the ECU commands, crank degrees from the park position: its table at the speed and the MAP it
    /// reads, within <paramref name="phaserRangeDeg"/> (0 when there is no phaser or the ECU cannot drive one).
    /// </summary>
    public double IntakeCamAdvanceTarget(double rpm, double mapReading, double phaserRangeDeg) =>
        phaserRangeDeg > 0 && Hardware.CamPhaseControl
            ? MathUtil.Clamp(Tune.IntakeCamAdvanceAt(rpm, Units.PaToKpa(mapReading)), 0.0, phaserRangeDeg)
            : 0.0;

    /// <summary>How far below a switch speed a switched valvetrain or intake stage switches back, rpm (no chatter at the threshold).</summary>
    public const double SwitchHysteresisRpm = 150.0;

    /// <summary>
    /// Whether variable-valve-lift camshafts should run their high-lift profile: above the tune's switch speed (with
    /// hysteresis from <paramref name="wasHigh"/>), with an ECU that can drive them, while the engine runs (the switching
    /// pins are oil-pressure operated).
    /// </summary>
    public bool HighValveLift(double rpm, bool wasHigh, bool running) =>
        running && Hardware.ValveLiftControl && Tune.ValveLiftSwitchRpm is double on
        && rpm >= (wasHigh ? on - SwitchHysteresisRpm : on);

    /// <summary>
    /// The runner stage a variable intake with <paramref name="stages"/> stages should run (0 = the primary runner): the
    /// highest stage whose switch speed the engine has reached, where a stage it already runs (<paramref name="current"/>)
    /// is kept down to its switch speed less the hysteresis — the rule of <see cref="HighValveLift"/>, stage by stage.
    /// 0 without an ECU that can drive the intake or while the engine is not running; a stage without a switch speed in
    /// the tune is never reached, nor any above it.
    /// </summary>
    public int IntakeRunnerStage(double rpm, int current, int stages, bool running)
    {
        if (!running || !Hardware.IntakeRunnerControl) return 0;
        int stage = 0;
        for (int k = 1; k < stages; k++)
        {
            if (Tune.IntakeRunnerSwitchRpmOf(k) is not double on || rpm < (current >= k ? on - SwitchHysteresisRpm : on)) break;
            stage = k;
        }
        return stage;
    }

    /// <summary>Table advance minus active knock retard, degrees BTDC.</summary>
    public double SparkAdvance(double rpm, double mapReading) => Tune.AdvanceAt(rpm, Units.PaToKpa(mapReading)) - KnockRetard;

    /// <summary>
    /// Speed-density air estimate from the ECU's own sensors and calibration:
    /// m = VE_table(rpm, MAP_read) · MAP_read · (displacement / cylinders) / (R · IAT). The ECU does not
    /// know the engine's real breathing: when cams, head, exhaust or boost hardware change it, the table is
    /// wrong and the mixture drifts until it is re-tuned. Beyond the MAP sensor's range the reading is
    /// clipped, so the estimate falls short (a stock ECU on boost runs lean).
    /// </summary>
    /// <param name="rpm">Engine speed.</param>
    /// <param name="manifoldPressure">True manifold pressure, Pa (the sensor clips it).</param>
    /// <param name="intakeAirTemperature">Manifold air temperature at the IAT sensor, K.</param>
    /// <param name="cylinders">Cylinder count (the ECU's injection pattern).</param>
    public double EstimatedAirPerCycle(double rpm, double manifoldPressure, double intakeAirTemperature, int cylinders)
    {
        if (manifoldPressure <= 0 || intakeAirTemperature <= 0 || cylinders <= 0) return 0.0;
        double map = ReadMap(manifoldPressure);
        double ve = Tune.VolumetricEfficiencyAt(rpm, Units.PaToKpa(map));
        double cylinderVolume = Units.CcToM3(Tune.DisplacementCc) / cylinders;
        return ve * map * cylinderVolume / (PhysicalConstants.AirGasConstant * intakeAirTemperature);
    }

    /// <summary>Fuel mass per cylinder per cycle the ECU wants, using its own stoichiometric AFR belief.</summary>
    public double CommandedFuelPerCycle(double estimatedAirPerCycle, double targetLambda) =>
        estimatedAirPerCycle / (targetLambda * Tune.FuelStoichAfr);

    /// <summary>
    /// Injector pulse width (s) to deliver <paramref name="fuelMass"/>, from the ECU's own injector and fuel
    /// calibration: fuel volume at the density it believes, over the flow it believes, plus the dead time it
    /// believes. It never sees the real fuel or injectors, so a fuel or injector swap is wrong until re-calibrated.
    /// </summary>
    public double PulseWidth(double fuelMass)
    {
        if (!(fuelMass > 0)) return 0.0;
        double density = Tune.FuelDensityKgL * 1000.0;
        return fuelMass / (density * Units.CcPerMinToM3PerSec(Tune.InjectorFlowCcMin)) + Tune.InjectorDeadTimeMs / 1000.0;
    }

    /// <summary>Fuel-cut rev limiter with hysteresis. Returns true while fuel is cut.</summary>
    public bool UpdateRevLimiter(double rpm)
    {
        double limit = EffectiveRevLimit;
        if (rpm >= limit) RevLimiterActive = true;
        else if (rpm <= limit - RevLimiterHysteresisRpm) RevLimiterActive = false;
        return RevLimiterActive;
    }

    /// <summary>Idle air control: PI loop on engine speed while the throttle is closed.</summary>
    public double UpdateIdle(double rpm, double throttle, bool running, double dt)
    {
        if (!running)
        {
            IdleValve = 0.5;
            return IdleValve;
        }
        if (throttle < 0.02)
        {
            double error = (Tune.IdleRpm - rpm) / Tune.IdleRpm;
            _idleIntegral = MathUtil.Clamp(_idleIntegral + 1.2 * error * dt, -0.3, 0.8);
            IdleValve = MathUtil.Clamp(IdleValveBase + 1.5 * error + _idleIntegral, 0.0, 1.0);
        }
        return IdleValve;
    }

    /// <summary>Knock control: retard quickly while knock is detected, recover slowly.</summary>
    public void UpdateKnockControl(double knockIntensity, double dt)
    {
        if (!KnockControlActive)
        {
            KnockRetard = 0.0;
            return;
        }
        if (knockIntensity > 0.0)
            KnockRetard = Math.Min(MaxKnockRetardDeg, KnockRetard + KnockRetardRateDegPerSecPerDeg * knockIntensity * dt);
        else
            KnockRetard = Math.Max(0.0, KnockRetard - KnockRecoveryDegPerSec * dt);
    }

    public void Reset()
    {
        KnockRetard = 0.0;
        RevLimiterActive = false;
        IdleValve = IdleValveBase;
        _idleIntegral = 0.0;
    }
}
