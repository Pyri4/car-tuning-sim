namespace CarSim.Core.Damage;

public enum WarningLevel { Caution, Danger }

/// <summary>A live condition the player should notice (dashboard light / dyno alarm).</summary>
public sealed record EngineWarning(string Code, WarningLevel Level, string Message);

/// <summary>Extremes seen while the engine ran — context for diagnosing a failure.</summary>
public sealed class OperatingHistory
{
    public double RunningSeconds;
    public double MaxRpm;
    public double MaxPeakCylinderPressureBar;
    public double MaxKnockIntensity;
    public double KnockSeconds;
    public double MaxCoolantC;
    public double MaxOilC;
    public double MinOilPressureMargin = double.PositiveInfinity;
    public double SecondsBelowRequiredOilPressure;
    public double MaxCrownC;
    public double MaxLambdaUnderLoad;
    public double MaxBoostKpa;
    public double MaxTurboRpm;
    public double MaxTurbineInletC;
    public double SecondsValveFloat;
    public double SecondsRevLimiter;
    public double MaxSumpG;
    public double MaxKnockRetard;
    public double MinCoolantLevel = 1.0;

    public void Record(Simulation.EngineTelemetry t, double sumpG, double dt)
    {
        if (!t.Running && t.Rpm < 100) return;
        RunningSeconds += dt;
        MaxRpm = Math.Max(MaxRpm, t.Rpm);
        MaxPeakCylinderPressureBar = Math.Max(MaxPeakCylinderPressureBar, t.PeakCylinderPressureBar);
        MaxKnockIntensity = Math.Max(MaxKnockIntensity, t.KnockIntensity);
        if (t.KnockIntensity > 0.3) KnockSeconds += dt;
        MaxCoolantC = Math.Max(MaxCoolantC, t.CoolantC);
        MaxOilC = Math.Max(MaxOilC, t.OilC);
        if (t.Rpm > 1000 && t.OilPressureRequired > 0)
        {
            double margin = t.OilPressure / t.OilPressureRequired;
            MinOilPressureMargin = Math.Min(MinOilPressureMargin, margin);
            if (margin < 1.0) SecondsBelowRequiredOilPressure += dt;
        }
        MaxCrownC = Math.Max(MaxCrownC, Common.Units.KToC(t.PistonCrownTemperature));
        if (t.Firing && t.ManifoldPressure > 90_000) MaxLambdaUnderLoad = Math.Max(MaxLambdaUnderLoad, t.Lambda);
        MaxBoostKpa = Math.Max(MaxBoostKpa, t.BoostKpa);
        MaxTurboRpm = Math.Max(MaxTurboRpm, t.TurboRpm);
        MaxTurbineInletC = Math.Max(MaxTurbineInletC, Common.Units.KToC(t.TurbineInletTemperature));
        if (t.ValveFloat) SecondsValveFloat += dt;
        if (t.RevLimiterActive) SecondsRevLimiter += dt;
        MaxSumpG = Math.Max(MaxSumpG, sumpG);
        MaxKnockRetard = Math.Max(MaxKnockRetard, t.KnockRetard);
        MinCoolantLevel = Math.Min(MinCoolantLevel, t.CoolantLevel);
    }
}
