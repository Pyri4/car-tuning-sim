using CarSim.Core.Common;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// The ECU fuels from its VE table, MAP and intake air temperature — never from the engine's true airflow
/// (the original ECU read the simulation's air mass directly, so every breathing mod was fuelled perfectly).
/// </summary>
public class SpeedDensityTests
{
    private static double LambdaError(EngineTelemetry t) => t.Lambda / t.TargetLambda - 1.0;

    private static CarSim.Core.Parts.Specs.EcuSpec Ecu(double mapSensorKpa) => new()
    {
        MapSensorMaxKpa = mapSensorKpa, BoostControl = false, KnockControl = true, MaxRevLimitRpm = 8000,
    };

    [Theory]
    [InlineData(1500, 0.3)]
    [InlineData(3000, 0.1)]
    [InlineData(3000, 1.0)]
    [InlineData(4000, 0.3)]
    [InlineData(6000, 0.1)]
    [InlineData(6000, 1.0)]
    [InlineData(7400, 0.3)]
    [InlineData(7400, 1.0)]
    public void TheShippedTableFuelsTheStockEngineOnTarget(double rpm, double throttle)
    {
        // If this fails after a physics change, the content table is stale: re-run `carsim calibrate-ve`.
        var t = SimFactory.At(SimFactory.Create(), rpm, throttle);
        Assert.InRange(LambdaError(t), -0.02, 0.02);
    }

    [Fact]
    public void TheAirEstimateUsesOnlyTheSensorsAndTheCalibration()
    {
        var tune = SimFactory.StockTune();
        var ecu = new EcuController(Ecu(250), tune);
        double map = 80_000, iat = 310, rpm = 4000;
        double expected = tune.VolumetricEfficiencyAt(rpm, 80) * map * Units.CcToM3(tune.DisplacementCc) / 4
                          / (PhysicalConstants.AirGasConstant * iat);
        Assert.Equal(expected, ecu.EstimatedAirPerCycle(rpm, map, iat, 4), 15);
        // Hotter air at the sensor: proportionally less fuel.
        Assert.Equal(expected * 310 / 340, ecu.EstimatedAirPerCycle(rpm, map, 340, 4), 15);
        // A MAP sensor pegged at its range under-reads boost, so the estimate stops rising.
        var stockEcu = new EcuController(Ecu(105), tune);
        Assert.Equal(stockEcu.EstimatedAirPerCycle(rpm, 105_000, iat, 4), stockEcu.EstimatedAirPerCycle(rpm, 160_000, iat, 4), 15);
    }

    [Fact]
    public void BreathingModsMoveTheMixtureUntilTheTableIsRetuned()
    {
        // Overlap costs low-rpm VE (rich on the stock table) and gains top-end VE (lean).
        var cams = SimFactory.Create(("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race"));
        Assert.True(LambdaError(SimFactory.At(cams, 2500)) < -0.10, "race cams run rich low down");
        Assert.True(LambdaError(SimFactory.At(cams, 7400)) > 0.03, "and lean at the top");
        Assert.True(LambdaError(SimFactory.At(SimFactory.Create(("exhaust_manifold", "k20.exhaust_manifold.header421")), 6500)) > 0.025,
            "a header's scavenging leans out the top end");
        Assert.True(LambdaError(SimFactory.At(SimFactory.Create(("cylinder_head", "k20.head.ported")), 7400)) > 0.02,
            "so does a ported head");
        // A bigger throttle does not change what the cylinder breathes at WOT.
        Assert.InRange(LambdaError(SimFactory.At(SimFactory.Create(("throttle_body", "throttle.70mm")), 6500)), -0.01, 0.01);
    }

    [Fact]
    public void AStrokerRunsLeanUntilTheEcuKnowsItsDisplacement()
    {
        var assembly = SimFactory.Assembly(("crankshaft", "k20.crankshaft.stroker90"), ("pistons", "k20.pistons.stroker_lc"));
        var lean = SimFactory.At(SimFactory.Create(assembly), 2500);
        Assert.True(LambdaError(lean) > 0.04, $"λ error {LambdaError(lean):P1}");

        var tune = SimFactory.StockTune();
        tune.DisplacementCc = Units.M3ToCc(SimFactory.Create(assembly).Config.Geometry.Displacement);
        foreach (double rpm in new[] { 2500.0, 4500, 6500 })
            Assert.InRange(LambdaError(SimFactory.At(SimFactory.Create(assembly, tune: tune), rpm)), -0.03, 0.03);
    }

    [Fact]
    public void TheAirTemperatureSensorCoversMostOfAMissingIntercooler()
    {
        // The turbo table was calibrated with the intercooler. Without it the charge is ~35 K hotter (≈ 11 %
        // less dense), but the ECU sees that at its IAT sensor; only the smaller second-order effects remain.
        var cooled = SimFactory.At(TurboTests.TurboSim(TurboTests.TurboBuild()), 5500, 1.0, 4.0);
        var hot = SimFactory.At(TurboTests.TurboSim(TurboTests.TurboBuild(intercooler: null)), 5500, 1.0, 4.0);
        double densityChange = hot.ManifoldTemperature / cooled.ManifoldTemperature - 1.0;
        Assert.True(densityChange > 0.08, $"IAT {cooled.ManifoldTemperature - 273.15:F0} → {hot.ManifoldTemperature - 273.15:F0} °C");
        Assert.InRange(LambdaError(cooled), -0.015, 0.015);
        Assert.True(Math.Abs(LambdaError(hot)) < 0.4 * densityChange, $"λ error {LambdaError(hot):P1}");
    }

    [Fact]
    public void RecalibratingTheTableBringsABuiltEngineBackOnTarget()
    {
        var assembly = SimFactory.Assembly(
            ("cylinder_head", "k20.head.ported"), ("valve_springs", "k20.valve_springs.performance"),
            ("camshafts", "k20.cams.race"), ("intake_manifold", "k20.intake.short_runner"),
            ("exhaust_manifold", "k20.exhaust_manifold.header421"), ("exhaust", "exhaust.race_76mm"));
        var sim = SimFactory.Create(assembly);
        Assert.True(LambdaError(SimFactory.At(sim, 7400)) > 0.08, "the stock table is well off on a built engine");

        var tune = sim.Ecu.Tune.Clone();
        var table = VeCalibrator.Calibrate(sim);
        for (int r = 0; r < table.Length; r++)
            for (int c = 0; c < table[r].Length; c++) tune.VolumetricEfficiency[r, c] = table[r][c];
        var calibrated = SimFactory.Create(assembly, tune: tune);
        foreach (double rpm in new[] { 2500.0, 4500, 6500, 7400 })
            foreach (double throttle in new[] { 0.2, 1.0 })
                Assert.InRange(LambdaError(SimFactory.At(calibrated, rpm, throttle)), -0.02, 0.02);
        // Columns above the rev limit repeat the last one inside it (the engine never runs there).
        int last = tune.VolumetricEfficiency.XAxis.ToList().FindLastIndex(x => x <= tune.RevLimitRpm);
        for (int c = last + 1; c < table[0].Length; c++)
            Assert.Equal(table[^1][last], table[^1][c]);
    }

    [Fact]
    public void PartLoadBreathingFallsSmoothlyWithoutAFloor()
    {
        // The residual factor used to be 1 − x clamped at 0.4: with long-overlap cams at part load VE sat flat
        // on the clamp and then jumped (0.39 → 0.60 between 31 and 45 kPa), which no fuel map could follow.
        var config = SimFactory.Create(("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race")).Config;
        var air = new AirPath(config);
        double previous = double.PositiveInfinity;
        for (double port = 101_000; port >= 5_000; port -= 1_000)
        {
            double f = air.ResidualFactor(103_000, port);
            Assert.True(f > 0 && f < previous, $"port {port / 1000:F0} kPa: f {f:F4} after {previous:F4}");
            previous = f;
        }
        // Small displacements are unchanged from the linear model: 1/(1 + x) ≈ 1 − x.
        var g = config.Geometry;
        double c = g.ClearanceVolume / (g.ClearanceVolume + g.SweptVolumePerCylinder);
        double linear = 1 - c * (Math.Pow(1.2, 1 / PhysicalConstants.CompressionPolytropicExponent) - 1);
        Assert.Equal(linear, air.ResidualFactor(120_000, 100_000), 3);
    }

    [Fact]
    public void TunesWithoutAFuelMapAreRejected()
    {
        var d = SimFactory.StockTune().ToDocument();
        CarSim.Core.Content.TuneDocument Copy(double[][]? ve, double? cc) => new()
        {
            Id = d.Id, Name = d.Name, RpmAxis = d.RpmAxis, LoadAxisKpa = d.LoadAxisKpa, TargetLambda = d.TargetLambda,
            IgnitionAdvanceDeg = d.IgnitionAdvanceDeg, VolumetricEfficiency = ve, DisplacementCc = cc, RevLimitRpm = d.RevLimitRpm,
            InjectorFlowCcMin = d.InjectorFlowCcMin, FuelStoichAfr = d.FuelStoichAfr,
        };
        Assert.Empty(Copy(d.VolumetricEfficiency, d.DisplacementCc).Validate());
        Assert.Contains(Copy(null, d.DisplacementCc).Validate(), p => p.Contains("volumetric_efficiency missing"));
        Assert.Contains(Copy(d.VolumetricEfficiency, null).Validate(), p => p.Contains("displacement_cc missing"));
        var zeroed = d.VolumetricEfficiency!.Select(r => r.Select(_ => 0.0).ToArray()).ToArray();
        Assert.Contains(Copy(zeroed, d.DisplacementCc).Validate(), p => p.Contains("volumetric_efficiency"));
        Assert.Throws<InvalidDataException>(() => EcuTune.FromDocument(Copy(null, d.DisplacementCc)));
    }

    [Fact]
    public void CalibrationLeavesTheEngineAndItsTuneAsTheyWere()
    {
        var sim = SimFactory.Create();
        var tune = sim.Ecu.Tune;
        double before = tune.VolumetricEfficiency[3, 5];
        VeCalibrator.Calibrate(sim, holdSeconds: 0.05, passes: 1);
        Assert.Same(tune, sim.Ecu.Tune);
        Assert.Equal(before, tune.VolumetricEfficiency[3, 5]);
        Assert.True(sim.DamageEnabled);
    }
}
