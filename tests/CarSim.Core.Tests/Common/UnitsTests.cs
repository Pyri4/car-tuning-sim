using CarSim.Core.Common;

namespace CarSim.Core.Tests.Common;

public class UnitsTests
{
    [Fact]
    public void RpmRoundTrip()
    {
        Assert.Equal(6000.0, Units.RadPerSecToRpm(Units.RpmToRadPerSec(6000.0)), 9);
        Assert.Equal(2.0 * Math.PI * 100.0, Units.RpmToRadPerSec(6000.0), 9);
    }

    [Fact]
    public void HorsepowerDefinition()
    {
        // 1 hp = 550 ft·lbf/s = 745.7 W; 5252 rpm is where hp equals lb-ft.
        double torqueNm = 300.0;
        double powerW = torqueNm * Units.RpmToRadPerSec(5252.113);
        Assert.Equal(Units.NmToLbFt(torqueNm), Units.WToHp(powerW), 3);
    }

    [Fact]
    public void FlowBenchPressureIsTwentyEightInchesOfWater()
    {
        Assert.Equal(6974.49, PhysicalConstants.FlowBenchPressureDrop, 1);
    }

    [Fact]
    public void InjectorAndPumpFlowConversions()
    {
        Assert.Equal(1e-6 / 60.0 * 550.0, Units.CcPerMinToM3PerSec(550.0), 15);
        Assert.Equal(255.0, Units.M3PerSecToLitresPerHour(Units.LitresPerHourToM3PerSec(255.0)), 9);
    }

    [Fact]
    public void TemperatureAndPressure()
    {
        Assert.Equal(373.15, Units.CToK(100.0), 9);
        Assert.Equal(1.0, Units.PaToBar(100_000.0), 12);
        Assert.Equal(14.5038, Units.PaToPsi(Units.BarToPa(1.0)), 3);
    }
}
