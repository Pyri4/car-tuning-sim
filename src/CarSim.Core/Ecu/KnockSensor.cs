namespace CarSim.Core.Ecu;

/// <summary>Coarse knock intensity as a knock sensor's signal processing reports it.</summary>
public enum KnockLevel
{
    None,
    Trace,
    Light,
    Moderate,
    Heavy,
}

/// <summary>
/// What a knock sensor tells the tuner (and the dyno log): whether the engine knocks and roughly how hard — not the
/// knock model's degrees of advance past the knock limit, from which one reading would give the limit away
/// (limit = advance − intensity). Knock onset is exact (any autoignition shows as at least <see cref="KnockLevel.Trace"/>),
/// so the limit is found the way tuners find it: sweep timing until it knocks.
/// </summary>
public static class KnockSensor
{
    /// <summary>Upper edges of the Trace, Light and Moderate bands, degrees past the knock limit.</summary>
    public const double TraceBelow = 0.3, LightBelow = 1.0, ModerateBelow = 2.5;

    public static KnockLevel Level(double knockIntensityDeg) => knockIntensityDeg switch
    {
        <= 0.0 => KnockLevel.None,
        < TraceBelow => KnockLevel.Trace,
        < LightBelow => KnockLevel.Light,
        < ModerateBelow => KnockLevel.Moderate,
        _ => KnockLevel.Heavy,
    };

    public static string Describe(KnockLevel level) => level switch
    {
        KnockLevel.None => "quiet",
        KnockLevel.Trace => "trace knock",
        KnockLevel.Light => "light knock",
        KnockLevel.Moderate => "knocking",
        _ => "heavy knock",
    };
}
