using CarSim.Core.Damage;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// The energy released by the fuel must equal brake power + heat to coolant + heat to oil + exhaust
/// enthalpy at every step, in every state (the original model created ≈ 19 kW with a blown head gasket).
/// </summary>
public class EnergyBalanceTests
{
    private static void AssertBalanced(EngineTelemetry t, string label)
    {
        double outputs = t.Power + t.HeatToCoolant + t.HeatToOil + t.ExhaustHeat;
        double scale = Math.Max(1000.0, Math.Abs(t.FuelPower) + Math.Abs(t.Power));
        Assert.True(Math.Abs(t.FuelPower - outputs) < 1e-9 * scale,
            $"{label}: fuel {t.FuelPower / 1000:F3} kW vs brake + coolant + oil + exhaust {outputs / 1000:F3} kW");
    }

    public static IEnumerable<object[]> OperatingPoints()
    {
        foreach (double rpm in new[] { 900.0, 3000, 5000, 7000 })
            foreach (double throttle in new[] { 0.05, 0.4, 1.0 })
                yield return new object[] { rpm, throttle };
    }

    [Theory]
    [MemberData(nameof(OperatingPoints))]
    public void StockEngineConservesEnergy(double rpm, double throttle)
    {
        var sim = SimFactory.Create();
        var t = SimFactory.At(sim, rpm, throttle, 0.5);
        Assert.True(t.Firing);
        AssertBalanced(t, $"{rpm} rpm, throttle {throttle}");
    }

    [Fact]
    public void FuelCutAndMotoringConserveEnergy()
    {
        var t = SimFactory.At(SimFactory.Create(), 7800, 1.0, 0.5);
        Assert.False(t.Firing);
        AssertBalanced(t, "rev limiter");
        // Motoring, the gas picks heat up from the hot walls on its way out (at the coolant's expense).
        var off = SimFactory.Create();
        var motoring = off.Step(0.005, new EngineInputs { Ignition = false, SpeedMode = SpeedMode.Held, HeldRpm = 3000 });
        AssertBalanced(motoring, "motoring");
        Assert.True(motoring.ExhaustHeat > motoring.Pmep * off.Config.Geometry.Displacement / (4 * Math.PI) * CarSim.Core.Common.Units.RpmToRadPerSec(3000));
    }

    [Fact]
    public void BlownHeadGasketConservesEnergy()
    {
        var sim = SimFactory.Create();
        sim.DamageEnabled = false;
        sim.Config.Part("head_gasket").Damage.Fail(FailureMode.HeadGasketBreach, 0);
        var healthy = SimFactory.At(SimFactory.Create(), 5000, 1.0, 1.0);
        var blown = SimFactory.At(sim, 5000, 1.0, 1.0);
        AssertBalanced(blown, "blown gasket");
        // The leak sends combustion heat into the coolant: more of the fuel energy goes there.
        Assert.True(blown.HeatToCoolant / blown.FuelPower > healthy.HeatToCoolant / healthy.FuelPower);
    }

    [Theory]
    [InlineData(0.72)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.2)]
    public void RichAndLeanMixturesConserveEnergy(double lambda)
    {
        var tune = SimFactory.StockTune();
        tune.SetLambda(lambda, 0);
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 4000, 1.0, 1.0);
        AssertBalanced(t, $"λ {lambda}");
    }

    [Fact]
    public void TurboAndE85BuildsConserveEnergy()
    {
        AssertBalanced(SimFactory.At(TurboTests.TurboSim(TurboTests.TurboBuild()), 6000, 1.0, 3.0), "turbo at boost");
        var tune = SimFactory.StockTune();
        tune.FuelStoichAfr = 9.8;
        tune.InjectorFlowCcMin = 550;
        tune.SetLambda(0.75, 90);
        AssertBalanced(SimFactory.At(SimFactory.Create(SimFactory.Assembly(("injectors", "injectors.550cc")), "e85", tune), 5000, 1.0, 1.0), "E85 rich");
    }

    [Fact]
    public void ExhaustTemperatureCarriesTheExhaustEnthalpy()
    {
        // Settled (the gas temperature lags 0.2 s): ṁ·c_p·(T_exhaust − T_charge) + latent heat of the
        // unburned fuel = the exhaust's share of the energy.
        var tune = SimFactory.StockTune();
        tune.SetLambda(0.80, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), tune: tune);
        var t = SimFactory.At(sim, 5000, 1.0, 4.0);
        var fuel = sim.Config.Fuel;
        double cycles = t.Rpm / 120.0 * sim.Config.Geometry.Cylinders;
        double burned = Math.Min(t.FuelMassFlow, t.AirMassFlow / fuel.StoichiometricAfr);
        double latent = (1 - CombustionModel.EvaporatedBeforeInletValveCloses) * (t.FuelMassFlow - burned) * fuel.LatentHeat;
        double carried = (t.AirMassFlow + t.FuelMassFlow) * CarSim.Core.Common.PhysicalConstants.ExhaustCp
                         * (t.PortGasTemperature - t.ChargeTemperature) + latent;
        Assert.True(cycles > 0);
        Assert.Equal(1.0, carried / t.ExhaustHeat, 2);
    }

    [Fact]
    public void RichMixtureCoolsTheExhaustThroughItsOwnPhysics()
    {
        // No empirical "−300 K per unit of richness" any more: richer mixtures run cooler because they make
        // a little more work from the same air and the excess fuel absorbs heat evaporating.
        double Egt(double lambda)
        {
            var tune = SimFactory.StockTune();
            tune.SetLambda(lambda, 90);
            return SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 6000, 1.0, 6.0).PortGasTemperature;
        }
        double stoich = Egt(1.0), rich = Egt(0.85), veryRich = Egt(0.75);
        Assert.True(rich < stoich - 20, $"λ 1.0 {stoich - 273.15:F0} °C, λ 0.85 {rich - 273.15:F0} °C");
        Assert.True(veryRich < rich);
        Assert.InRange(stoich - 273.15, 700, 1000);
    }
}
