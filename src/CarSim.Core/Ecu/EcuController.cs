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

    /// <summary>Table advance minus active knock retard, degrees BTDC.</summary>
    public double SparkAdvance(double rpm, double mapReading) => Tune.AdvanceAt(rpm, Units.PaToKpa(mapReading)) - KnockRetard;

    /// <summary>
    /// Speed-density air estimate. The calibration is assumed to model the engine's breathing
    /// correctly, so the estimate is exact while the MAP sensor is in range and falls short in
    /// proportion to how far the manifold pressure exceeds the sensor's range.
    /// </summary>
    public double EstimatedAirPerCycle(double trueAirPerCycle, double manifoldPressure)
    {
        if (manifoldPressure <= 0) return 0.0;
        return trueAirPerCycle * ReadMap(manifoldPressure) / manifoldPressure;
    }

    /// <summary>Fuel mass per cylinder per cycle the ECU wants, using its own stoichiometric AFR belief.</summary>
    public double CommandedFuelPerCycle(double estimatedAirPerCycle, double targetLambda) =>
        estimatedAirPerCycle / (targetLambda * Tune.FuelStoichAfr);

    /// <summary>Injector pulse width (s) to deliver <paramref name="fuelMass"/>, using the calibrated injector flow.</summary>
    public double PulseWidth(double fuelMass, double fuelDensity) =>
        fuelMass / (fuelDensity * Units.CcPerMinToM3PerSec(Tune.InjectorFlowCcMin));

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
