using CarSim.Core.Tests.Vehicles;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.Gameplay;

public class DrivingSessionTests
{
    private static readonly TrackLayout Track = TrackLayout.TestFacility();
    private const double Frame = 1.0 / 60.0;

    [Fact]
    public void FrameTimesAreSimulatedInFixedSteps()
    {
        var session = new DrivingSession(Car.Chassis(), Track);
        int steps = 0;
        for (int i = 0; i < 60; i++) steps += session.Advance(Frame, new VehicleInputs()).Steps;
        Assert.InRange(steps, 499, 500);
        Assert.Equal(steps * DrivingSession.StepSeconds, session.Sim.State.Time, 9);
        // A long hitch is clamped rather than simulated in one go.
        int clamped = (int)Math.Round(DrivingSession.MaxFrameSeconds / DrivingSession.StepSeconds);
        Assert.InRange(session.Advance(2.0, new VehicleInputs()).Steps, clamped - 1, clamped + 1);
    }

    [Fact]
    public void AutopilotLapsTheTrackAtFrameRate()
    {
        var session = new DrivingSession(Car.Chassis(), Track) { AutopilotEnabled = true };
        for (int i = 0; i < 150 * 60 && session.Timer.Laps < 1; i++) session.Advance(Frame, new VehicleInputs());
        Assert.Equal(1, session.Timer.Laps);
        Assert.InRange(session.Timer.LastLap!.Value, 40, 70);
        Assert.Empty(session.Failures);
    }

    [Fact]
    public void ShiftRequestsWaitForTheShiftInProgress()
    {
        var session = new DrivingSession(Car.Chassis(), Track);
        var input = new VehicleInputs { Throttle = 0.5 };
        session.RequestShift(+1);
        session.Advance(Frame, input);
        Assert.True(session.Sim.State.ShiftTimer > 0, "first shift started");
        session.RequestShift(+1); // pressed again while the first shift is still in progress
        for (int i = 0; i < 60; i++) session.Advance(Frame, input);
        Assert.Equal(2, session.Sim.State.Gear);
        // Shift flags passed in directly are ignored: shifting goes through RequestShift.
        for (int i = 0; i < 30; i++) session.Advance(Frame, new VehicleInputs { ShiftUp = true });
        Assert.Equal(2, session.Sim.State.Gear);
    }

    [Fact]
    public void RecoveryPutsTheCarBackOnTheTrackAndVoidsTheLap()
    {
        var session = new DrivingSession(Car.Chassis(), Track);
        var s = session.Sim.State;
        s.X = 200; s.Y = -25; s.Heading = 2.0; s.U = 20; s.YawRate = 1.0;
        session.Timer.Update(Track.Count / 2, 5);
        session.Timer.Update(Track.Count - 1, 10);
        session.Timer.Update(0, 11);
        Assert.True(session.Timer.Timing);
        session.ResetToTrack();
        Assert.Equal(0.0, s.U);
        Assert.Equal(0.0, s.YawRate);
        Assert.Equal(1, s.Gear);
        Assert.True(Math.Abs(session.LateralOffset) < 0.01);
        Assert.Equal(Track.HeadingAt(session.TrackIndex), s.Heading, 9);
        Assert.False(session.Timer.Timing);
        session.Advance(0.5, new VehicleInputs());
        Assert.True(Math.Abs(session.LateralOffset) < 0.5, "stays put after recovery");
    }

    [Fact]
    public void FailuresWhileDrivingAreReportedOnce()
    {
        var session = new DrivingSession(Car.Chassis(), Track);
        Car.Rolling(session.Sim, 160, 5);
        var reported = new List<CarSim.Core.Damage.FailureReport>();
        for (int i = 0; i < 300; i++)
        {
            // Stab down through the box to second at 160 km/h.
            if (session.Sim.State.Gear > 2 && session.Sim.State.ShiftTimer <= 0) session.RequestShift(-1);
            reported.AddRange(session.Advance(Frame, new VehicleInputs()).NewFailures);
        }
        Assert.True(session.Sim.Engine.Damage.Seized, "money-shifting into 2nd at 160 km/h destroys the engine");
        Assert.NotEmpty(reported);
        Assert.Equal(session.Failures, reported);
        Assert.Equal(session.Sim.Engine.Damage.Failures.Count, reported.Count);
    }

    [Fact]
    public void AutopilotRecoversWhenLost()
    {
        var session = new DrivingSession(Car.Chassis(), Track) { AutopilotEnabled = true };
        var s = session.Sim.State;
        s.X = 200; s.Y = -40; s.Heading = Math.PI; // in a field, facing the wrong way
        for (int i = 0; i < 6 * 60 && session.AutopilotRecoveries == 0; i++) session.Advance(Frame, new VehicleInputs());
        Assert.Equal(1, session.AutopilotRecoveries);
        Assert.True(Math.Abs(session.LateralOffset) < Track.Width / 2);
        for (int i = 0; i < 5 * 60; i++) session.Advance(Frame, new VehicleInputs());
        Assert.Equal(1, session.AutopilotRecoveries);
        Assert.True(session.Last!.SpeedKmh > 20, "drives on after the recovery");
    }

    [Fact]
    public void ChassisFailuresAreReportedLikeEngineFailures()
    {
        var sim = Car.Create(CarSim.Core.Tests.Simulation.TurboTests.TurboBuild(),
            CarSim.Core.Ecu.EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base")), "gasoline_98");
        sim.Config.Chassis.PartIn("clutch")!.Wear = 0.9999; // a few more seconds of slipping finishes it
        var session = new DrivingSession(sim, Track);
        Car.Rolling(sim, 90, 4);
        var reported = new List<CarSim.Core.Damage.FailureReport>();
        for (int i = 0; i < 10 * 60 && reported.Count == 0; i++)
            reported.AddRange(session.Advance(Frame, new VehicleInputs { Throttle = 1 }).NewFailures);
        var report = Assert.Single(reported);
        Assert.Equal(CarSim.Core.Damage.FailureMode.ClutchBurnout, report.Mode);
        Assert.Equal(session.Failures, reported);
    }

    [Fact]
    public void KeyboardSteeringAssistLimitsLockAtSpeed()
    {
        var car = Car.Chassis().Config;
        var filter = new KeyboardInputFilter();
        Assert.Equal(1.0, filter.SteerLimit(3, car));
        double at100 = filter.SteerLimit(100 / 3.6, car);
        double at180 = filter.SteerLimit(180 / 3.6, car);
        Assert.InRange(at100, 0.1, 0.6);
        Assert.True(at180 < at100);

        for (int i = 0; i < 60; i++) filter.Update(Frame, true, false, +1, 100 / 3.6, car);
        Assert.Equal(1.0, filter.Throttle, 6);
        Assert.Equal(at100, filter.Steer, 6);
        filter.Update(Frame, false, false, 0, 100 / 3.6, car);
        Assert.True(filter.Steer < at100, "steering returns when the key is released");
        Assert.True(filter.Throttle < 1.0);
    }
}
