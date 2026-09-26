using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Knock from end-gas autoignition. The original knock limit was a sum of linear terms with a light-load
/// patch, and at full throttle it sat 4–11° above best-torque timing at every speed, so octane never limited
/// the naturally aspirated engine (review H5).
/// </summary>
public class KnockModelTests
{
    private static CombustionModel.KnockConditions Reference => new(
        Rpm: 3000, Octane: 95, CompressionRatio: 10.5, PortPressurePa: PhysicalConstants.StandardPressure,
        ChargeTemperatureK: 313.15, CoolantTemperatureK: 363.15, Lambda: 1.0, DeckClearanceMm: 0.0);

    [Fact]
    public void IgnitionDelayIsDouaudEyzat()
    {
        double expected = 17.68e-3 * Math.Pow(0.95, 3.402) * Math.Pow(40, -1.7) * Math.Exp(3800.0 / 800);
        Assert.Equal(expected, KnockModel.AutoignitionDelay(40 * PhysicalConstants.StandardPressure, 800, 95), 12);
        Assert.InRange(expected * 1000, 0.5, 5); // milliseconds: the same order as a combustion event
    }

    [Fact]
    public void TheKnockLimitIsWhereTheIntegralReachesOne()
    {
        var c = Reference;
        double klsa = KnockModel.KnockLimitedAdvance(c, 25);
        Assert.InRange(klsa, 12, 30);
        Assert.InRange(KnockModel.KnockIntegral(c, klsa), 0.95, 1.05);
        // Evaluated from timing well away from the limit, the estimate still lands on it.
        Assert.InRange(KnockModel.KnockLimitedAdvance(c, klsa + 8), klsa - 1, klsa + 1);
        Assert.InRange(KnockModel.KnockLimitedAdvance(c, klsa - 8), klsa - 1, klsa + 1);
        // Advance is monotone: earlier spark, more knock.
        Assert.True(KnockModel.KnockIntegral(c, 30) > KnockModel.KnockIntegral(c, 20));
    }

    [Fact]
    public void EveryFactorActsThroughTheEndGas()
    {
        double baseline = KnockModel.KnockLimitedAdvance(Reference, 20);
        double With(CombustionModel.KnockConditions c) => KnockModel.KnockLimitedAdvance(c, 20);
        Assert.True(With(Reference with { Octane = 98 }) > baseline + 1, "octane");
        Assert.True(With(Reference with { CompressionRatio = 12 }) < baseline - 2, "compression");
        Assert.True(With(Reference with { PortPressurePa = 180_000 }) < baseline - 5, "boost");
        Assert.True(With(Reference with { ChargeTemperatureK = 353 }) < baseline - 2, "hot charge");
        Assert.True(With(Reference with { CoolantTemperatureK = 383 }) < baseline, "hot coolant");
        Assert.True(With(Reference with { Lambda = 0.8 }) > baseline + 2, "rich mixture");
        Assert.True(With(Reference with { Rpm = 6000 }) > baseline + 5, "less time at high rpm");
        Assert.True(With(Reference with { DeckClearanceMm = 3 }) < baseline, "poor quench");
        Assert.True(With(Reference with { ExhaustToIntakePressureRatio = 2.0 }) < baseline, "more hot residual gas");
    }

    [Fact]
    public void UnderBoostFuelSensitivityCounts()
    {
        // E85 (RON 105, MON 88) against race fuel (RON 110, MON 102): naturally aspirated the race fuel's RON
        // wins; at 2.5 bar the ethanol blend's sensitivity makes it the more knock-resistant fuel.
        CombustionModel.KnockConditions Fuel(double ron, double mon, double pressure) =>
            Reference with { Octane = ron, OctaneSensitivity = ron - mon, PortPressurePa = pressure };
        Assert.Equal(105, KnockModel.OctaneIndex(Fuel(105, 88, PhysicalConstants.StandardPressure)), 9);
        Assert.True(KnockModel.OctaneIndex(Fuel(110, 102, PhysicalConstants.StandardPressure)) > KnockModel.OctaneIndex(Fuel(105, 88, PhysicalConstants.StandardPressure)));
        Assert.True(KnockModel.OctaneIndex(Fuel(105, 88, 250_000)) > KnockModel.OctaneIndex(Fuel(110, 102, 250_000)));
        Assert.True(KnockModel.KnockLimitedAdvance(Fuel(105, 88, 250_000), 15) > KnockModel.KnockLimitedAdvance(Fuel(110, 102, 250_000), 15));
    }

    [Fact]
    public void LightLoadCannotKnockWithoutAnySpecialCase()
    {
        // Overrun at 20 kPa with 45° of part-load advance: the end gas never gets hot and dense enough.
        var overrun = Reference with { PortPressurePa = 20_000, Rpm = 3500, ExhaustToIntakePressureRatio = 5 };
        Assert.True(KnockModel.KnockIntegral(overrun, 45) < 0.5);
        Assert.True(KnockModel.KnockLimitedAdvance(overrun, 45) > 60);
    }

    private static EngineTelemetry Wot(string fuel, double rpm, bool knockControl = false)
    {
        var tune = SimFactory.StockTune();
        tune.KnockControlEnabled = knockControl;
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel, tune);
        sim.DamageEnabled = false;
        return SimFactory.At(sim, rpm);
    }

    [Fact]
    public void TheNaturallyAspiratedEngineIsKnockLimitedLowDownOnPumpFuel()
    {
        foreach (string fuel in new[] { "gasoline_91", "gasoline_95" })
        {
            var low = Wot(fuel, 2000);
            Assert.True(low.KnockLimitAdvance < low.MbtAdvance - 3, $"{fuel} at 2000 rpm: limit {low.KnockLimitAdvance:F1}°, MBT {low.MbtAdvance:F1}°");
        }
        var high = Wot("gasoline_95", 5500);
        Assert.True(high.KnockLimitAdvance > high.MbtAdvance + 5, "not limited at high rpm (less time)");
        double k91 = Wot("gasoline_91", 2500).KnockLimitAdvance, k95 = Wot("gasoline_95", 2500).KnockLimitAdvance, k98 = Wot("gasoline_98", 2500).KnockLimitAdvance;
        Assert.True(k91 < k95 && k95 < k98, $"RON 91 {k91:F1}°, 95 {k95:F1}°, 98 {k98:F1}°");
        Assert.True(k98 - k91 > 2.5);
    }

    [Fact]
    public void TheFactoryCalibrationRespectsItsDesignFuel()
    {
        foreach (double rpm in new[] { 1000.0, 1500, 2000, 2500, 3500, 5000, 7000 })
            Assert.Equal(0.0, Wot("gasoline_95", rpm).KnockIntensity);
        // On RON 91 it knocks lugging at low rpm — which is what the knock sensor is for.
        Assert.True(Wot("gasoline_91", 2000).KnockIntensity > 0.3);
        var protectedByKnockControl = Wot("gasoline_91", 2000, knockControl: true);
        Assert.True(protectedByKnockControl.KnockRetard > 0.2);
        Assert.Equal(0.0, protectedByKnockControl.KnockIntensity, 1);
    }
}
