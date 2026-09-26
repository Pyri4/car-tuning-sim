using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CarSim.Core.Content;

namespace CarSim.Core.Parts;

/// <summary>
/// A spec value the owner can change on a part they have (a setup setting: tyre pressure, camber,
/// damper clicks, brake bias, LSD preload, ...). <see cref="Field"/> is the spec's own snake_case field,
/// so an adjustment changes exactly what the simulation already reads; the part's authored value is
/// the default.
/// </summary>
public sealed record PartAdjustment(string Field, string Label, double Min, double Max, double Step, double Default)
{
    /// <summary>Clamps to the range and snaps to the step grid (anchored at <see cref="Min"/>).</summary>
    public double Snap(double value)
    {
        if (double.IsNaN(value)) return Default;
        double v = Math.Clamp(value, Min, Max);
        if (Step > 0) v = Math.Min(Max, Min + Math.Round((v - Min) / Step) * Step);
        return Math.Round(v, 6);
    }
}

/// <summary>Reads and overrides authored spec fields by their content (snake_case) names.</summary>
public static class SpecAdjuster
{
    /// <summary>The authored numeric value of <paramref name="field"/>, or null if the spec has no such numeric field.</summary>
    public static double? Read(PartSpec spec, string field)
    {
        var p = Property(spec.GetType(), field);
        return p?.GetValue(spec) is double d ? d : null;
    }

    /// <summary>
    /// A copy of <paramref name="spec"/> with <paramref name="values"/> replacing authored fields. Goes
    /// through the content JSON options, so the copy is built exactly as if it had been authored that way.
    /// </summary>
    public static PartSpec With(PartSpec spec, IReadOnlyDictionary<string, double> values)
    {
        var node = JsonSerializer.SerializeToNode(spec, spec.GetType(), ContentJson.Options)!.AsObject();
        foreach (var (field, value) in values)
        {
            if (Property(spec.GetType(), field) == null) throw new ArgumentException($"{spec.GetType().Name} has no numeric field '{field}'.");
            node[field] = value;
        }
        return (PartSpec)node.Deserialize(spec.GetType(), ContentJson.Options)!;
    }

    private static PropertyInfo? Property(Type type, string field) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p =>
            p.PropertyType == typeof(double) && p.SetMethod != null && p.GetCustomAttribute<JsonIgnoreAttribute>() == null
            && JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name) == field);
}
