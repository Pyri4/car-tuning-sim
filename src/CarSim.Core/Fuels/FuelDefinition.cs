using System.Text.Json.Serialization;

namespace CarSim.Core.Fuels;

/// <summary>Fuel properties that drive fueling demand, energy release and knock resistance.</summary>
public sealed class FuelDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Research octane number. Higher resists knock.</summary>
    public required double OctaneRon { get; init; }

    /// <summary>Mass air/fuel ratio for complete combustion (λ = 1).</summary>
    public required double StoichiometricAfr { get; init; }

    public required double LowerHeatingValueMjKg { get; init; }
    public required double DensityKgL { get; init; }

    /// <summary>
    /// Evaporative charge-cooling strength relative to gasoline (1.0). Alcohol fuels cool the
    /// charge much more, which lowers charge temperature and knock tendency.
    /// </summary>
    public double ChargeCoolingFactor { get; init; } = 1.0;

    public string Description { get; init; } = "";

    /// <summary>J/kg.</summary>
    [JsonIgnore] public double LowerHeatingValue => LowerHeatingValueMjKg * 1e6;

    /// <summary>kg/m³.</summary>
    [JsonIgnore] public double Density => DensityKgL * 1000.0;

    /// <summary>Latent heat of vaporisation of gasoline, J/kg (the reference for <see cref="ChargeCoolingFactor"/>).</summary>
    public const double GasolineLatentHeat = 350e3;

    /// <summary>Stoichiometric AFR of the reference gasoline.</summary>
    public const double GasolineStoichiometricAfr = 14.7;

    /// <summary>
    /// Latent heat of vaporisation, J/kg of fuel. <see cref="ChargeCoolingFactor"/> is the cooling per kilogram
    /// of stoichiometric charge relative to gasoline, so per kilogram of fuel it scales with the fuel's AFR
    /// (E85: 3.5 × 350 kJ/kg × 9.8/14.7 ≈ 820 kJ/kg, close to the measured value).
    /// </summary>
    [JsonIgnore] public double LatentHeat => GasolineLatentHeat * ChargeCoolingFactor * StoichiometricAfr / GasolineStoichiometricAfr;

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (!(OctaneRon >= 60 && OctaneRon <= 130)) p.Add($"octane_ron out of range: {OctaneRon}");
        if (!(StoichiometricAfr >= 3 && StoichiometricAfr <= 20)) p.Add($"stoichiometric_afr out of range: {StoichiometricAfr}");
        if (!(LowerHeatingValueMjKg >= 10 && LowerHeatingValueMjKg <= 60)) p.Add($"lower_heating_value_mj_kg out of range: {LowerHeatingValueMjKg}");
        if (!(DensityKgL >= 0.5 && DensityKgL <= 1.2)) p.Add($"density_kg_l out of range: {DensityKgL}");
        if (!(ChargeCoolingFactor >= 0 && ChargeCoolingFactor <= 10)) p.Add($"charge_cooling_factor out of range: {ChargeCoolingFactor}");
        return p;
    }

    public override string ToString() => $"{Name} [{Id}]";
}
