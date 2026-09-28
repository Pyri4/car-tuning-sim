using CarSim.Core.Content;

namespace CarSim.Core.Engines;

/// <summary>
/// A real hardware feature an engine may have, and whether the simulator models it. This list is the single statement
/// of which features the physics represents. For a modelled feature, <see cref="Present"/> says whether an assembly's
/// parts provide it (the physics reads the same part data). An unmodelled feature is never simulated: a declared one
/// is reported as not modelled, with the approximation the content says stands in for it.
/// </summary>
public sealed record EngineFeature(string Id, string Description, Func<EngineCapabilities, bool>? Present)
{
    public bool Modelled => Present != null;
}

/// <summary>The vocabulary of <see cref="EngineIdentity.Features"/> (ENGINE_AUTHORING_GUIDE.md, "Declared features").</summary>
public static class EngineFeatures
{
    public static readonly IReadOnlyList<EngineFeature> All = new EngineFeature[]
    {
        // Modelled: present when some installed part provides the hardware.
        new("intake_cam_phasing", "Intake cam phaser (VVT)", c => c.IntakeCamPhasers),
        new("variable_valve_lift_two_stage", "Two-stage variable valve lift (VTEC-type)", c => c.VariableLiftCams),
        new("variable_intake", "Switched intake runners (DISA/VIS-type)", c => c.SwitchedRunners),
        new("turbocharger", "Turbocharging", c => c.Turbochargers > 0),
        new("twin_turbo_parallel", "Two or more turbochargers in parallel", c => c.Turbochargers > 1),
        new("intercooler", "Charge-air cooling", c => c.Intercooled),
        // Not modelled: authoring one needs an approximation note; the physics never pretends to have it.
        new("exhaust_cam_phasing", "Exhaust cam phaser", null),
        new("variable_valve_lift_continuous", "Continuously variable valve lift (Valvetronic-type)", null),
        new("direct_injection", "Direct fuel injection", null),
        new("port_and_direct_injection", "Dual (port + direct) injection", null),
        new("twin_scroll_turbine", "Twin-scroll turbine housing", null),
        new("sequential_turbos", "Sequential turbocharging", null),
        new("variable_geometry_turbine", "Variable-geometry turbine", null),
        new("supercharger", "Crank-driven supercharger", null),
        new("dry_sump", "Dry-sump lubrication", null),
        new("individual_throttles", "Individual throttle bodies", null),
        new("mass_air_flow_metering", "Mass-air-flow (hot-film/hot-wire) fuel metering", null),
        new("returnless_fuel_system", "Returnless fuel system", null),
        new("map_controlled_thermostat", "Map-controlled (electrically heated) thermostat", null),
        new("dual_mass_flywheel", "Dual-mass flywheel torsional isolation", null),
        new("cylinder_deactivation", "Cylinder deactivation", null),
        new("variable_oil_pump", "Variable-displacement oil pump", null),
    };

    private static readonly Dictionary<string, EngineFeature> ById = All.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public static EngineFeature? Find(string id) => ById.GetValueOrDefault(id);
}

/// <summary>A hardware feature an engine's content says the real engine has.</summary>
public sealed record DeclaredFeature
{
    /// <summary>An <see cref="EngineFeatures"/> id.</summary>
    public required string Feature { get; init; }

    /// <summary>For a feature the simulator does not model: what stands in for it, or "omitted". Required then.</summary>
    public string? Approximation { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Who and what an engine definition is: display and provenance metadata. Nothing in the simulation reads it (the
/// source audit enforces that): the physics follows the parts.
/// </summary>
public sealed record EngineIdentity
{
    /// <summary>"real" (a real engine, possibly under a fictional marque), "fictional" or "synthetic" (test content).</summary>
    public required string Kind { get; init; }

    public string? Manufacturer { get; init; }

    /// <summary>The family a variant belongs to ("M54"); for display and grouping, never for behaviour.</summary>
    public string? Family { get; init; }

    /// <summary>The variant code ("M54B30").</summary>
    public string? Variant { get; init; }

    public string? Years { get; init; }
    public string? Market { get; init; }

    /// <summary>For a real engine: the source id of its reference (a <see cref="SourceDefinition"/>).</summary>
    public string? Reference { get; init; }

    /// <summary>Hardware features of the real engine, modelled or not.</summary>
    public IReadOnlyList<DeclaredFeature> Features { get; init; } = Array.Empty<DeclaredFeature>();

    public static readonly IReadOnlyList<string> Kinds = new[] { "real", "fictional", "synthetic" };

    public IEnumerable<string> Validate(Func<string, bool> sourceExists)
    {
        if (!Kinds.Contains(Kind)) yield return $"identity: kind must be one of {string.Join(", ", Kinds)} (was '{Kind}').";
        if (!string.IsNullOrWhiteSpace(Reference) && !sourceExists(Reference))
            yield return $"identity: unknown reference source '{Reference}'.";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Features)
        {
            var feature = EngineFeatures.Find(d.Feature);
            if (feature == null)
            {
                yield return $"identity: unknown feature '{d.Feature}'. Known: {string.Join(", ", EngineFeatures.All.Select(f => f.Id))}.";
                continue;
            }
            if (!seen.Add(d.Feature)) yield return $"identity: feature '{d.Feature}' is declared twice.";
            if (!feature.Modelled && string.IsNullOrWhiteSpace(d.Approximation))
                yield return $"identity: feature '{d.Feature}' is not modelled; say what stands in for it ('approximation', or \"omitted\").";
        }
    }
}

/// <summary>How a declared feature stands in a built engine.</summary>
public enum FeatureStatus
{
    /// <summary>Modelled, and the installed parts provide it.</summary>
    Supported,

    /// <summary>The simulator does not model it; <see cref="FeatureReportEntry.Approximation"/> says what stands in.</summary>
    NotModelled,

    /// <summary>Modelled, but no installed part provides it: the content is missing the hardware's data.</summary>
    MissingData,

    /// <summary>Modelled and provided by the parts, but not declared by the identity (information, not an error).</summary>
    Undeclared,
}

public sealed record FeatureReportEntry(string Feature, string Description, FeatureStatus Status, string? Approximation);

/// <summary>Compares an engine's declared features with what its assembly provides.</summary>
public static class FeatureReport
{
    public static IReadOnlyList<FeatureReportEntry> For(EngineIdentity? identity, EngineCapabilities capabilities)
    {
        var entries = new List<FeatureReportEntry>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in identity?.Features ?? Array.Empty<DeclaredFeature>())
        {
            if (EngineFeatures.Find(d.Feature) is not { } f) continue; // rejected at load
            declared.Add(f.Id);
            var status = !f.Modelled ? FeatureStatus.NotModelled
                : f.Present!(capabilities) ? FeatureStatus.Supported : FeatureStatus.MissingData;
            entries.Add(new FeatureReportEntry(f.Id, f.Description, status, d.Approximation));
        }
        foreach (var f in EngineFeatures.All.Where(f => f.Modelled && !declared.Contains(f.Id) && f.Present!(capabilities)))
            entries.Add(new FeatureReportEntry(f.Id, f.Description, FeatureStatus.Undeclared, null));
        return entries;
    }
}
