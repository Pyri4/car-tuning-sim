using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>Fuel delivery limits and ECU calibration mistakes.</summary>
public class FuelingTests
{
    [Fact]
    public void StockEngineHitsTargetLambda()
    {
        var t = SimFactory.At(SimFactory.Create(), 5000);
        Assert.Equal(t.TargetLambda, t.Lambda, 3);
        Assert.Equal(FuelLimit.None, t.FuelLimit);
        Assert.InRange(t.InjectorDuty, 0.2, 0.85);
    }

    [Fact]
    public void BiggerInjectorsWithoutRescalingRunRich()
    {
        var sim = SimFactory.Create(("injectors", "injectors.550cc"));
        var t = SimFactory.At(sim, 5000);
        Assert.Equal(t.TargetLambda * 310.0 / 550.0, t.Lambda, 2);
        var tune = SimFactory.StockTune();
        tune.InjectorFlowCcMin = 550;
        var fixedT = SimFactory.At(SimFactory.Create(SimFactory.Assembly(("injectors", "injectors.550cc")), tune: tune), 5000);
        Assert.Equal(fixedT.TargetLambda, fixedT.Lambda, 3);
    }

    [Fact]
    public void E85WithGasolineCalibrationRunsLean()
    {
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), fuel: "e85"), 4000);
        Assert.Equal(t.TargetLambda * 14.7 / 9.8, t.Lambda, 2);
    }

    [Fact]
    public void E85PushesStockInjectorsPastTheirRecommendedDuty()
    {
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "e85", tune: tune);
        var high = SimFactory.At(sim, 7400);
        Assert.True(high.InjectorDuty > sim.Config.Injectors.MaxDuty);
        Assert.True(high.InjectorDuty > 1.35 * SimFactory.At(SimFactory.Create(), 7400).InjectorDuty, "E85 needs ~44% more fuel volume");
    }

    [Fact]
    public void StaticInjectorsRunLeanAtHighRpm()
    {
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        tune.SetLambda(0.70, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "e85", tune: tune);
        var low = SimFactory.At(sim, 3000);
        var high = SimFactory.At(sim, 7400);
        Assert.Equal(FuelLimit.None, low.FuelLimit);
        Assert.Equal(low.TargetLambda, low.Lambda, 3);
        Assert.Equal(FuelLimit.InjectorCapacity, high.FuelLimit);
        Assert.True(high.InjectorDuty > 1.0);
        Assert.True(high.Lambda > high.TargetLambda + 0.03, "lean when the injectors go static");
    }

    [Fact]
    public void E85MakesSlightlyMoreTorqueWhenFueledCorrectly()
    {
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        tune.InjectorFlowCcMin = 550;
        var e85 = SimFactory.Create(SimFactory.Assembly(("injectors", "injectors.550cc")), fuel: "e85", tune: tune);
        var gas = SimFactory.Create();
        var te = SimFactory.At(e85, 4000);
        var tg = SimFactory.At(gas, 4000);
        Assert.True(te.ChargeTemperature < tg.ChargeTemperature, "evaporative charge cooling");
        Assert.True(te.KnockLimitAdvance > tg.KnockLimitAdvance + 5, "octane and cooling");
        Assert.True(te.Torque > tg.Torque);
    }

    [Fact]
    public void WornPumpCannotHoldRailPressure()
    {
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        tune.InjectorFlowCcMin = 1000;
        tune.SetLambda(0.78, 90);
        var assembly = SimFactory.Assembly(("injectors", "injectors.1000cc"));
        assembly.PartIn("fuel_pump")!.Wear = 1.0;
        var t = SimFactory.At(SimFactory.Create(assembly, fuel: "e85", tune: tune), 7000);
        Assert.Equal(FuelLimit.PumpCapacity, t.FuelLimit);
        Assert.True(t.FuelRailPressure < 280_000, $"rail {t.FuelRailPressure / 1000:F1} kPa");
        Assert.True(t.Lambda > t.TargetLambda + 0.02, $"λ {t.Lambda:F3} vs target {t.TargetLambda:F3}");
    }

    [Fact]
    public void PumpModelBoostEatsCapacity()
    {
        var inj = new InjectorSpec { Count = 4, FlowCcMin = 1000 };
        var pump = TestContent.Database.GetPart("fuel_pump.oem_170").GetSpec<FuelPumpSpec>();
        var na = FuelSystem.Deliver(inj, 0, pump, 0, 750, 0.009, 0.01, 101_325, 101_325);
        var boosted = FuelSystem.Deliver(inj, 0, pump, 0, 750, 0.009, 0.01, 201_325, 101_325);
        Assert.True(boosted.RailPressure < na.RailPressure);
        Assert.True(boosted.FuelPerCycle < na.FuelPerCycle);
    }

    [Fact]
    public void InjectorDutyIsCappedAtStatic()
    {
        var inj = TestContent.Database.GetPart("injectors.310cc").GetSpec<InjectorSpec>();
        var pump = TestContent.Database.GetPart("fuel_pump.race_560").GetSpec<FuelPumpSpec>();
        var d = FuelSystem.Deliver(inj, 0, pump, 0, 750, 0.02, 0.01, 101_325, 101_325);
        Assert.Equal(2.0, d.Duty, 9);
        Assert.Equal(FuelLimit.InjectorCapacity, d.Limit);
        Assert.Equal(750 * inj.RatedFlow * 0.01, d.FuelPerCycle, 12);
    }

    [Fact]
    public void LeanMixtureRunsHotter()
    {
        var tune = SimFactory.StockTune();
        tune.SetLambda(1.0, 90);
        var lean = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 6000, 1.0, 8.0);
        var rich = SimFactory.At(SimFactory.Create(), 6000, 1.0, 8.0);
        Assert.True(lean.ExhaustGasTemperature > rich.ExhaustGasTemperature);
        Assert.True(lean.PistonCrownTemperature > rich.PistonCrownTemperature);
        Assert.True(lean.KnockLimitAdvance < rich.KnockLimitAdvance);
    }
}
