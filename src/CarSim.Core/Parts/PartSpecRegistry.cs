using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Parts;

/// <summary>Maps each part category to the spec type its content must deserialize into.</summary>
public static class PartSpecRegistry
{
    private static readonly Dictionary<string, Type> SpecTypes = new(StringComparer.Ordinal)
    {
        [PartCategory.Block] = typeof(BlockSpec),
        [PartCategory.MainBearings] = typeof(BearingSpec),
        [PartCategory.Crankshaft] = typeof(CrankshaftSpec),
        [PartCategory.RodBearings] = typeof(BearingSpec),
        [PartCategory.ConnectingRods] = typeof(ConnectingRodSpec),
        [PartCategory.Pistons] = typeof(PistonSpec),
        [PartCategory.HeadGasket] = typeof(HeadGasketSpec),
        [PartCategory.CylinderHead] = typeof(CylinderHeadSpec),
        [PartCategory.ValveSprings] = typeof(ValveSpringSpec),
        [PartCategory.Camshafts] = typeof(CamshaftSpec),
        [PartCategory.IntakeManifold] = typeof(IntakeManifoldSpec),
        [PartCategory.ThrottleBody] = typeof(ThrottleBodySpec),
        [PartCategory.Injectors] = typeof(InjectorSpec),
        [PartCategory.FuelPump] = typeof(FuelPumpSpec),
        [PartCategory.ExhaustManifold] = typeof(ExhaustManifoldSpec),
        [PartCategory.Exhaust] = typeof(ExhaustSpec),
        [PartCategory.OilPump] = typeof(OilPumpSpec),
        [PartCategory.OilPan] = typeof(OilPanSpec),
        [PartCategory.Radiator] = typeof(RadiatorSpec),
        [PartCategory.Flywheel] = typeof(FlywheelSpec),
        [PartCategory.Ecu] = typeof(EcuSpec),
    };

    public static bool TryGetSpecType(string category, out Type specType) =>
        SpecTypes.TryGetValue(category, out specType!);

    public static bool IsKnownCategory(string category) => SpecTypes.ContainsKey(category);

    public static IEnumerable<string> Categories => SpecTypes.Keys;
}
