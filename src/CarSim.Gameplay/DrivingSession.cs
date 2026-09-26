using CarSim.Core.Damage;
using CarSim.Core.Vehicles;

namespace CarSim.Gameplay;

/// <summary>What happened during one <see cref="DrivingSession.Advance"/> call.</summary>
public sealed record DrivingStep(int Steps, bool LapCompleted, IReadOnlyList<FailureReport> NewFailures);

/// <summary>
/// A car on the test track: fixed-step simulation decoupled from the frame rate, lap timing, gear-shift
/// requests that wait out a shift in progress, recovery to the track, and an optional autopilot. The
/// presentation layer feeds it player inputs and frame times and draws <see cref="Sim"/>'s state.
/// </summary>
public sealed class DrivingSession
{
    /// <summary>Simulation step (s). The vehicle model substeps internally.</summary>
    public const double StepSeconds = 0.002;

    /// <summary>Longest frame time simulated at once; longer hitches slow the simulation instead of spiralling.</summary>
    public const double MaxFrameSeconds = 0.1;

    private double _accumulator;
    private int _queuedShift;
    private int _knownFailures, _knownChassisFailures;
    private TrackDriver? _autopilot;
    private double _lostSeconds;

    /// <summary>How long the autopilot tolerates being lost (far off the track, facing backwards or stuck) before recovering.</summary>
    public const double AutopilotLostSeconds = 2.0;
    private readonly List<FailureReport> _failures = new();

    public DrivingSession(VehicleSimulation sim, TrackLayout track)
    {
        Sim = sim;
        Track = track;
        Sim.Track = track;
        Timer = new LapTimer(track);
        TrackIndex = track.Nearest(sim.State.X, sim.State.Y);
        _knownFailures = sim.Engine.Damage.Failures.Count;
        _knownChassisFailures = sim.Wear.Failures.Count;
    }

    public VehicleSimulation Sim { get; }
    public TrackLayout Track { get; }
    public LapTimer Timer { get; }

    /// <summary>Nearest centreline sample to the car.</summary>
    public int TrackIndex { get; private set; }

    /// <summary>The built-in test driver takes over the controls.</summary>
    public bool AutopilotEnabled { get; set; }

    /// <summary>Times the autopilot got lost (spun off, stuck) and put the car back on the track.</summary>
    public int AutopilotRecoveries { get; private set; }

    /// <summary>Failures (engine and chassis) that happened during this session, oldest first.</summary>
    public IReadOnlyList<FailureReport> Failures => _failures;

    public VehicleTelemetry? Last => Sim.Last;

    /// <summary>Lateral distance from the centreline (m, positive = left).</summary>
    public double LateralOffset => Track.LateralOffset(Sim.State.X, Sim.State.Y, TrackIndex);

    /// <summary>All four wheels on the grass.</summary>
    public bool OffTrack => Sim.WheelSurface.All(s => s == Surface.Grass);

    /// <summary>Tyres straight out of the garage: at ambient temperature and cold pressure (an out lap to warm them).</summary>
    public void StartOnColdTyres() => Sim.Tyres.SetAll(TyreThermalModel.AmbientK);

    /// <summary>Queue a gear change; it is applied as soon as no shift is in progress.</summary>
    public void RequestShift(int direction) => _queuedShift = Math.Sign(direction);

    /// <summary>
    /// Runs the simulation for <paramref name="frameSeconds"/> of real time (clamped to
    /// <see cref="MaxFrameSeconds"/>) in fixed steps. Shift flags in <paramref name="input"/> are ignored:
    /// use <see cref="RequestShift"/>.
    /// </summary>
    public DrivingStep Advance(double frameSeconds, VehicleInputs input)
    {
        _accumulator += Math.Clamp(frameSeconds, 0.0, MaxFrameSeconds);
        int steps = 0;
        bool lap = false;
        var fresh = new List<FailureReport>();
        while (_accumulator >= StepSeconds)
        {
            _accumulator -= StepSeconds;
            VehicleInputs stepInput;
            if (AutopilotEnabled)
            {
                _autopilot ??= TrackDriver.ForCar(Track, Sim.Config);
                stepInput = _autopilot.Drive(Sim, Sim.Last);
                _queuedShift = 0;
            }
            else
            {
                stepInput = input;
                stepInput.ShiftUp = stepInput.ShiftDown = false;
                if (_queuedShift != 0 && Sim.State.ShiftTimer <= 0)
                {
                    if (_queuedShift > 0) stepInput.ShiftUp = true; else stepInput.ShiftDown = true;
                    _queuedShift = 0;
                }
            }
            var t = Sim.Step(StepSeconds, stepInput);
            steps++;
            TrackIndex = Track.Nearest(t.X, t.Y, TrackIndex);
            lap |= Timer.Update(TrackIndex, t.Time);
            if (AutopilotEnabled) CheckAutopilotLost(t);
            var failures = Sim.Engine.Damage.Failures;
            for (; _knownFailures < failures.Count; _knownFailures++) fresh.Add(failures[_knownFailures]);
            var chassisFailures = Sim.Wear.Failures;
            for (; _knownChassisFailures < chassisFailures.Count; _knownChassisFailures++) fresh.Add(chassisFailures[_knownChassisFailures]);
        }
        _failures.AddRange(fresh);
        return new DrivingStep(steps, lap, fresh);
    }

    private void CheckAutopilotLost(VehicleTelemetry t)
    {
        double headingError = Math.Abs(TrackLayout.NormalizeAngle(Sim.State.Heading - Track.HeadingAt(TrackIndex)));
        bool lost = Math.Abs(LateralOffset) > Track.Width / 2 + TrackLayout.KerbWidth + 4.0
                    || headingError > 2.0
                    || (t.Speed < 0.5 && t.Time > 5.0 && !Sim.Engine.Damage.Seized);
        _lostSeconds = lost ? _lostSeconds + StepSeconds : 0.0;
        if (_lostSeconds < (t.Speed < 0.5 ? 2 * AutopilotLostSeconds : AutopilotLostSeconds)) return;
        ResetToTrack();
        AutopilotRecoveries++;
    }

    /// <summary>
    /// Recovery: puts the car back on the centreline at the nearest point, pointing along the track and
    /// stopped in first gear. The engine keeps its state (a seized engine stays seized). Voids the lap.
    /// </summary>
    public void ResetToTrack()
    {
        var s = Sim.State;
        int i = Track.Nearest(s.X, s.Y);
        var (x, y) = Track.Point(i);
        s.X = x; s.Y = y;
        s.Heading = Track.HeadingAt(i);
        s.U = s.V = s.YawRate = 0.0;
        s.Ax = s.Ay = 0.0;
        s.LongTransfer = s.LongTransferRate = s.LatTransfer = s.LatTransferRate = 0.0;
        for (int w = 0; w < 4; w++) s.WheelOmega[w] = 0.0;
        s.SteerAngle = 0.0;
        s.ShiftTimer = 0.0;
        s.Gear = s.PendingGear = 1;
        s.Clutch = 0.0;
        _queuedShift = 0;
        _lostSeconds = 0;
        TrackIndex = i;
        Timer.Invalidate();
    }
}

/// <summary>
/// Turns digital keys into smooth pedal and steering inputs, with a steering assist that limits lock
/// at speed to roughly what the front tyres can use (so tapping a key does not spin the car at 150 km/h).
/// Analogue devices bypass it.
/// </summary>
public sealed class KeyboardInputFilter
{
    public double ThrottleRise = 5.0, ThrottleFall = 8.0;
    public double BrakeRise = 6.0, BrakeFall = 10.0;
    public double SteerRate = 2.5, SteerReturn = 4.0;

    /// <summary>Steering-assist margin over the lock needed for the tyres' grip.</summary>
    public double AssistMargin = 1.3;

    public double Throttle { get; private set; }
    public double Brake { get; private set; }
    public double Steer { get; private set; }

    /// <param name="dt">Frame time (s).</param>
    /// <param name="throttle">Throttle key held.</param>
    /// <param name="brake">Brake key held.</param>
    /// <param name="steer">−1 right, 0, +1 left.</param>
    /// <param name="speed">Car speed (m/s).</param>
    /// <param name="car">The car (for wheelbase, lock and front-tyre grip).</param>
    public void Update(double dt, bool throttle, bool brake, int steer, double speed, VehicleConfiguration car)
    {
        Throttle = Ramp(Throttle, throttle ? 1.0 : 0.0, throttle ? ThrottleRise : ThrottleFall, dt);
        Brake = Ramp(Brake, brake ? 1.0 : 0.0, brake ? BrakeRise : BrakeFall, dt);
        double limit = SteerLimit(speed, car);
        double target = Math.Clamp(steer, -1, 1) * limit;
        bool returning = steer == 0 || Math.Sign(steer) != Math.Sign(Steer);
        Steer = Ramp(Steer, target, returning ? SteerReturn : SteerRate, dt);
        Steer = Math.Clamp(Steer, -limit, limit);
    }

    /// <summary>
    /// Fraction of full lock the assist allows: kinematic lock for a turn at the front tyres' grip
    /// (wheelbase·μg/v²) plus the peak slip angle, with margin.
    /// </summary>
    public double SteerLimit(double speed, VehicleConfiguration car)
    {
        var tire = car.TiresFront;
        double v2 = Math.Max(speed * speed, 1.0);
        double needed = car.Wheelbase * tire.PeakFriction * CarSim.Core.Common.PhysicalConstants.Gravity / v2
                        + CarSim.Core.Vehicles.TireModel.PeakSlipAngle(tire, tire.OptimalPressureKpa);
        return Math.Clamp(AssistMargin * needed / car.MaxSteer, 0.1, 1.0);
    }

    public VehicleInputs ToInputs(double handbrake = 0.0, bool starter = false) => new()
    {
        Throttle = Throttle, Brake = Brake, Steer = Steer, Handbrake = handbrake, Starter = starter, Ignition = true,
    };

    private static double Ramp(double value, double target, double rate, double dt) =>
        value + Math.Clamp(target - value, -rate * dt, rate * dt);
}
