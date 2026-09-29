using System.Reflection;
using System.Text.Json.Serialization;

namespace CarSim.Core.Content;

/// <summary>
/// How an authored value was obtained (ENGINE_AUTHORING_GUIDE.md §6). Words, not letters: the letters A–D already name
/// debt classes and model-constant classes. Tune tables are "calibrated", which the tune manifest records; it is not a
/// content provenance type.
/// </summary>
public static class ProvenanceTypes
{
    /// <summary>The maker's specification (spec sheet, press kit, official figures).</summary>
    public const string Published = "published";

    /// <summary>Measured on the real part, or primary documentation (repair or service data, a drawing).</summary>
    public const string Measured = "measured";

    /// <summary>A reputable secondary source (trade press, enthusiast measurements, parts listings).</summary>
    public const string Secondary = "secondary";

    /// <summary>A definitional conversion of a sourced value (advertised → 1 mm duration, lb/h → cc/min).</summary>
    public const string Converted = "converted";

    /// <summary>Computed from other values by a named formula or tool.</summary>
    public const string Derived = "derived";

    /// <summary>An engineering estimate with no source.</summary>
    public const string Estimated = "estimated";

    /// <summary>Chosen so that the model reproduces a reference output. Always names its target.</summary>
    public const string Fitted = "fitted";

    public static readonly IReadOnlyList<string> All = new[] { Published, Measured, Secondary, Converted, Derived, Estimated, Fitted };

    /// <summary>Types that cite a source: the value comes from outside the project.</summary>
    public static bool NeedsSource(string type) => type is Published or Measured or Secondary or Converted;
}

/// <summary>
/// Where one authored value comes from. The value itself stays in the spec (with its unit in the field name); this
/// record sits beside it, in the part's or engine's <c>provenance</c> map, keyed by the field name.
/// </summary>
public sealed record ValueProvenance
{
    /// <summary>One of <see cref="ProvenanceTypes.All"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Id of a <see cref="SourceDefinition"/> (required for published, measured, secondary and converted values).</summary>
    public string? Source { get; init; }

    /// <summary>Derived and converted values: the formula or tool (e.g. "A-D1, carsim derive-stage").</summary>
    public string? Method { get; init; }

    /// <summary>Fitted values: what the value was chosen to reproduce.</summary>
    public string? Target { get; init; }

    /// <summary>Optional: "high", "medium" or "low".</summary>
    public string? Confidence { get; init; }

    public string? Notes { get; init; }

    public static readonly IReadOnlyList<string> Confidences = new[] { "high", "medium", "low" };

    /// <summary>Problems with the record itself; <paramref name="field"/> names it in the messages.</summary>
    public IEnumerable<string> Validate(string field, Func<string, bool> sourceExists)
    {
        if (!ProvenanceTypes.All.Contains(Type))
        {
            yield return $"provenance '{field}': type must be one of {string.Join(", ", ProvenanceTypes.All)} (was '{Type}').";
            yield break;
        }
        if (ProvenanceTypes.NeedsSource(Type) && string.IsNullOrWhiteSpace(Source))
            yield return $"provenance '{field}': a {Type} value needs a 'source'.";
        if (!string.IsNullOrWhiteSpace(Source) && !sourceExists(Source))
            yield return $"provenance '{field}': unknown source '{Source}'.";
        if (Type is ProvenanceTypes.Derived or ProvenanceTypes.Converted && string.IsNullOrWhiteSpace(Method))
            yield return $"provenance '{field}': a {Type} value needs a 'method' (the formula or tool).";
        if (Type == ProvenanceTypes.Fitted && string.IsNullOrWhiteSpace(Target))
            yield return $"provenance '{field}': a fitted value needs a 'target' (what it was chosen to reproduce).";
        if (Type != ProvenanceTypes.Fitted && !string.IsNullOrWhiteSpace(Target))
            yield return $"provenance '{field}': only a fitted value has a 'target'.";
        if (Confidence != null && !Confidences.Contains(Confidence))
            yield return $"provenance '{field}': confidence must be one of {string.Join(", ", Confidences)} (was '{Confidence}').";
    }
}

/// <summary>A cited source (document kind <c>sources</c>). Facts are cited; nothing is copied.</summary>
public sealed record SourceDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>One of <see cref="Types"/>.</summary>
    public required string Type { get; init; }

    public string? Publisher { get; init; }
    public string? Url { get; init; }

    /// <summary>ISO date the source was read, where known.</summary>
    public string? Accessed { get; init; }

    /// <summary>What the project may do with it (facts cited; a licence for anything more).</summary>
    public string? LicenseNote { get; init; }

    public string? Notes { get; init; }

    [JsonIgnore] public string LoadedFrom { get; init; } = "";

    public static readonly IReadOnlyList<string> Types = new[]
    {
        "manufacturer", "service_documentation", "reference_work", "press", "enthusiast_measurement", "retailer",
        "project_document",
    };

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Title)) yield return "Source is missing 'title'.";
        if (!Types.Contains(Type)) yield return $"type must be one of {string.Join(", ", Types)} (was '{Type}').";
    }
}

/// <summary>Field names that a <c>provenance</c> map may key: the authored JSON names of a type's content fields.</summary>
public static class ProvenanceFields
{
    /// <summary>The key that applies to every authored field without its own entry.</summary>
    public const string Default = "*";

    /// <summary>JSON (snake_case) names of the settable, serialized properties of <paramref name="type"/>.</summary>
    public static IReadOnlyList<string> Of(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Select(p => ContentJson.Options.PropertyNamingPolicy!.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Expands a map with a <see cref="Default"/> entry over the fields actually authored: the result has one entry per
    /// authored field that has provenance, and none for fields left to a schema default (those are "defaulted", which a
    /// map cannot claim). Keys are ordered, so the result is deterministic.
    /// </summary>
    public static IReadOnlyDictionary<string, ValueProvenance> Expand(IReadOnlyDictionary<string, ValueProvenance>? map,
        IEnumerable<string> authoredFields)
    {
        var result = new SortedDictionary<string, ValueProvenance>(StringComparer.Ordinal);
        if (map == null) return result;
        map.TryGetValue(Default, out var fallback);
        foreach (var field in authoredFields)
        {
            if (map.TryGetValue(field, out var own)) result[field] = own;
            else if (fallback != null) result[field] = fallback;
        }
        return result;
    }
}
