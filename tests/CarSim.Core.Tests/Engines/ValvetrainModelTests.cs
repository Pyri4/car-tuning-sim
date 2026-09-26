using CarSim.Core.Engines;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Tests.Engines;

public class ValvetrainModelTests
{
    private static CamshaftSpec Cams(string id) => TestContent.Database.GetPart(id).GetSpec<CamshaftSpec>();
    private static ValveSpringSpec Springs(string id) => TestContent.Database.GetPart(id).GetSpec<ValveSpringSpec>();
    private static CylinderHeadSpec Head(string id) => TestContent.Database.GetPart(id).GetSpec<CylinderHeadSpec>();

    [Fact]
    public void StockValvetrainFloatsAboveFactoryLimit()
    {
        double f = ValvetrainModel.FloatRpm(Cams("k20.cams.oem"), Springs("k20.valve_springs.oem"), Head("k20.head.oem"));
        Assert.InRange(f, 7700, 8400);
    }

    [Fact]
    public void StifferSpringsRaiseFloatSpeed()
    {
        var cams = Cams("k20.cams.oem");
        var head = Head("k20.head.oem");
        Assert.True(ValvetrainModel.FloatRpm(cams, Springs("k20.valve_springs.performance"), head) >
                    ValvetrainModel.FloatRpm(cams, Springs("k20.valve_springs.oem"), head));
    }

    [Fact]
    public void FloatSpeedScalesWithSqrtOfSpringForceOverMass()
    {
        double baseRpm = ValvetrainModel.FloatRpm(230, 0.010, 500, 0.090);
        Assert.Equal(2.0, ValvetrainModel.FloatRpm(230, 0.010, 2000, 0.090) / baseRpm, 9);
        Assert.Equal(0.5, ValvetrainModel.FloatRpm(230, 0.010, 500, 0.360) / baseRpm, 9);
        Assert.True(ValvetrainModel.FloatRpm(230, 0.012, 500, 0.090) < baseRpm, "more lift → more deceleration → lower float speed");
    }

    [Fact]
    public void WornSpringsFloatEarlier()
    {
        var cams = Cams("k20.cams.oem");
        var springs = Springs("k20.valve_springs.oem");
        var head = Head("k20.head.oem");
        Assert.True(ValvetrainModel.FloatRpm(cams, springs, head, springWear: 1.0) < ValvetrainModel.FloatRpm(cams, springs, head));
    }

    [Fact]
    public void LighterValvesFloatLater()
    {
        var cams = Cams("k20.cams.oem");
        var springs = Springs("k20.valve_springs.oem");
        Assert.True(ValvetrainModel.FloatRpm(cams, springs, Head("k20.head.ported")) > ValvetrainModel.FloatRpm(cams, springs, Head("k20.head.oem")));
    }
}
