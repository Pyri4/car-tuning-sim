using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

public class CombustionModelTests
{
    private static CombustionModel.KnockConditions Reference => new(
        Rpm: 3000, Octane: 95, CompressionRatio: 10.5, PortPressurePa: PhysicalConstants.StandardPressure,
        ChargeTemperatureK: 313.15, CoolantTemperatureK: 363.15, Lambda: 1.0, DeckClearanceMm: 0.0);

    [Fact]
    public void IndicatedEfficiencyRisesWithCompressionWithDiminishingReturns()
    {
        double e8 = CombustionModel.IndicatedEfficiency(8), e10 = CombustionModel.IndicatedEfficiency(10), e12 = CombustionModel.IndicatedEfficiency(12);
        Assert.True(e10 > e8 && e12 > e10);
        Assert.True(e12 - e10 < e10 - e8);
        Assert.InRange(e10, 0.35, 0.45);
    }

    [Fact]
    public void MbtAdvanceGrowsWithRpmAndShrinksWithChargeDensity()
    {
        Assert.True(CombustionModel.MbtAdvance(6000, 1) > CombustionModel.MbtAdvance(2000, 1));
        Assert.True(CombustionModel.MbtAdvance(4000, 2) < CombustionModel.MbtAdvance(4000, 1));
    }

    [Fact]
    public void SparkFactorPeaksAtMbt()
    {
        Assert.Equal(1.0, CombustionModel.SparkFactor(25, 25));
        Assert.True(CombustionModel.SparkFactor(15, 25) < 1.0);
        Assert.True(CombustionModel.SparkFactor(35, 25) < 1.0);
        // 10° retard costs about 3 %.
        Assert.Equal(0.97, CombustionModel.SparkFactor(15, 25), 3);
    }

    [Fact]
    public void MixtureFactorGivesBestTorqueSlightlyRich()
    {
        double Torque(double lambda) => lambda <= 1 ? CombustionModel.MixtureFactor.Evaluate(lambda)
                                                     : CombustionModel.MixtureFactor.Evaluate(lambda) / lambda;
        double best = new[] { 0.7, 0.8, 0.85, 0.88, 0.9, 0.95, 1.0, 1.1, 1.2 }.MaxBy(Torque);
        Assert.InRange(best, 0.85, 0.9);
        Assert.True(Torque(1.1) < Torque(1.0));
        Assert.True(Torque(1.5) < 0.5, "very lean mixtures misfire");
    }

    [Fact]
    public void KnockLimitReferencePoint()
    {
        Assert.Equal(24.0, CombustionModel.KnockLimitedAdvance(Reference), 9);
    }

    [Fact]
    public void KnockLimitRespondsToEachFactorInTheRightDirection()
    {
        double baseline = CombustionModel.KnockLimitedAdvance(Reference);
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { Octane = 98 }) > baseline, "octane");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { CompressionRatio = 12 }) < baseline, "compression");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { PortPressurePa = 180_000 }) < baseline, "boost");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { ChargeTemperatureK = 353 }) < baseline, "hot charge");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { CoolantTemperatureK = 383 }) < baseline, "hot coolant");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { Lambda = 0.8 }) > baseline, "rich mixture");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { Rpm = 6000 }) > baseline, "less time at high rpm");
        Assert.True(CombustionModel.KnockLimitedAdvance(Reference with { DeckClearanceMm = 3 }) < baseline, "poor quench");
    }

    [Fact]
    public void PeakCylinderPressureRisesWithBoostAdvanceAndKnock()
    {
        double baseline = CombustionModel.PeakCylinderPressure(100_000, 10, 12e5, 25, 25, 0);
        Assert.InRange(Units.PaToBar(baseline), 55, 75);
        Assert.True(CombustionModel.PeakCylinderPressure(200_000, 10, 22e5, 20, 20, 0) > baseline);
        Assert.True(CombustionModel.PeakCylinderPressure(100_000, 10, 12e5, 35, 25, 0) > baseline);
        Assert.True(CombustionModel.PeakCylinderPressure(100_000, 10, 12e5, 25, 25, 5) > baseline);
    }

    [Fact]
    public void FrictionGrowsWithPistonSpeedAndColdOil()
    {
        double hot = OilViscosity.At(Units.CToK(100));
        double cold = OilViscosity.At(Units.CToK(20));
        Assert.True(CombustionModel.FrictionMep(20, hot, 560, 6e6) > CombustionModel.FrictionMep(10, hot, 560, 6e6));
        Assert.True(CombustionModel.FrictionMep(10, cold, 560, 6e6) > CombustionModel.FrictionMep(10, hot, 560, 6e6));
    }

    [Fact]
    public void OilViscosityFallsWithTemperature()
    {
        Assert.Equal(0.010, OilViscosity.At(Units.CToK(100)), 6);
        Assert.True(OilViscosity.At(Units.CToK(20)) > 10 * OilViscosity.At(Units.CToK(100)));
        Assert.True(OilViscosity.At(Units.CToK(140)) < OilViscosity.At(Units.CToK(100)));
    }
}
