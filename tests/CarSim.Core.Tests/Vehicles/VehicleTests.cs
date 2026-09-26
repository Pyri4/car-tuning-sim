using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

public static class Car
{
    public const double Dt = 0.002;
    private static readonly PartInstanceFactory Factory = new(20_000_000);

    public static VehicleSimulation Create(EngineAssembly? engine = null, EcuTune? tune = null, string fuel = "gasoline_95",
        params (string slot, string part)[] chassisSwaps)
    {
        var db = TestContent.Database;
        var def = db.GetVehicle("kestrel_s2");
        engine ??= SimFactory.Assembly();
        var chassis = VehicleAssembly.CreateStock(def, db, Factory);
        foreach (var (slot, part) in chassisSwaps)
        {
            chassis.Remove(slot, out _);
            Assert.True(chassis.Install(slot, Factory.Create(db.GetPart(part))).Ok);
        }
        var engineSim = SimFactory.Create(engine, fuel, tune ?? SimFactory.StockTune());
        var sim = new VehicleSimulation(new VehicleConfiguration(def, chassis, engine, db), engineSim);
        sim.StartIdling();
        return sim;
    }

    public static VehicleSimulation Chassis(params (string slot, string part)[] swaps) => Create(null, null, "gasoline_95", swaps);

    /// <summary>Sets the car rolling at <paramref name="kmh"/> in <paramref name="gear"/> with wheels and engine synchronised.</summary>
    public static void Rolling(VehicleSimulation sim, double kmh, int gear)
    {
        var s = sim.State;
        s.U = kmh / 3.6;
        for (int w = 0; w < 4; w++) s.WheelOmega[w] = s.U / sim.Config.TireOf(w).Radius;
        s.Gear = gear;
        s.Clutch = 1;
        s.EngineOmega = gear == 0 ? s.EngineOmega : s.WheelOmega[2] * sim.Config.OverallRatio(gear);
    }

    /// <summary>Full-throttle standing start shifting at <paramref name="shiftRpm"/>; returns seconds to the target speed.</summary>
    public static double Accelerate(VehicleSimulation sim, double targetKmh, double shiftRpm = 7200, double maxSeconds = 60)
    {
        var input = new VehicleInputs { ShiftUp = true };
        var t = sim.Step(Dt, input);
        input.ShiftUp = false;
        input.Throttle = 1;
        double time = 0;
        while (time < maxSeconds)
        {
            input.ShiftUp = t.EngineRpm > shiftRpm && t.Gear < sim.Config.Gearbox.Ratios.Count && sim.State.ShiftTimer <= 0;
            t = sim.Step(Dt, input);
            time += Dt;
            if (t.SpeedKmh >= targetKmh) return time;
        }
        return double.PositiveInfinity;
    }

    /// <summary>Stop from speed, ramping the pedal to <paramref name="pedal"/> over 0.3 s as a driver would.</summary>
    public static double BrakingDistance(VehicleSimulation sim, double fromKmh, double pedal)
    {
        Rolling(sim, fromKmh, 0);
        var input = new VehicleInputs();
        double start = sim.State.Distance;
        var t = sim.Step(Dt, input);
        for (int i = 0; i < 20_000 && t.Speed > 0.05; i++)
        {
            input.Brake = pedal * Math.Min(1.0, i * Dt / 0.3);
            t = sim.Step(Dt, input);
        }
        return sim.State.Distance - start;
    }

    /// <summary>Ramp steering at constant speed; return the highest steady lateral g before the car spins.</summary>
    public static double MaxLateralG(VehicleSimulation sim, double kmh = 80)
    {
        Rolling(sim, kmh, 3);
        var input = new VehicleInputs();
        double max = 0, time = 0;
        VehicleTelemetry t = sim.Step(Dt, input);
        for (int i = 0; i < 12_000; i++)
        {
            input.Steer = Math.Min(1, time * 0.08);
            input.Throttle = Math.Clamp(0.3 + (kmh / 3.6 - t.Speed) * 0.3, 0, 1);
            t = sim.Step(Dt, input);
            time += Dt;
            if (Math.Abs(t.BodySlipAngle) < 0.15) max = Math.Max(max, Math.Abs(t.LateralG));
            else break;
        }
        return max;
    }
}

public class VehicleTests
{
    [Fact]
    public void TyreForcePeaksAtThePeakSlipAndSlidesBeyond()
    {
        var tire = TestContent.Database.GetPart("tires.street_225_50r16").GetSpec<TireSpec>();
        double peak = TireModel.Forces(tire, 3500, tire.PeakSlipRatio, 0).Fx;
        Assert.Equal(TireModel.Friction(tire, 3500) * 3500, peak, 1);
        Assert.True(TireModel.Forces(tire, 3500, tire.PeakSlipRatio * 0.5, 0).Fx < peak);
        double sliding = TireModel.Forces(tire, 3500, 1.0, 0).Fx;
        Assert.InRange(sliding / peak, 0.7, 0.85);
        Assert.True(TireModel.Forces(tire, 3500, 0, 0.05).Fy < 0, "lateral force opposes the slip angle");
    }

    [Fact]
    public void CombinedSlipReducesLateralGrip()
    {
        var tire = TestContent.Database.GetPart("tires.street_225_50r16").GetSpec<TireSpec>();
        double pure = Math.Abs(TireModel.Forces(tire, 3500, 0, tire.PeakSlipAngle).Fy);
        double combined = Math.Abs(TireModel.Forces(tire, 3500, tire.PeakSlipRatio, tire.PeakSlipAngle).Fy);
        Assert.True(combined < 0.8 * pure);
    }

    [Fact]
    public void LoadSensitivityMeansDoubleLoadLessThanDoubleGrip()
    {
        var tire = TestContent.Database.GetPart("tires.street_225_50r16").GetSpec<TireSpec>();
        Assert.True(TireModel.Friction(tire, 7000) * 7000 < 2 * TireModel.Friction(tire, 3500) * 3500);
    }

    [Fact]
    public void StockCarAccelerationIsPlausible()
    {
        double t100 = Car.Accelerate(Car.Chassis(), 100);
        Assert.InRange(t100, 7.0, 10.0);
    }

    [Fact]
    public void TopSpeedIsDragLimitedAndPlausible()
    {
        var sim = Car.Chassis();
        Car.Accelerate(sim, 400, maxSeconds: 70);
        Assert.InRange(sim.Last!.SpeedKmh, 195, 235);
    }

    [Fact]
    public void ShorterFinalDriveAcceleratesHarder()
    {
        double stock = Car.Accelerate(Car.Chassis(("differential", "differential.lsd_410")), 150);
        double shortRatio = Car.Accelerate(Car.Chassis(("differential", "differential.lsd_457")), 150);
        Assert.True(shortRatio < stock, $"4.10: {stock:F2}s, 4.57: {shortRatio:F2}s");
    }

    [Fact]
    public void MorePowerAcceleratesHarder()
    {
        var built = SimFactory.Assembly(("cylinder_head", "k20.head.ported"), ("valve_springs", "k20.valve_springs.performance"),
            ("camshafts", "k20.cams.sport"), ("exhaust", "exhaust.race_76mm"), ("exhaust_manifold", "k20.exhaust_manifold.header421"));
        double stock = Car.Accelerate(Car.Chassis(), 160);
        double faster = Car.Accelerate(Car.Create(built), 160);
        Assert.True(faster < stock, $"stock {stock:F2}s, built {faster:F2}s");
    }

    [Fact]
    public void ThresholdBrakingBeatsLockingTheWheels()
    {
        double locked = Car.BrakingDistance(Car.Chassis(), 100, 1.0);
        double threshold = new[] { 0.45, 0.5, 0.55, 0.6, 0.65 }.Min(p => Car.BrakingDistance(Car.Chassis(), 100, p));
        Assert.True(threshold < 0.9 * locked, $"locked {locked:F1} m, threshold {threshold:F1} m");
        Assert.InRange(threshold, 40, 52); // includes the 0.3 s pedal ramp
    }

    [Fact]
    public void GrippierTyresStopShorterAndCornerHarder()
    {
        var slicks = new[] { ("tires_front", "tires.semislick_235_40r17"), ("tires_rear", "tires.semislick_235_40r17") };
        double Best(params (string, string)[] swaps) => new[] { 0.45, 0.55, 0.65, 0.75, 0.85 }.Min(p => Car.BrakingDistance(Car.Chassis(swaps), 100, p));
        Assert.True(Best(slicks) < Best());
        double street = Car.MaxLateralG(Car.Chassis());
        double semi = Car.MaxLateralG(Car.Chassis(slicks));
        Assert.InRange(street, 0.8, 1.0);
        Assert.True(semi > street + 0.15, $"street {street:F2} g, semi-slick {semi:F2} g");
    }

    [Fact]
    public void RollStiffnessDistributionSetsBalance()
    {
        var stock = Car.Chassis().Config;
        Assert.InRange(stock.FrontRollShare, 0.5, 0.6);
        Assert.InRange(stock.RollFrequency / (2 * Math.PI), 1.5, 3.5);
        Assert.InRange(stock.RollDampingRatio, 0.2, 0.6);
        var track = Car.Chassis(("suspension", "suspension.coilover_track")).Config;
        Assert.True(track.RollFrequency > stock.RollFrequency, "stiffer suspension responds faster");
        Assert.True(track.CgHeight < stock.CgHeight, "lowered");
    }

    [Fact]
    public void StifferSuspensionTransfersLoadFaster()
    {
        // Step steer; time until the lateral load transfer reaches 90 % of its quasi-static value m·a_y·h.
        double TimeToTransfer(VehicleSimulation sim)
        {
            Car.Rolling(sim, 80, 3);
            var input = new VehicleInputs { Steer = 0.12, Throttle = 0.3 };
            double time = 0;
            sim.Step(Car.Dt, input);
            while (time < 3)
            {
                sim.Step(Car.Dt, input);
                time += Car.Dt;
                double target = sim.Config.Mass * sim.State.Ay * sim.Config.CgHeight;
                if (time > 0.05 && Math.Abs(target) > 1000 && sim.State.LatTransfer / target > 0.9) break;
            }
            return time;
        }
        Assert.True(TimeToTransfer(Car.Chassis(("suspension", "suspension.coilover_track"))) < TimeToTransfer(Car.Chassis()));
    }

    [Fact]
    public void OpenDiffSpinsTheInsideWheelWhereAnLsdPutsPowerDown()
    {
        double InsideSpin(VehicleSimulation sim, out double accel)
        {
            Car.Rolling(sim, 40, 2);
            var input = new VehicleInputs { Steer = 0.35, Throttle = 0.25 };
            var t = sim.Step(Car.Dt, input);
            for (int i = 0; i < 1000; i++) t = sim.Step(Car.Dt, input);
            input.Throttle = 1.0;
            double maxSlip = 0, sumAx = 0;
            for (int i = 0; i < 400; i++)
            {
                t = sim.Step(Car.Dt, input);
                maxSlip = Math.Max(maxSlip, t.SlipRatio[Wheel.RL]);
                sumAx += t.LongitudinalG;
            }
            accel = sumAx / 400;
            return maxSlip;
        }
        double openSlip = InsideSpin(Car.Chassis(("differential", "differential.open_410")), out double openAx);
        double lsdSlip = InsideSpin(Car.Chassis(("differential", "differential.lsd_410")), out double lsdAx);
        Assert.True(openSlip > lsdSlip, $"inside-wheel slip open {openSlip:F2} vs LSD {lsdSlip:F2}");
        Assert.True(lsdAx > openAx, $"forward g open {openAx:F3} vs LSD {lsdAx:F3}");
    }

    [Fact]
    public void PartSwapsChangeMass()
    {
        double stock = Car.Chassis().Config.Mass;
        Assert.Equal(1250, stock, 6);
        var heavier = Car.Chassis(("brakes", "brakes.big_kit")).Config.Mass;
        Assert.Equal(stock + 3, heavier, 6);
        var turboEngine = TurboBuildEngine();
        Assert.True(Car.Create(turboEngine, EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base")), "gasoline_98").Config.Mass > stock);
    }

    private static EngineAssembly TurboBuildEngine() => Simulation.TurboTests.TurboBuild();

    [Fact]
    public void MissedDownshiftOverRevsTheEngine()
    {
        var sim = Car.Chassis();
        Car.Rolling(sim, 150, 4);
        var input = new VehicleInputs { Throttle = 0 };
        sim.Step(Car.Dt, input);
        // Grab second gear instead of third at 150 km/h: the clutch drags the engine far past the limiter.
        input.ShiftDown = true;
        sim.Step(Car.Dt, input);
        input.ShiftDown = false;
        for (int i = 0; i < 200; i++) sim.Step(Car.Dt, input);
        input.ShiftDown = true;
        sim.Step(Car.Dt, input);
        input.ShiftDown = false;
        double peakRpm = 0;
        for (int i = 0; i < 1500; i++) peakRpm = Math.Max(peakRpm, sim.Step(Car.Dt, input).EngineRpm);
        Assert.True(peakRpm > 9000, $"peak {peakRpm:F0} rpm");
        var report = Assert.Single(sim.Engine.Damage.Failures);
        Assert.Contains(report.ContributingFactors, f => f.Contains("above the rev limiter"));
        Assert.True(sim.Engine.Damage.Seized);
    }

    [Fact]
    public void SustainedCorneringStarvesTheOilOnGrippyTyres()
    {
        var slicks = new[] { ("tires_front", "tires.semislick_235_40r17"), ("tires_rear", "tires.semislick_235_40r17") };
        var sim = Car.Chassis(slicks);
        Car.Rolling(sim, 80, 3);
        var input = new VehicleInputs { Steer = 0.18 };
        var t = sim.Step(Car.Dt, input);
        bool warned = false;
        for (int i = 0; i < 3000; i++)
        {
            input.Throttle = Math.Clamp(0.3 + (80 / 3.6 - t.Speed) * 0.3, 0, 1);
            t = sim.Step(Car.Dt, input);
            warned |= sim.Engine.Damage.Warnings.Any(w => w.Code == "oil_surge");
        }
        Assert.True(Math.Abs(t.LateralG) > sim.Engine.Config.OilPan.MaxSustainedG);
        Assert.True(warned);
        double straightLine = sim.Engine.OilPressure(t.Engine.Rpm, t.Engine.OilTemperature, 1.0);
        Assert.True(t.Engine.OilPressure < 0.7 * straightLine, $"{t.Engine.OilPressureBar:F2} bar cornering vs {straightLine / 1e5:F2} bar straight");
    }

    [Fact]
    public void DrivingIsDeterministic()
    {
        (double, double, double) Run()
        {
            var sim = Car.Chassis();
            Car.Accelerate(sim, 120);
            var input = new VehicleInputs { Steer = 0.3, Throttle = 0.6 };
            for (int i = 0; i < 1000; i++) sim.Step(Car.Dt, input);
            return (sim.State.X, sim.State.Y, sim.State.Heading);
        }
        Assert.Equal(Run(), Run());
    }
}
