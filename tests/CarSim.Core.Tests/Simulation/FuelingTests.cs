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
        // Only the flow scaling wrong (dead time set to the new injectors' 1.00 ms): rich by the flow ratio (to within
        // the extra charge cooling of so much more fuel evaporating, ≈ 1 %).
        var tune = SimFactory.StockTune();
        tune.InjectorDeadTimeMs = 1.00;
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(("injectors", "injectors.550cc")), tune: tune), 5000);
        Assert.Equal(t.TargetLambda * 310.0 / 550.0, t.Lambda, 2);
        tune = tune.Clone();
        tune.InjectorFlowCcMin = 550;
        var fixedT = SimFactory.At(SimFactory.Create(SimFactory.Assembly(("injectors", "injectors.550cc")), tune: tune), 5000);
        Assert.Equal(fixedT.TargetLambda, fixedT.Lambda, 3);
    }

    [Fact]
    public void AWrongDeadTimeMattersAtIdleAndHardlyAtFullLoad()
    {
        // The ECU believes 0.4 ms more dead time than the injectors have: each pulse opens 0.4 ms too long. Short
        // idle pulses run rich by tens of percent; long full-load pulses by a few.
        var tune = SimFactory.StockTune();
        tune.InjectorDeadTimeMs += 0.4;
        var idle = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 900, 0.0, 2.0);
        var full = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 6000, 1.0, 1.0);
        double idleError = idle.TargetLambda / idle.Lambda - 1.0, fullError = full.TargetLambda / full.Lambda - 1.0;
        Assert.True(idleError > 0.15, $"idle {idleError:P1} rich");
        Assert.InRange(fullError, 0.005, 0.06);
        Assert.True(idleError > 4 * fullError);
        // The same pulse arithmetic, independently: extra fuel = 0.4 ms of injector flow over the opening time.
        var inj = SimFactory.Create().Config.Injectors;
        double cycle = 120.0 / 6000.0;
        double openTime = full.InjectorDuty * cycle - tune.InjectorDeadTimeMs / 1000.0 + 0.4e-3;
        Assert.Equal(1.0 + 0.4e-3 / (openTime - 0.4e-3), full.TargetLambda / full.Lambda, 2);
        Assert.Equal(0.90, inj.DeadTimeMs, 6);
    }

    [Fact]
    public void TheEcuMetersWithTheFuelDensityItBelievesNotTheRealOne()
    {
        // Race fuel is 4 % lighter than the pump fuel the stock tune is calibrated for. With the stoichiometric AFR
        // updated but not the density, the ECU's injector volume carries 4 % less mass: lean by the density ratio.
        var race = TestContent.Database.GetFuel("race_110");
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = race.StoichiometricAfr;
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), "race_110", tune), 5000);
        Assert.Equal(t.TargetLambda * tune.FuelDensityKgL / race.DensityKgL, t.Lambda, 2);
        var calibrated = tune.Clone();
        calibrated.FuelDensityKgL = race.DensityKgL;
        var fixedT = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), "race_110", calibrated), 5000);
        Assert.Equal(fixedT.TargetLambda, fixedT.Lambda, 2);
    }

    [Fact]
    public void E85WithGasolineCalibrationRunsLean()
    {
        // The ECU still divides by 14.7, so it delivers 14.7/9.8 too little fuel — offset slightly by E85 being
        // denser than the gasoline it meters as (0.781 vs 0.748 kg/L: 4 % more mass per injected volume). E85 also
        // cools the charge more than the gasoline the VE table was calibrated on, so a little more air than the ECU
        // expects gets in: slightly leaner again.
        var e85 = TestContent.Database.GetFuel("e85");
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), fuel: "e85"), 4000);
        double calibrationError = t.TargetLambda * 14.7 / e85.StoichiometricAfr * SimFactory.StockTune().FuelDensityKgL / e85.DensityKgL;
        Assert.InRange(t.Lambda, calibrationError, calibrationError * 1.05);
    }

    [Fact]
    public void E85PushesStockInjectorsPastTheirRecommendedDuty()
    {
        // An E85 power target (λ 0.80) on the stock injectors.
        var gasTune = SimFactory.StockTune();
        gasTune.SetLambda(0.80, 90);
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        tune.SetLambda(0.80, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "e85", tune: tune);
        var high = SimFactory.At(sim, 7400);
        Assert.True(high.InjectorDuty > sim.Config.Injectors.MaxDuty);
        double gasDuty = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: gasTune), 7400).InjectorDuty;
        Assert.True(high.InjectorDuty > 1.35 * gasDuty, "E85 needs ~40 % more fuel volume at the same λ");
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
        // Off target by the gasoline-calibrated VE table only (E85's extra charge cooling packs in more air).
        Assert.InRange(low.Lambda / low.TargetLambda, 1.0, 1.08);
        Assert.Equal(FuelLimit.InjectorCapacity, high.FuelLimit);
        // The ECU asks for more than 100 % duty; the shortfall shows up as a leaner mixture.
        Assert.True(high.InjectorDuty > 1.03);
        Assert.True(high.Lambda / high.TargetLambda > low.Lambda / low.TargetLambda + 0.02, "lean when the injectors go static");
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
