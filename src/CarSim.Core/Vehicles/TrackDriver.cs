using CarSim.Core.Common;

namespace CarSim.Core.Vehicles;

/// <summary>
/// A simple, deterministic test driver: pure-pursuit steering along the centreline and a target-speed
/// profile from track curvature and braking capability. Used for automated lap tests and demos; it is
/// deliberately not a racing AI (it drives the centreline with margin).
/// </summary>
public sealed class TrackDriver
{
    private readonly TrackLayout _track;
    private readonly double[] _targetSpeed;
    private readonly double _plannedLateral;
    private int _index = -1;

    /// <param name="track">Circuit.</param>
    /// <param name="lateralG">Cornering acceleration the driver plans for.</param>
    /// <param name="brakingG">Deceleration the driver plans for.</param>
    public TrackDriver(TrackLayout track, double lateralG = 0.75, double brakingG = 0.7)
    {
        _track = track;
        int n = track.Count;
        _targetSpeed = new double[n];
        double aLat = lateralG * PhysicalConstants.Gravity, aBrake = brakingG * PhysicalConstants.Gravity;
        _plannedLateral = aLat;
        for (int i = 0; i < n; i++)
        {
            double k = Math.Abs(track.CurvatureAt(i));
            _targetSpeed[i] = k > 1e-4 ? Math.Min(70.0, Math.Sqrt(aLat / k)) : 70.0;
        }
        // Backward passes: be slow enough before each corner to brake down to it.
        for (int pass = 0; pass < 2; pass++)
            for (int i = n - 1; i >= 0; i--)
            {
                int next = track.Wrap(i + 1);
                double ds = Math.Max(0.5, track.DistanceAt(next) - track.DistanceAt(i));
                if (next == 0) ds = Math.Max(0.5, track.Length - track.DistanceAt(i));
                _targetSpeed[i] = Math.Min(_targetSpeed[i], Math.Sqrt(_targetSpeed[next] * _targetSpeed[next] + 2 * aBrake * ds));
            }
    }

    /// <summary>A driver who plans cornering and braking around the grip of the car's fitted tyres.</summary>
    public static TrackDriver ForCar(TrackLayout track, VehicleConfiguration car)
    {
        double mu = 0.5 * (car.TiresFront.PeakFriction + car.TiresRear.PeakFriction);
        return new TrackDriver(track, lateralG: 0.8 * mu, brakingG: 0.78 * mu);
    }

    public int Index => _index;
    public double TargetSpeedAt(int i) => _targetSpeed[_track.Wrap(i)];

    public VehicleInputs Drive(VehicleSimulation sim, VehicleTelemetry? last)
    {
        var s = sim.State;
        _index = _track.Nearest(s.X, s.Y, _index);
        double speed = Math.Sqrt(s.U * s.U + s.V * s.V);

        // Pure pursuit.
        double lookahead = 8.0 + 0.6 * speed;
        int ahead = _index;
        double travelled = 0;
        while (travelled < lookahead)
        {
            var (ax, ay) = _track.Point(ahead);
            var (bx, by) = _track.Point(ahead + 1);
            travelled += Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            ahead++;
        }
        var (tx, ty) = _track.Point(ahead);
        double dx = tx - s.X, dy = ty - s.Y;
        double localY = -dx * Math.Sin(s.Heading) + dy * Math.Cos(s.Heading);
        double dist = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        double curvature = 2.0 * localY / (dist * dist);
        // Pure pursuit plus a correction on the lateral offset (the car understeers, especially on the brakes).
        double offset = _track.LateralOffset(s.X, s.Y, _index);
        double offsetGain = 0.03 * Math.Min(1.0, 100.0 / Math.Max(1.0, speed * speed));
        double steerAngle = Math.Atan(curvature * sim.Config.Wheelbase) - offsetGain * offset;
        // Never ask for much more lateral acceleration than the tyres can give at this speed.
        double aMax = 1.1 * _plannedLateral;
        double steerLimit = Math.Atan(sim.Config.Wheelbase * aMax / Math.Max(1.0, speed * speed)) * 1.6;
        steerAngle = Math.Clamp(steerAngle, -steerLimit, steerLimit);
        var input = new VehicleInputs { Steer = Math.Clamp(steerAngle / sim.Config.MaxSteer, -1, 1) };
        double turning = Math.Min(1.0, Math.Abs(Math.Tan(steerAngle)) / sim.Config.Wheelbase * speed * speed / aMax);

        // Speed: aim for the slowest target within the next stretch; crawl back if off the circuit.
        double target = double.MaxValue;
        for (int k = 0; k < 12; k++) target = Math.Min(target, _targetSpeed[_track.Wrap(_index + k)]);
        if (Math.Abs(offset) > _track.Width / 2 + TrackLayout.KerbWidth) target = Math.Min(target, 10.0);
        double error = target - speed;
        if (error < -1.0) input.Brake = Math.Clamp(-error / 6.0, 0.1, 0.55) * (1.0 - 0.6 * turning);
        else input.Throttle = Math.Clamp(0.25 + error / 3.0, 0.0, 1.0);

        // Traction and stability: back off when the driven wheels spin or the car starts to slide.
        if (last != null)
        {
            int d0 = sim.Config.RearWheelDrive ? Wheel.RL : Wheel.FL;
            double spin = Math.Max(last.SlipRatio[d0], last.SlipRatio[d0 + 1]);
            if (spin > 0.10) input.Throttle *= Math.Max(0.1, 1.0 - (spin - 0.10) * 6.0);
            double slide = Math.Abs(last.BodySlipAngle);
            if (slide > 0.05) input.Throttle *= Math.Max(0.0, 1.0 - (slide - 0.05) * 12.0);
            if (!last.Engine.Running && !last.Engine.Seized && speed < 3.0) { input.Starter = true; input.Throttle = 0.2; }
        }

        // Gears, judged from gearbox-side speed (engine speed is meaningless while the clutch is open).
        if (s.ShiftTimer <= 0 && last != null)
        {
            double limit = sim.Engine.Ecu.EffectiveRevLimit;
            int d0 = sim.Config.RearWheelDrive ? Wheel.RL : Wheel.FL;
            double wheelOmega = 0.5 * (s.WheelOmega[d0] + s.WheelOmega[d0 + 1]);
            double gearRpm = Units.RadPerSecToRpm(wheelOmega * sim.Config.OverallRatio(s.Gear));
            if (s.Gear == 0) input.ShiftUp = true;
            else if (gearRpm > limit - 400 && s.Gear < sim.Config.Gearbox.Ratios.Count) input.ShiftUp = true;
            else if (s.Gear > 1 && gearRpm < 3500)
            {
                double lower = Units.RadPerSecToRpm(wheelOmega * sim.Config.OverallRatio(s.Gear - 1));
                if (lower < limit - 900) input.ShiftDown = true;
            }
        }
        return input;
    }
}
