using CarSim.Core.Vehicles;

namespace CarSim.Gameplay;

/// <summary>
/// Results of the standard chassis tests for one set-up. <see cref="BumpySkidpadG"/> is the same test on a
/// kerb-grade rough surface, where stiffness, low ride height and damping have a price.
/// </summary>
public sealed record BenchResult(double SkidpadG, double BrakingDistance100M, double FrontBrakeShare, double FrontRollShare, string Problem = "",
    double BumpySkidpadG = 0)
{
    public bool Ok => Problem.Length == 0;
}

/// <summary>
/// Standard chassis tests for comparing set-ups without a lap: steady cornering at 80 km/h (steering
/// wound on until the car lets go) on smooth asphalt and on a bumpy (kerb-grade) surface, and a 100–0 km/h
/// stop at the best pedal pressure before lock-up.
/// Runs on a copy of the simulation with wear and damage switched off, so testing costs nothing.
/// </summary>
public static class ChassisBench
{
    public const double Dt = 0.002;

    public static BenchResult Run(Garage garage)
    {
        var (probe, problem) = garage.CreateVehicleSimulation();
        if (probe == null) return new BenchResult(0, 0, 0, 0, problem);
        var c = probe.Config;
        double frontBrake = c.Brakes.FrontMaxTorqueNm;
        double rearBrake = c.Brakes.RearMaxTorqueNm * c.Brakes.RearPressureFactor;
        double skidpad = Skidpad(Fresh(garage));
        var bumpy = Fresh(garage);
        for (int w = 0; w < 4; w++) bumpy.WheelSurface[w] = Surface.Kerb;
        double bumpySkidpad = Skidpad(bumpy);
        double braking = BestStop(garage);
        return new BenchResult(skidpad, braking, frontBrake / (frontBrake + rearBrake), c.FrontRollShare, BumpySkidpadG: bumpySkidpad);
    }

    private static VehicleSimulation Fresh(Garage garage)
    {
        var sim = garage.CreateVehicleSimulation().Sim!;
        sim.Wear.Enabled = false;
        sim.Engine.DamageEnabled = false;
        return sim;
    }

    private static void Rolling(VehicleSimulation sim, double kmh, int gear)
    {
        var s = sim.State;
        s.U = kmh / 3.6;
        for (int w = 0; w < 4; w++) s.WheelOmega[w] = s.U / sim.Config.TireOf(w).Radius;
        s.Gear = gear;
        s.Clutch = 1;
        if (gear != 0) s.EngineOmega = s.WheelOmega[Wheel.RL] * sim.Config.OverallRatio(gear);
    }

    /// <summary>Highest steady lateral g at ~80 km/h before the body slip angle passes 8.6°.</summary>
    public static double Skidpad(VehicleSimulation sim, double kmh = 80)
    {
        Rolling(sim, kmh, 3);
        var input = new VehicleInputs();
        double max = 0, time = 0;
        var t = sim.Step(Dt, input);
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

    /// <summary>100–0 km/h stopping distance with the pedal ramped (0.3 s) to <paramref name="pedal"/>, and whether a wheel locked.</summary>
    public static (double Distance, bool Locked) Stop(VehicleSimulation sim, double pedal, double fromKmh = 100)
    {
        Rolling(sim, fromKmh, 0);
        var input = new VehicleInputs();
        double start = sim.State.Distance;
        bool locked = false;
        var t = sim.Step(Dt, input);
        for (int i = 0; i < 20_000 && t.Speed > 0.05; i++)
        {
            input.Brake = pedal * Math.Min(1.0, i * Dt / 0.3);
            t = sim.Step(Dt, input);
            if (t.Speed > 3)
                for (int w = 0; w < 4; w++) locked |= t.WheelSpeed[w] < 0.3 * t.Speed;
        }
        return (sim.State.Distance - start, locked);
    }

    /// <summary>
    /// Threshold braking: the stop at the firmest pedal that does not lock a wheel (found by bisection),
    /// or a full-pedal stop if even that does not lock.
    /// </summary>
    public static double BestStop(Garage garage, double fromKmh = 100)
    {
        var full = Stop(Fresh(garage), 1.0, fromKmh);
        if (!full.Locked) return full.Distance;
        double ok = 0.15, bad = 1.0, okDistance = Stop(Fresh(garage), ok, fromKmh).Distance;
        for (int i = 0; i < 7; i++)
        {
            double mid = 0.5 * (ok + bad);
            var r = Stop(Fresh(garage), mid, fromKmh);
            if (r.Locked) bad = mid;
            else { ok = mid; okDistance = r.Distance; }
        }
        return okDistance;
    }
}
