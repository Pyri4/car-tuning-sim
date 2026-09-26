namespace CarSim.Core.Content;

/// <summary>
/// A new-game starting point: the engine (factory build), how worn and fatigued each part is, the
/// money available, and the fuel and tune in use. Wear and fatigue are what the player discovers on
/// inspection.
/// </summary>
public sealed class ScenarioDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string Engine { get; init; }

    /// <summary>The car the engine sits in (vehicle id); empty for an engine-only scenario.</summary>
    public string Vehicle { get; init; } = "";
    public double Money { get; init; }
    public string Fuel { get; init; } = "gasoline_95";

    /// <summary>Tune id; empty uses the engine's stock tune.</summary>
    public string Tune { get; init; } = "";

    /// <summary>Slot id (engine or chassis) → part wear (0–1).</summary>
    public IReadOnlyDictionary<string, double> Wear { get; init; } = new Dictionary<string, double>();

    /// <summary>Slot id → (failure mode name → fatigue 0–1).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Fatigue { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    /// <summary>Part ids placed on the shelf at the start.</summary>
    public IReadOnlyList<string> Inventory { get; init; } = Array.Empty<string>();

    public string Source { get; init; } = "";
}
