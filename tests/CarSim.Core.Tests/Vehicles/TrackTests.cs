using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

public class TrackTests
{
    private static readonly TrackLayout Track = TrackLayout.TestFacility();

    [Fact]
    public void TestFacilityClosesAndHasTheDesignedLength()
    {
        Assert.True(Track.ClosureError < 0.5, $"gap {Track.ClosureError:F2} m");
        Assert.InRange(Track.Length, 1070, 1095);
        double tightest = Enumerable.Range(0, Track.Count).Select(i => Math.Abs(Track.CurvatureAt(i))).Max();
        Assert.Equal(1.0 / 20.0, tightest, 6);
    }

    [Fact]
    public void SurfacesAcrossTheTrack()
    {
        // At the start the track runs along +X, centred on y = 0.
        Assert.Equal(Surface.Asphalt, Track.SurfaceAt(10, 0, Track.Nearest(10, 0)));
        Assert.Equal(Surface.Kerb, Track.SurfaceAt(10, 6.0, Track.Nearest(10, 6.0)));
        Assert.Equal(Surface.Grass, Track.SurfaceAt(10, -9.0, Track.Nearest(10, -9.0)));
        Assert.True(TrackLayout.Grip(Surface.Grass) < TrackLayout.Grip(Surface.Kerb));
    }

    private static (double? lap, double maxOffset, VehicleSimulation sim) Lap(VehicleSimulation sim, int laps = 1)
    {
        sim.Track = Track;
        var driver = TrackDriver.ForCar(Track, sim.Config);
        var timer = new LapTimer(Track);
        VehicleTelemetry? t = null;
        double maxOffset = 0;
        for (int i = 0; i < 400 * 500 && timer.Laps < laps; i++)
        {
            var input = driver.Drive(sim, t);
            t = sim.Step(Car.Dt, input);
            timer.Update(driver.Index, t.Time);
            if (t.Time > 3) maxOffset = Math.Max(maxOffset, Math.Abs(Track.LateralOffset(sim.State.X, sim.State.Y, driver.Index)));
            if (sim.Engine.Damage.Seized) break;
        }
        return (timer.LastLap, maxOffset, sim);
    }

    [Fact]
    public void TestDriverLapsTheStockCarOnTheAsphalt()
    {
        var (lap, maxOffset, sim) = Lap(Car.Chassis());
        Assert.NotNull(lap);
        Assert.InRange(lap!.Value, 40, 70);
        Assert.True(maxOffset < Track.Width / 2 + TrackLayout.KerbWidth, $"max offset {maxOffset:F1} m");
        Assert.Empty(sim.Engine.Damage.Failures);
    }

    [Fact]
    public void StickierTyresLapFaster()
    {
        var slicks = new[] { ("tires_front", "tires.semislick_235_40r17"), ("tires_rear", "tires.semislick_235_40r17") };
        double street = Lap(Car.Chassis()).lap!.Value;
        double semi = Lap(Car.Chassis(slicks)).lap!.Value;
        Assert.True(semi < street - 1.0, $"street {street:F2}s, semi-slick {semi:F2}s");
    }

    [Fact]
    public void MorePowerLapsFaster()
    {
        var built = SimFactory.Assembly(("cylinder_head", "k20.head.ported"), ("valve_springs", "k20.valve_springs.performance"),
            ("camshafts", "k20.cams.sport"), ("exhaust", "exhaust.race_76mm"), ("exhaust_manifold", "k20.exhaust_manifold.header421"));
        double stock = Lap(Car.Chassis()).lap!.Value;
        double faster = Lap(Car.Create(built)).lap!.Value;
        Assert.True(faster < stock, $"stock {stock:F2}s, built {faster:F2}s");
    }

    [Fact]
    public void LapTimerCountsCompleteLapsOnly()
    {
        var timer = new LapTimer(Track);
        int n = Track.Count;
        Assert.False(timer.Update(0, 0));
        Assert.False(timer.Update(n / 2, 20));
        Assert.False(timer.Update(n - 1, 40));
        Assert.False(timer.Update(1, 41), "first crossing starts the first timed lap");
        Assert.False(timer.Update(n / 2, 60));
        Assert.False(timer.Update(n - 2, 90));
        Assert.True(timer.Update(2, 92));
        Assert.Equal(51.0, timer.LastLap!.Value, 9);
        Assert.Equal(1, timer.Laps);
    }
}
