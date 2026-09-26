using CarSim.Core.Common;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

/// <summary>
/// Physical invariants of the chassis model, checked against independent statics and conservation laws
/// rather than against the implementation's own formulas.
/// </summary>
public class ChassisInvariantTests
{
    [Fact]
    public void SteadyCorneringTransfersExactlyMassTimesAccelerationTimesCgHeight()
    {
        var sim = Car.Chassis();
        Car.Rolling(sim, 60, 3);
        var input = new VehicleInputs { Steer = 0.12 };
        VehicleTelemetry t = null!;
        for (int i = 0; i < 6.0 / Car.Dt; i++)
        {
            input.Throttle = Math.Clamp(0.25 + (60 / 3.6 - sim.State.U) * 0.3, 0, 1);
            t = sim.Step(Car.Dt, input);
        }
        var c = sim.Config;
        double moment = (t.WheelLoad[Wheel.FR] - t.WheelLoad[Wheel.FL]) / 2 * c.TrackFront
                        + (t.WheelLoad[Wheel.RR] - t.WheelLoad[Wheel.RL]) / 2 * c.TrackRear;
        double statics = c.Mass * t.LateralG * PhysicalConstants.Gravity * c.CgHeight;
        Assert.True(Math.Abs(t.LateralG) > 0.4, $"{t.LateralG:F2} g");
        Assert.Equal(1.0, moment / statics, 2);
        // Vertical equilibrium: the wheels carry the car.
        Assert.Equal(c.Mass * PhysicalConstants.Gravity, t.WheelLoad.Sum(), 0);
    }

    [Fact]
    public void BrakingMovesMassTimesDecelerationTimesCgHeightOverWheelbaseToTheFront()
    {
        var sim = Car.Chassis();
        Car.Rolling(sim, 100, 0);
        var input = new VehicleInputs();
        VehicleTelemetry t = null!;
        for (int i = 0; i < 1.5 / Car.Dt; i++)
        {
            input.Brake = Math.Min(0.4, i * Car.Dt / 0.3);
            t = sim.Step(Car.Dt, input);
        }
        var c = sim.Config;
        double staticFront = c.Mass * PhysicalConstants.Gravity * c.CgToRear / c.Wheelbase;
        double transfer = t.WheelLoad[Wheel.FL] + t.WheelLoad[Wheel.FR] - staticFront;
        double statics = -c.Mass * t.LongitudinalG * PhysicalConstants.Gravity * c.CgHeight / c.Wheelbase;
        Assert.True(t.LongitudinalG < -0.5, $"{t.LongitudinalG:F2} g");
        Assert.Equal(1.0, transfer / statics, 2);
    }

    [Fact]
    public void MassAndCentreOfGravityFollowThePartsAndTheRideHeight()
    {
        var db = TestContent.Database;
        var stock = Car.Create().Config;
        var sleeved = Car.Create(SimFactory.Assembly(("block", "k20.block.sleeved"))).Config;
        double delta = db.GetPart("k20.block.sleeved").MassKg - db.GetPart("k20.block.oem").MassKg;
        Assert.Equal(stock.Mass + delta, sleeved.Mass, 9);
        Assert.True(sleeved.FrontWeightFraction > stock.FrontWeightFraction, "the engine sits over the front axle");
        // Front axle load changes by exactly the added mass (it is carried at the front).
        Assert.Equal(delta, sleeved.Mass * sleeved.FrontWeightFraction - stock.Mass * stock.FrontWeightFraction, 9);
        var lowered = Car.Chassis(("suspension", "suspension.coilover_track")).Config;
        var spec = db.GetPart("suspension.coilover_track").GetSpec<CarSim.Core.Parts.Specs.SuspensionSpec>();
        Assert.Equal(stock.CgHeight + 0.8 * Units.MmToM(spec.RideHeightOffsetMm), lowered.CgHeight, 9);
    }

    [Fact]
    public void CoastingLosesExactlyTheWorkDoneByDragAndRollingResistance()
    {
        var sim = Car.Chassis();
        Car.Rolling(sim, 100, 0);
        var c = sim.Config;
        double Energy()
        {
            double e = 0.5 * c.Mass * (sim.State.U * sim.State.U + sim.State.V * sim.State.V);
            for (int w = 0; w < 4; w++) e += 0.5 * c.TireOf(w).InertiaKgM2 * sim.State.WheelOmega[w] * sim.State.WheelOmega[w];
            return e;
        }
        double start = Energy(), work = 0;
        var input = new VehicleInputs();
        var t = sim.Step(Car.Dt, input);
        start = Energy();
        for (int i = 0; i < 20.0 / Car.Dt; i++)
        {
            double u = sim.State.U;
            double drag = 0.5 * VehicleSimulation.AirDensity * c.DragArea * u * u * u;
            double rolling = 0;
            for (int w = 0; w < 4; w++)
                rolling += t.WheelLoad[w] * TireModel.RollingResistance(c.TireOf(w), sim.Tyres.StateOf(w).PressureKpa) * Math.Abs(sim.State.WheelOmega[w] * c.TireOf(w).Radius);
            work += (drag + rolling) * Car.Dt;
            t = sim.Step(Car.Dt, input);
        }
        Assert.True(sim.State.U < 0.8 * 100 / 3.6);
        Assert.Equal(1.0, (start - Energy()) / work, 2);
    }

    [Fact]
    public void ResultsConvergeAsTheTimestepShrinks()
    {
        double Accelerate(double dt)
        {
            var sim = Car.Chassis();
            var input = new VehicleInputs { ShiftUp = true };
            var t = sim.Step(dt, input);
            input.ShiftUp = false;
            input.Throttle = 1;
            double time = 0;
            while (t.SpeedKmh < 100 && time < 30)
            {
                input.ShiftUp = t.EngineRpm > 7200 && t.Gear < sim.Config.Gearbox.Ratios.Count && sim.State.ShiftTimer <= 0;
                t = sim.Step(dt, input);
                time += dt;
            }
            return time;
        }
        double Stop(double dt)
        {
            var sim = Car.Chassis();
            Car.Rolling(sim, 100, 0);
            double start = sim.State.Distance, elapsed = 0;
            var input = new VehicleInputs();
            while (sim.State.U > 0.1 && elapsed < 20)
            {
                input.Brake = Math.Min(0.5, elapsed / 0.3);
                sim.Step(dt, input);
                elapsed += dt;
            }
            return sim.State.Distance - start;
        }
        double fine = Accelerate(0.001), coarse = Accelerate(0.004);
        Assert.InRange(coarse / fine, 0.99, 1.01);
        double stopFine = Stop(0.001), stopCoarse = Stop(0.004);
        Assert.InRange(stopCoarse / stopFine, 0.99, 1.01);
    }
}
