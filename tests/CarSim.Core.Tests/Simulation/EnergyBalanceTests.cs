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

        // Motoring with the throttle open: little pumping work, so the cold gas picks heat up from the hot walls on
        // its way out (at the coolant's expense) and leaves with more than the pumping work.
        var open = SimFactory.Create();
        var motoringOpen = open.Step(0.005, new EngineInputs { Ignition = false, Throttle = 1.0, SpeedMode = SpeedMode.Held, HeldRpm = 3000 });
        AssertBalanced(motoringOpen, "motoring, throttle open");
        double pumpingOpen = motoringOpen.Pmep * open.Config.Geometry.Displacement / (4 * Math.PI) * CarSim.Core.Common.Units.RpmToRadPerSec(3000);
        Assert.True(motoringOpen.PortWallHeat < 0, "cold gas takes heat from the walls");
        Assert.True(motoringOpen.ExhaustHeat > pumpingOpen);

        // Motoring with it shut: deep vacuum, the pumping work is large and the gas flow tiny. The walls take heat
        // from the gas instead — it would otherwise leave at thousands of kelvin.
        var shut = SimFactory.Create();
        var motoringShut = shut.Step(0.005, new EngineInputs { Ignition = false, Throttle = 0.0, SpeedMode = SpeedMode.Held, HeldRpm = 3000 });
        AssertBalanced(motoringShut, "motoring, throttle shut");
        double pumpingShut = motoringShut.Pmep * shut.Config.Geometry.Displacement / (4 * Math.PI) * CarSim.Core.Common.Units.RpmToRadPerSec(3000);
        Assert.True(motoringShut.PortWallHeat > 0, "hot pumped gas gives heat to the walls");
        Assert.True(motoringShut.ExhaustHeat < pumpingShut);
    }

    /// <summary>
    /// Whatever the operating point, the exhaust port can only pull the gas towards the wall temperature: the gas
    /// leaves between the wall temperature and the temperature it would have had without the walls (adiabatic).
    /// </summary>
    [Theory]
    [InlineData(1500, 0.0, true)]
    [InlineData(3000, 0.0, true)]
    [InlineData(6500, 0.0, true)]
    [InlineData(3000, 0.3, true)]
    [InlineData(6500, 1.0, true)]
    [InlineData(3000, 0.0, false)]
    [InlineData(3000, 1.0, false)]
    public void ThePortPullsTheGasTowardsTheWallNeverPastIt(double rpm, double throttle, bool ignition)
    {
        var sim = SimFactory.Create();
        var input = new EngineInputs { Ignition = ignition, Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        EngineTelemetry t = null!;
        for (int i = 0; i < 400; i++) t = sim.Step(0.005, input);
        double capacityRate = (t.AirMassFlow + t.FuelMassFlow) * CarSim.Core.Common.PhysicalConstants.ExhaustCp;
        // Settled (the port-gas state lags 0.2 s): without the walls the gas would be hotter by the heat they took.
        double adiabatic = t.PortGasTemperature + t.PortWallHeat / capacityRate;
        double wall = t.CoolantTemperature;
        double lo = Math.Min(wall, adiabatic), hi = Math.Max(wall, adiabatic);
        Assert.InRange(t.PortGasTemperature, lo - 1.0, hi + 1.0);
        Assert.True(Math.Sign(t.PortWallHeat) == Math.Sign(adiabatic - wall) || Math.Abs(t.PortWallHeat) < 1.0);
    }

    [Fact]
    public void ClosedThrottleOverrunLeavesTheExhaustCoolerThanFullLoad()
    {
        // Regression: once pumping work was routed into the exhaust (to close the energy balance) the few grams per
        // second passing a shut throttle carried ~10 kW of it and left the ports at 1,500–4,500 °C.
        foreach (double rpm in new[] { 2000.0, 4000, 6500 })
        {
            double full = SimFactory.At(SimFactory.Create(), rpm, 1.0, 1.0).PortGasTemperature;
            var sim = SimFactory.Create();
            SimFactory.At(sim, rpm, 1.0, 1.0);
            var overrun = SimFactory.At(sim, rpm, 0.0, 3.0);
            Assert.True(overrun.PortGasTemperature < full, $"{rpm} rpm: overrun {overrun.PortGasTemperature - 273.15:F0} °C, full load {full - 273.15:F0} °C");
            Assert.True(overrun.PortGasTemperature - 273.15 < 700, $"{rpm} rpm: overrun {overrun.PortGasTemperature - 273.15:F0} °C");
        }
    }

    /// <summary>
    /// First law over a warm-up with stored heat: over a minute from cold on a free-running engine with a load, the
    /// fuel energy released equals the brake work + the exhaust's enthalpy + what the radiator, block surface and sump
    /// shed + the heat now stored in the coolant and oil. Guards the thermal integration against flows that appear in
    /// telemetry but are not integrated (or the reverse).
    /// </summary>
    [Fact]
    public void WarmUpClosesTheFirstLawWithStoredHeat()
    {
        var sim = SimFactory.Create(SimFactory.Assembly(), state: new EngineState());
        sim.DamageEnabled = false;
        var c = sim.Config;
        double coolant0 = sim.State.CoolantTemperature, oil0 = sim.State.OilTemperature;
        // Start and run against a fan-like load (the engine settles at a speed where it balances the load).
        var input = new EngineInputs { Starter = true, Throttle = 0.4 };
        double dt = 0.005, fuel = 0, brake = 0, exhaust = 0, shed = 0;
        for (int i = 0; i < 60.0 / dt; i++)
        {
            if (i * dt > 1.0) input.Starter = false;
            input.LoadTorque = 2.5e-4 * sim.State.Omega * sim.State.Omega;
            var t = sim.Step(dt, input);
            fuel += t.FuelPower * dt;
            brake += t.Power * dt;
            exhaust += t.ExhaustHeat * dt;
            shed += (t.RadiatorHeatRejection + t.CoolantSurfaceLoss + t.OilSumpLoss) * dt;
        }
        Assert.True(sim.State.Running);
        Assert.Equal(1.0, sim.State.CoolantLevel);
        double stored = c.CoolantHeatCapacity * (sim.State.CoolantTemperature - coolant0) + c.OilHeatCapacity * (sim.State.OilTemperature - oil0);
        Assert.True(stored > 0.2 * fuel, "most of a cold start's heat is soaked into the engine");
        double outputs = brake + exhaust + shed + stored;
        Assert.True(Math.Abs(fuel - outputs) < 1e-6 * fuel, $"fuel {fuel / 1e6:F4} MJ vs outputs {outputs / 1e6:F4} MJ");
    }

    /// <summary>
    /// Bounds that do not go through the heat split: the fuel cannot release more than its heating value, and brake
    /// work cannot beat the air-standard Otto efficiency at the engine's compression ratio — NA or boosted.
    /// </summary>
    [Fact]
    public void BrakeEfficiencyStaysBelowTheOttoLimitEverywhere()
    {
        void Check(EngineSimulation sim, double rpm, double throttle, string label)
        {
            var t = SimFactory.At(sim, rpm, throttle, 2.0);
            if (!t.Firing) return;
            double supplied = t.FuelMassFlow * sim.Config.Fuel.LowerHeatingValue;
            double otto = 1.0 - Math.Pow(sim.Config.Geometry.CompressionRatio, -0.4);
            Assert.True(t.FuelPower <= supplied * (1 + 1e-9), $"{label}: released {t.FuelPower:F0} W > supplied {supplied:F0} W");
            Assert.True(t.Power < otto * supplied, $"{label}: brake {t.Power / supplied:P1} of the fuel vs Otto {otto:P1}");
            Assert.True(t.ExhaustHeat > 0, $"{label}: a firing engine's exhaust leaves hotter than its charge");
        }
        foreach (double rpm in new[] { 1500.0, 3000, 5000, 7000 })
            foreach (double throttle in new[] { 0.1, 0.4, 1.0 })
            {
                Check(SimFactory.Create(), rpm, throttle, $"NA {rpm} rpm {throttle}");
                var turbo = TurboTests.TurboSim(TurboTests.TurboBuild());
                turbo.DamageEnabled = false;
                Check(turbo, rpm, throttle, $"turbo {rpm} rpm {throttle}");
            }
    }

    /// <summary>A failure never makes power: every degraded engine failure the model reacts to costs torque.</summary>
    [Fact]
    public void NoEngineFailureCreatesPower()
    {
        var modes = FailureModeInfo.All.Values
            .Where(i => i.Severity == FailureSeverity.Degraded)
            .Where(i => SimFactory.Create().Config.Assembly.FindByCategory(i.Category) != null)
            .Select(i => i.Mode).ToList();
        Assert.Contains(FailureMode.HeadGasketBreach, modes);
        foreach (double rpm in new[] { 2500.0, 5000, 7000 })
        {
            var healthy = SimFactory.At(SimFactory.Create(), rpm, 1.0, 1.0);
            foreach (var mode in modes)
            {
                var sim = SimFactory.Create();
                sim.DamageEnabled = false;
                sim.Config.Assembly.FindByCategory(FailureModeInfo.Of(mode).Category)!.Damage.Fail(mode, 0);
                var t = SimFactory.At(sim, rpm, 1.0, 1.0);
                AssertBalanced(t, $"{mode} at {rpm} rpm");
                Assert.True(t.Torque <= healthy.Torque + 1e-6, $"{mode} at {rpm} rpm: {t.Torque:F2} N·m vs healthy {healthy.Torque:F2} N·m");
            }
        }
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
