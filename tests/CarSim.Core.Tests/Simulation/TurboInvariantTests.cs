using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Physical invariants of the turbocharger far from its calibration point: every turbo, boost targets from the shipped
/// 175 kPa to 350 kPa, low to high rpm, throttle and target steps. These would fail if the original instabilities came
/// back — the efficiency↔pressure-ratio iteration that flipped between two solutions (torque 305 ↔ 375 N·m above
/// 175 kPa), choke that removed the compressor's work so the shaft ran away (bounded only by a hidden 2× clamp),
/// integrator wind-up, or overrun exhaust heat that over-spun small turbos.
/// </summary>
public class TurboInvariantTests
{
    public static readonly string[] Turbos = { "turbo.t25_small", "turbo.t28_ball", "turbo.t35_big" };

    private static TurbochargerSpec Spec(string id) => TestContent.Database.GetPart(id).GetSpec<TurbochargerSpec>();

    private static EngineSimulation Build(string turbo, double targetKpa)
    {
        var tune = SimFactory.ExtendLoadAxis(TurboTests.TuneWithBoost(targetKpa), 300, 350);
        // Race fuel keeps knock control out of it: this is about the turbo, not knock-control dither.
        var sim = SimFactory.Create(TurboTests.TurboBuild(turbo), "race_110", tune);
        sim.DamageEnabled = false;
        return sim;
    }

    /// <summary>Consecutive step-to-step reversals of manifold pressure larger than 10 Pa: a limit cycle, not a drift.</summary>
    private static int Chatter(IReadOnlyList<double> series)
    {
        int count = 0;
        for (int i = 2; i < series.Count; i++)
        {
            double a = series[i - 1] - series[i - 2], b = series[i] - series[i - 1];
            if (Math.Abs(a) > 10 && Math.Abs(b) > 10 && Math.Sign(a) != Math.Sign(b)) count++;
        }
        return count;
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void TheCompressorObeysTheFirstAndSecondLawEverywhere(string id)
    {
        var s = Spec(id);
        double cp = PhysicalConstants.AirCp;
        foreach (double n in new[] { 0.05, 0.2, 0.4, 0.6, 0.8, 1.0, 1.15, 1.3 })
        {
            double omega = n * s.MaxShaftSpeed;
            double tip = omega * s.CompressorTipRadius;
            double bound = TurbochargerModel.MaxPressureRatio(s, omega, 298.15);
            for (double x = 0.0; x <= 2.0; x += 0.02)
            {
                double flow = x * s.CompressorChokeFlowKgS;
                var p = TurbochargerModel.Compressor(s, omega, flow, 101_325, 298.15);
                string at = $"{id} n {n:F2}, {flow:F3} kg/s";
                Assert.True(double.IsFinite(p.PressureRatio) && double.IsFinite(p.Power) && double.IsFinite(p.OutletTemperature), at);
                // First law: all the through-flow work heats the charge; shaft power covers at least that.
                Assert.Equal(298.15 + p.SpecificWork / cp, p.OutletTemperature, 9);
                Assert.True(p.Power >= flow * p.SpecificWork - 1e-9, at);
                // The work never collapses (the old model scaled it to zero at choke and the shaft ran away).
                Assert.True(p.SpecificWork >= (1 - 2 * TurbochargerModel.WorkBacksweep) * TurbochargerModel.WorkCoefficient * tip * tip - 1e-9, at);
                // Second law: no better than the peak isentropic efficiency, so no free compression.
                Assert.True(p.Efficiency <= s.CompressorPeakEfficiency + 1e-9, $"{at}: η {p.Efficiency:F4}");
                // The air-path root finder brackets the solution with this bound: it must hold.
                Assert.True(p.PressureRatio <= bound * (1 + 1e-12), $"{at}: PR {p.PressureRatio:F4} above bound {bound:F4}");
            }
        }
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var turbo in Turbos)
            foreach (double target in new[] { 175.0, 240, 280, 350 })
                foreach (double rpm in new[] { 3500.0, 7000 })
                    yield return new object[] { turbo, target, rpm };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ClosedLoopBoostNeverOscillatesOrRunsAway(string turbo, double targetKpa, double rpm)
    {
        var sim = Build(turbo, targetKpa);
        SimFactory.At(sim, rpm, 1.0, 5.0);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        var map = new List<double>();
        double maxShaft = 0, shaftBefore = sim.State.TurboOmega;
        EngineTelemetry t = null!;
        for (int i = 0; i < 500; i++)
        {
            t = sim.Step(0.002, input);
            Assert.True(double.IsFinite(t.Torque) && double.IsFinite(t.ManifoldPressure) && double.IsFinite(sim.State.TurboOmega));
            map.Add(t.ManifoldPressure);
            maxShaft = Math.Max(maxShaft, sim.State.TurboOmega);
        }
        string at = $"{turbo} {targetKpa} kPa {rpm} rpm";
        Assert.Equal(0, Chatter(map));
        // Unreachable targets over-spin small turbos (a failure the damage model reports), but the shaft settles where
        // the compressor's work balances the turbine: bounded by physics, not by a clamp.
        Assert.True(maxShaft < 1.25 * sim.Config.Turbo!.MaxShaftSpeed, $"{at}: shaft {maxShaft / sim.Config.Turbo.MaxShaftSpeed:P0} of rated");
        // Where the shaft has settled, the powers on it balance.
        if (Math.Abs(sim.State.TurboOmega - shaftBefore) < 5e-4 * shaftBefore)
        {
            double friction = TurbochargerModel.FrictionPower(sim.Config.Turbo, sim.State.TurboOmega, sim.Config.Part("turbocharger").Wear);
            Assert.True(Math.Abs(t.TurbinePower - t.CompressorPower - friction) < 0.005 * t.TurbinePower + 1.0,
                $"{at}: turbine {t.TurbinePower:F0} W, compressor {t.CompressorPower:F0} W, friction {friction:F0} W");
        }
        // Boost never exceeds a reachable target by more than the actuator's proportional band.
        double gaugeTarget = Units.KpaToPa(targetKpa) - 101_325;
        if (t.WastegateOpening > 0.01) Assert.True(t.BoostPressure < gaugeTarget + 5_000, $"{at}: boost {t.BoostKpa:F0} kPa with the gate open");
    }

    [Fact]
    public void AnUnreachableTargetBreaksTheTurboWithAnExplanationNotTheSolver()
    {
        // A small turbo chasing 350 kPa at 7000 rpm: near choke it can only pass more air by spinning faster, the
        // ECU keeps the gate shut, and the shaft runs past its rating — a real failure with a report.
        var sim = SimFactory.Create(TurboTests.TurboBuild("turbo.t25_small"), "race_110", SimFactory.ExtendLoadAxis(TurboTests.TuneWithBoost(350), 300, 350));
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 7000, CoolantTemperatureOverride = 363.15 };
        double maxShaft = 0;
        for (int i = 0; i < 5000 && sim.Damage.Failures.Count == 0; i++)
        {
            sim.Step(0.002, input);
            maxShaft = Math.Max(maxShaft, sim.State.TurboOmega);
        }
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(CarSim.Core.Damage.FailureMode.TurboOverspeed, report.Mode);
        Assert.Contains(report.ContributingFactors, f => f.Contains("too small"));
        Assert.InRange(maxShaft / sim.Config.Turbo!.MaxShaftSpeed, 1.0, 1.25);
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void ThrottleSquareWavesStayBoundedAndSmooth(string turbo)
    {
        var sim = Build(turbo, 240);
        var input = new EngineInputs { SpeedMode = SpeedMode.Held, HeldRpm = 6000, CoolantTemperatureOverride = 363.15 };
        var phase = new List<double>();
        int worstPhase = 0;
        double maxShaft = 0;
        for (int i = 0; i < 2500; i++)
        {
            input.Throttle = (i / 125) % 2 == 0 ? 1.0 : 0.0; // 0.25 s on, 0.25 s off
            if (i % 125 == 0) { worstPhase = Math.Max(worstPhase, Chatter(phase)); phase.Clear(); }
            var t = sim.Step(0.002, input);
            Assert.True(double.IsFinite(t.Torque) && double.IsFinite(sim.State.TurboOmega));
            Assert.InRange(sim.State.BoostControlIntegral, 0.0, 1.0);
            Assert.True(t.TurbineInletTemperature < sim.Config.Turbo!.MaxTurbineInletTemperature, $"{Units.KToC(t.TurbineInletTemperature):F0} °C");
            phase.Add(t.ManifoldPressure);
            maxShaft = Math.Max(maxShaft, sim.State.TurboOmega);
        }
        // A transient may overshoot once after each throttle edge (the quasi-static manifold jumps to the new
        // pressure in one step); a limit cycle reverses again and again.
        Assert.True(Math.Max(worstPhase, Chatter(phase)) <= 1, $"{turbo}: {worstPhase} reversals in one throttle phase");
        Assert.True(maxShaft < sim.Config.Turbo!.MaxShaftSpeed, $"shaft {maxShaft / sim.Config.Turbo.MaxShaftSpeed:P0} of rated");
    }

    [Fact]
    public void BoostTargetStepsSettleWithoutWindUp()
    {
        var sim = Build("turbo.t28_ball", 150);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6000, CoolantTemperatureOverride = 363.15 };
        SimFactory.At(sim, 6000, 1.0, 3.0);
        var boost = sim.Ecu.Tune.BoostTarget!;
        double previous = 150;
        // Targets the T28 can reach at 6000 rpm (it tops out near 265 kPa with the shaft at its rating).
        foreach (double target in new[] { 235.0, 160, 250, 175, 215 })
        {
            for (int c = 0; c < boost.Columns; c++) if (boost.XAxis[c] >= 3500) boost[0, c] = target;
            double peak = 0, last = 0;
            var map = new List<double>();
            for (int i = 0; i < 1500; i++)
            {
                var t = sim.Step(0.002, input);
                peak = Math.Max(peak, t.MapKpa);
                last = t.MapKpa;
                map.Add(t.ManifoldPressure);
                Assert.InRange(sim.State.BoostControlIntegral, 0.0, 1.0);
            }
            Assert.Equal(0, Chatter(map));
            // Up-steps overshoot by at most 8 % of the gauge target; every step ends within 4 kPa of it.
            double gauge = target - 101.325;
            if (target > previous) Assert.True(peak < target + 0.08 * gauge, $"step {previous} → {target}: peak {peak:F1} kPa");
            Assert.InRange(last, target - 4, target + 4);
            previous = target;
        }
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void TheTurbineEfficiencyFloorIsNeverWhatMakesBoost(string turbo)
    {
        // The floor on the turbine's efficiency parabola only acts spooling from standstill and at idle. It must never
        // be what holds a turbo on boost; and on overrun, where a few g/s trickle through a fast wheel, the turbine
        // must drag (windmill), not credit power that could keep the shaft spinning.
        var sim = TurboTests.TurboSim(TurboTests.TurboBuild(turbo));
        sim.DamageEnabled = false;
        var input = new EngineInputs { SpeedMode = SpeedMode.Held, CoolantTemperatureOverride = 363.15 };
        foreach (var (rpm, throttle, seconds) in new[] { (1000.0, 0.0, 1.0), (2000.0, 1.0, 2.0), (4000.0, 1.0, 2.0), (6500.0, 1.0, 2.0), (6500.0, 0.0, 2.0), (3000.0, 0.3, 2.0) })
        {
            input.HeldRpm = rpm;
            input.Throttle = throttle;
            for (int i = 0; i < seconds / 0.002; i++)
            {
                var t = sim.Step(0.002, input);
                // Windmilling (far past the optimum blade-speed ratio): the wheel drags, it never drives.
                if (t.TurbineBladeSpeedRatio > 1.2) Assert.True(t.TurbinePower <= 0, $"{turbo} overrun: {t.TurbinePower:F1} W at BSR {t.TurbineBladeSpeedRatio:F1}");
                if (!TurbochargerModel.TurbineOnEfficiencyFloor(t.TurbineBladeSpeedRatio) || t.TurbinePower <= 0) continue;
                Assert.True(t.BoostPressure < 5_000, $"{turbo} {rpm} rpm throttle {throttle}: on the floor at {t.BoostKpa:F0} kPa boost");
            }
        }
    }
}
