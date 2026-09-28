namespace CarSim.Core.Parts;

/// <summary>
/// Immutable definition of a purchasable part (loaded from content). Runtime condition lives in
/// <see cref="PartInstance"/>, so one definition is shared by every copy of the part.
/// </summary>
public sealed class PartDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Purchase price of a new part, in game currency.</summary>
    public double Price { get; init; }

    /// <summary>Mass of the part as sold (the whole set for set parts), kg.</summary>
    public double MassKg { get; init; }

    /// <summary>Interface keys this part offers to others (e.g. <c>turbo_flange.t3</c>).</summary>
    public IReadOnlyList<string> Provides { get; init; } = Array.Empty<string>();

    /// <summary>Interface keys that some other installed part must provide.</summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

    /// <summary>Free-form tags for filtering/search (not used for compatibility).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Content file the definition was loaded from (diagnostics only).</summary>
    public string Source { get; init; } = "";

    public required PartSpec Spec { get; init; }

    /// <summary>Spec fields the owner may adjust on this part, with their ranges (setup settings).</summary>
    public IReadOnlyList<PartAdjustment> Adjustments { get; init; } = Array.Empty<PartAdjustment>();

    /// <summary>The part this one is a variant of (<c>extends</c>), or null. Resolved at load; informational.</summary>
    public string? Extends { get; init; }

    /// <summary>
    /// Spec fields the content states (after <c>extends</c> resolution); every other spec field takes its schema default.
    /// Authored vs defaulted is a provenance fact: a default is nobody's claim about the part.
    /// </summary>
    public IReadOnlyList<string> AuthoredSpecFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Provenance of the authored values, keyed by spec field name (and <c>mass_kg</c>), expanded over the authored
    /// fields. Empty when the content records none (fictional and synthetic parts need none).
    /// </summary>
    public IReadOnlyDictionary<string, Content.ValueProvenance> Provenance { get; init; } =
        new Dictionary<string, Content.ValueProvenance>();

    public PartAdjustment? FindAdjustment(string field) => Adjustments.FirstOrDefault(a => a.Field == field);

    public T GetSpec<T>() where T : PartSpec =>
        Spec as T ?? throw new InvalidOperationException(
            $"Part '{Id}' (category '{Category}') has spec {Spec.GetType().Name}, not {typeof(T).Name}.");

    public override string ToString() => $"{Name} [{Id}]";
}
