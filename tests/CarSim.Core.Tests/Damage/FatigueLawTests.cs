using CarSim.Core.Damage;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Damage;

/// <summary>
/// The damage laws. The original model accumulated fatigue per second with a cubic below the rating and a
/// kink above it (rods at 90 % of their rating failed after 4 minutes), compared temperatures as Celsius
/// ratios, treated rpm ratios as stress ratios, and forgot a part's history between sessions.
/// </summary>
public class FatigueLawTests
{
    private static FailureModeInfo Rods => FailureModeInfo.Of(FailureMode.RodTensileOverload);

    private static double LifeSeconds(FailureModeInfo info, double ratio, double rpm = 7000) =>
        1.0 / info.DamageRate(ratio, ratio, 1.0, rpm, 4);

    [Fact]
    public void RodsBelowTheirRatingLastHoursNotMinutes()
    {
        Assert.Equal(double.PositiveInfinity, LifeSeconds(Rods, Rods.Endurance));
        Assert.True(LifeSeconds(Rods, 0.85) > 100 * 3600, "85 %: effectively unlimited");
        Assert.True(LifeSeconds(Rods, 0.90) > 5 * 3600, $"90 %: {LifeSeconds(Rods, 0.9) / 3600:F1} h");
        Assert.InRange(LifeSeconds(Rods, 1.00), 5 * 60, 30 * 60);
        Assert.InRange(LifeSeconds(Rods, 1.10), 20, 3 * 60);
        Assert.True(LifeSeconds(Rods, 1.25) < 30, "well past the rating it goes quickly");
    }

    [Fact]
    public void DamageIsCountedPerLoadCycle()
    {
        // Same load, twice the speed: twice the cycles per second, half the running time to failure.
        Assert.Equal(2.0, LifeSeconds(Rods, 1.05, 3500) / LifeSeconds(Rods, 1.05, 7000), 9);
        var info = FailureModeInfo.Of(FailureMode.CrankshaftTorsion);
        Assert.Equal(info.CyclesPerSecond(6000, 4) * 1.5, info.CyclesPerSecond(6000, 6), 9);
        Assert.Equal(0.0, Rods.DamageRate(1.1, 1.1, 1.0, 0, 4));
    }

    [Theory]
    [InlineData(FailureMode.RodTensileOverload)]
    [InlineData(FailureMode.CrankshaftOverspeed)]
    [InlineData(FailureMode.GearboxOverload)]
    [InlineData(FailureMode.TurboOverspeed)]
    public void TheCurveIsSmoothAndRisingThroughTheRating(FailureMode mode)
    {
        var info = FailureModeInfo.Of(mode);
        double Rate(double r) => info.DamageRate(r, r, 1.0, 6000, 4);
        const double h = 1e-7;
        double below = (Rate(1.0) - Rate(1.0 - h)) / h, above = (Rate(1.0 + h) - Rate(1.0)) / h;
        Assert.Equal(1.0, above / below, 2);
        double last = 0;
        for (double r = info.DamageOnsetLoadRatio + 0.01; r < 1.3; r += 0.01)
        {
            Assert.True(Rate(r) > last, $"{mode} at {r:F2}");
            last = Rate(r);
        }
    }

    [Fact]
    public void SpeedRatedPartsAreStressedWithTheSquareOfSpeed()
    {
        var flywheel = FailureModeInfo.Of(FailureMode.FlywheelBurst);
        Assert.Equal(1.21, flywheel.StressRatio(1.1), 9);
        Assert.False(flywheel.IsInstant(1.13, 1.13, 1.0));
        Assert.True(flywheel.IsInstant(1.15, 1.15, 1.0), "hoop stress at 115 % speed is 132 % of the rating: it bursts");
        Assert.Equal(Math.Sqrt(flywheel.Endurance), flywheel.DamageOnsetLoadRatio, 9);
    }

    [Fact]
    public void ThermalDamageIsArrheniusInKelvin()
    {
        var crown = FailureModeInfo.Of(FailureMode.PistonCrownOverheat);
        double Life(double c, double rating = 300) => 1.0 / crown.DamageRate(c / rating, c, rating, 5000, 4);
        Assert.Equal(crown.LifeAtRating, Life(300), 6);
        // 280 °C on a 300 °C piston used to count as 93 % "stress" and last 9 minutes.
        Assert.True(Life(280) > 60 * 60, $"{Life(280) / 60:F0} min");
        Assert.True(Life(180) > 1000 * 3600, "normal running barely ages a piston");
        // No threshold or kink: each 10 K costs about the same factor, set by the activation temperature.
        double[] factors = { Life(290) / Life(300), Life(300) / Life(310), Life(310) / Life(320) };
        Assert.All(factors, f => Assert.InRange(f, 1.6, 2.1));
        Assert.Equal(Math.Exp(crown.ActivationK * (1 / 573.15 - 1 / 593.15)), Life(300) / Life(320), 9);
        // Absolute temperature, not a Celsius ratio: the same 20 K matters less to a part rated hotter
        // (Arrhenius: the factor is exp(Θ·ΔT/T²)).
        Assert.True(Life(280) / Life(300) > Life(380, 400) / Life(400, 400));
        Assert.True(Life(380, 400) / Life(400, 400) > 1.5);
        Assert.True(crown.IsInstant(1.4, 401, 300) && !crown.IsInstant(1.3, 390, 300));
    }

    [Fact]
    public void StressWarningsSayHowLongThePartWouldLast()
    {
        var sim = SimFactory.Create(("valve_springs", "k20.valve_springs.performance"), ("flywheel", "k20.flywheel.light"),
            ("crankshaft", "k20.crankshaft.forged"));
        var input = new EngineInputs { Throttle = 0, SpeedMode = SpeedMode.Held, HeldRpm = 9800, CoolantTemperatureOverride = 363.15 };
        sim.Step(0.005, input);
        var rods = Assert.Single(sim.Damage.Warnings, w => w.Code == $"stress_{FailureMode.RodTensileOverload}");
        Assert.Equal(WarningLevel.Danger, rods.Level);
        Assert.Contains("s left", rods.Message);
        // At the rev limiter the stock engine is inside every fatigue limit: no stress warnings at all.
        var stock = SimFactory.Create();
        stock.Step(0.005, new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 7400, CoolantTemperatureOverride = 363.15 });
        Assert.DoesNotContain(stock.Damage.Warnings, w => w.Code.StartsWith("stress_"));
    }

    [Fact]
    public void AFailureReportExplainsDamageFromEarlierSessions()
    {
        var a = SimFactory.Assembly(("valve_springs", "k20.valve_springs.performance"), ("flywheel", "k20.flywheel.light"),
            ("crankshaft", "k20.crankshaft.forged"));
        var input = new EngineInputs { Throttle = 0, SpeedMode = SpeedMode.Held, HeldRpm = 9800, CoolantTemperatureOverride = 363.15 };
        var rods = a.PartIn("connecting_rods")!;

        // Session 1: abuse, but stop before anything breaks.
        var first = SimFactory.Create(a);
        while (rods.Damage.FatigueOf(FailureMode.RodTensileOverload) < 0.7) first.Step(0.005, input);
        Assert.Empty(first.Damage.Failures);
        var ledger = rods.Damage.ExposureOf(FailureMode.RodTensileOverload);
        Assert.True(ledger.Seconds > 10 && ledger.PeakRatio > 1.05, $"{ledger}");

        // Session 2: a short blast finishes them, and the report says most of the damage was old.
        var second = SimFactory.Create(a);
        for (int i = 0; i < 12_000 && second.Damage.Failures.Count == 0; i++) second.Step(0.005, input);
        var report = Assert.Single(second.Damage.Failures);
        Assert.Equal(FailureMode.RodTensileOverload, report.Mode);
        Assert.True(report.Time < 0.5 * ledger.Seconds, "only the remaining 30 % of life was used this session");
        Assert.Contains(report.Measurements, m => m.Label.Contains("already used") && m.Value.StartsWith("70"));
        Assert.Contains(report.Measurements, m => m.Label.Contains("damaging load") && m.Value.Contains("% of the rating"));
        Assert.Contains(report.ContributingFactors, f => f.Contains("earlier running"));
    }
}
