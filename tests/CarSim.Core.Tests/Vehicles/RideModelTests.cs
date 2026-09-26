using CarSim.Core.Parts;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

/// <summary>
/// Suspension set-up must be a trade-off. Originally springs, dampers, bars and ride height only changed
/// roll, load-transfer lag and CG height, so stiffer and lower was always faster (review H2).
/// </summary>
public class RideModelTests
{
    private const double Ms = 280, Mu = 42, K = 110_000, Kt = 200_000;

    [Fact]
    public void FrequencyResponseMatchesATimeDomainQuarterCar()
    {
        // Drive the quarter car with white road velocity (deterministic generator) and compare the tyre-load
        // variance with the frequency-domain integral the ride model uses.
        const double c = 4000, dt = 2e-4, seconds = 400, s0 = 1e-4; // one-sided road-velocity PSD, (m/s)²/Hz
        double sigmaV = Math.Sqrt(s0 / (2 * dt));
        ulong state = 0x9E3779B97F4A7C15;
        double Gauss()
        {
            double U()
            {
                state ^= state << 13; state ^= state >> 7; state ^= state << 17;
                return ((state >> 11) + 0.5) / (1UL << 53);
            }
            return Math.Sqrt(-2 * Math.Log(U())) * Math.Cos(2 * Math.PI * U());
        }
        double zs = 0, vs = 0, zu = 0, vu = 0, zr = 0, sum = 0, sum2 = 0;
        int n = (int)(seconds / dt), warm = (int)(20 / dt);
        for (int i = 0; i < n; i++)
        {
            zr += Gauss() * sigmaV * dt;
            double fs = K * (zu - zs) + c * (vu - vs);
            double ft = Kt * (zr - zu);
            vs += fs / Ms * dt; zs += vs * dt;
            vu += (ft - fs) / Mu * dt; zu += vu * dt;
            if (i < warm) continue;
            sum += ft; sum2 += ft * ft;
        }
        int m = n - warm;
        double variance = sum2 / m - (sum / m) * (sum / m);
        double predicted = s0 * RideModel.Integrate(Ms, Mu, K, c, Kt).Force;
        Assert.InRange(variance / predicted, 0.9, 1.1);
    }

    [Fact]
    public void LoadFluctuationGrowsWithTheSquareRootOfSpeedAndTheRoughness()
    {
        var ride = Car.Chassis().Config.Ride;
        double at20 = ride.TyreLoadSigma(0, 20, Surface.Kerb, 0), at80 = ride.TyreLoadSigma(0, 80, Surface.Kerb, 0);
        Assert.Equal(2.0, at80 / at20, 9);
        Assert.Equal(0.0, ride.TyreLoadSigma(0, 0, Surface.Kerb, 0));
        Assert.Equal(Math.Sqrt(RideModel.Roughness(Surface.Grass) / RideModel.Roughness(Surface.Asphalt)),
            ride.TyreLoadSigma(1, 30, Surface.Grass, 0) / ride.TyreLoadSigma(1, 30, Surface.Asphalt, 0), 9);
    }

    [Fact]
    public void DampingHasAnOptimumInsideTheAdjustmentRange()
    {
        // Too little damping lets the wheel hop; too much passes the bumps straight through.
        double Force(double c) => RideModel.Integrate(Ms, Mu, K, c, Kt).Force;
        var track = TestContent.Database.GetPart("suspension.coilover_track").Adjustments.Single(a => a.Field == "front_damper_ns_m");
        double best = Enumerable.Range(0, 200).Select(i => 500.0 + 100 * i).MinBy(Force);
        Assert.InRange(best, track.Min + 500, track.Max - 500);
        Assert.True(Force(1000) > 1.5 * Force(best) && Force(15000) > 1.3 * Force(best));
    }

    [Fact]
    public void StiffSpringsBarsAndHeavyWheelsCostGripOnBumps()
    {
        double Force(double k, double mu = Mu) => RideModel.Integrate(Ms, mu, k, 4000, Kt).Force;
        Assert.True(Force(45_000) < Force(70_000) && Force(70_000) < Force(110_000), "stiffer springs");
        Assert.True(Force(K, 30) < Force(K, 42) && Force(K, 42) < Force(K, 55), "unsprung mass");
        // Anti-roll bars stiffen the roll mode: one bumped wheel twists the bar.
        Assert.Equal(2 * 900 * 180 / Math.PI / (1.47 * 1.47), RideModel.AntiRollBarWheelRate(900, 1.47), 9);
        var soft = Car.Chassis().Config;
        var track = Car.Chassis(("suspension", "suspension.coilover_track")).Config;
        Assert.True(track.Ride.TyreLoadSigma(0, 30, Surface.Kerb, 0) > 1.2 * soft.Ride.TyreLoadSigma(0, 30, Surface.Kerb, 0));
        Assert.True(RideModel.GripFactor(3000, 1200) < RideModel.GripFactor(3000, 800));
    }

    [Fact]
    public void UnsprungMassComesFromTheFittedParts()
    {
        var db = TestContent.Database;
        var c = Car.Chassis().Config;
        double expected = db.GetPart("tires.street_205_55r16").MassKg / 2 + RideModel.HubMassKg
                          + db.GetPart("brakes.oem").MassKg / 4 + RideModel.SuspensionUnsprungShare * db.GetPart("suspension.oem").MassKg / 4;
        Assert.Equal(expected, c.Ride.UnsprungMass(0), 9);
        Assert.True(Car.Chassis(("brakes", "brakes.big_kit")).Config.Ride.UnsprungMass(0) > c.Ride.UnsprungMass(0), "big brakes add unsprung mass");
    }

    [Fact]
    public void LoweringEatsBumpTravelAndBottomingCostsGrip()
    {
        var stock = Car.Chassis().Config;
        var track = Car.Chassis(("suspension", "suspension.coilover_track")).Config;
        Assert.Equal(0.075, stock.Ride.BumpTravel(0), 9);
        Assert.Equal(0.075 - 0.040, track.Ride.BumpTravel(0), 9);
        var r = track.Ride;
        Assert.True(r.BottomingProbability(0, 0.010, 30, Surface.Kerb) < r.BottomingProbability(0, 0.030, 30, Surface.Kerb));
        Assert.True(r.BottomingProbability(0, 0.0, 30, Surface.Asphalt) < 1e-6, "no bottoming at rest height on smooth asphalt");
        Assert.True(r.TyreLoadSigma(0, 30, Surface.Kerb, 1.0) > 2 * r.TyreLoadSigma(0, 30, Surface.Kerb, 0.0));
    }

    private static VehicleSimulation TrackCoilovers(params (string field, double value)[] settings)
    {
        var db = TestContent.Database;
        var def = db.GetVehicle("kestrel_s2");
        var factory = new PartInstanceFactory(13_000_000);
        var chassis = VehicleAssembly.CreateStock(def, db, factory);
        var part = factory.Create(db.GetPart("suspension.coilover_track"));
        foreach (var (field, value) in settings) Assert.True(part.Adjust(field, value));
        Assert.True(chassis.Remove(def.SlotFor(PartCategory.Suspension), out _).Ok);
        Assert.True(chassis.Install(def.SlotFor(PartCategory.Suspension), part).Ok);
        var engine = SimFactory.Assembly();
        var sim = new VehicleSimulation(new VehicleConfiguration(def, chassis, engine, db), SimFactory.Create(engine));
        sim.StartIdling();
        // A bumpy skidpad: kerb-grade roughness under every wheel (no track, so nothing overwrites it).
        for (int w = 0; w < 4; w++) sim.WheelSurface[w] = Surface.Kerb;
        return sim;
    }

    [Fact]
    public void OnABumpySurfaceTheBestSetUpIsNotAtAnEndStop()
    {
        double Lateral(params (string, double)[] s) => Car.MaxLateralG(TrackCoilovers(s));
        // Ride height: lower is better until the suspension starts landing on its bump stops.
        double low = Lateral(("ride_height_offset_mm", -60)), mid = Lateral(("ride_height_offset_mm", -50)), high = Lateral(("ride_height_offset_mm", -20));
        Assert.True(mid > low * 1.01 && mid > high, $"-60: {low:F4} g, -50: {mid:F4} g, -20: {high:F4} g");
        // Damping: an optimum between the adjuster's end stops.
        double Damped(double c) => Lateral(("front_damper_ns_m", c), ("rear_damper_ns_m", c - 200));
        double soft = Damped(3000), middle = Damped(5000), hard = Damped(6400);
        Assert.True(middle > soft && middle > hard, $"3000: {soft:F4} g, 5000: {middle:F4} g, 6400: {hard:F4} g");
    }
}
