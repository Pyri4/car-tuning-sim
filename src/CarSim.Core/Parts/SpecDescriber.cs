using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CarSim.Core.Parts;

/// <summary>
/// Human-readable listing of a spec's authored values ("BoreMm" → "Bore: 86.0 mm"), for UIs and tools.
/// Driven by reflection over the authoring properties, so new spec fields appear automatically.
/// </summary>
public static class SpecDescriber
{
    private static readonly (string Suffix, string Unit)[] UnitSuffixes =
    {
        ("KgCm2", "kg·cm²"), ("KgM2", "kg·m²"), ("WPerK", "W/K"), ("CcPerRev", "cc/rev"), ("CcMin", "cc/min"), ("KgS", "kg/s"),
        ("Cm2", "cm²"), ("Cfm", "CFM"), ("Lph", "L/h"), ("Kpa", "kPa"), ("Bar", "bar"), ("Rpm", "rpm"), ("Deg", "°"),
        ("Mm", "mm"), ("Cc", "cc"), ("Kn", "kN"), ("Nm", "N·m"), ("Ms", "m/s"), ("Kg", "kg"), ("C", "°C"), ("G", "g"),
        ("N", "N"), ("L", "L"),
    };

    public static IReadOnlyList<(string Label, string Value)> Describe(PartSpec spec)
    {
        var rows = new List<(string, string)>();
        foreach (var p in spec.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null || p.SetMethod == null) continue;
            object? value = p.GetValue(spec);
            if (value is IReadOnlyList<double[]> curve)
            {
                rows.Add((Label(p.Name, out _), string.Join("  ", curve.Select(pt => $"{pt[0]:0.#}→{pt[1]:0}"))));
                continue;
            }
            string label = Label(p.Name, out string unit);
            string text = value switch
            {
                double d => Format(d) + (unit.Length > 0 ? (unit.StartsWith('°') ? unit : " " + unit) : ""),
                int i => i.ToString(CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : ""),
                bool b => b ? "yes" : "no",
                null => "—",
                _ => value.ToString() ?? "",
            };
            rows.Add((label, text));
        }
        return rows;
    }

    private static string Format(double d) =>
        Math.Abs(d) >= 1000 ? d.ToString("N0", CultureInfo.InvariantCulture)
        : Math.Abs(d - Math.Round(d)) < 1e-9 ? d.ToString("0", CultureInfo.InvariantCulture)
        : Math.Abs(d) < 1 ? d.ToString("0.###", CultureInfo.InvariantCulture)
        : d.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Label(string name, out string unit)
    {
        unit = "";
        foreach (var (suffix, u) in UnitSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal) && char.IsLower(name[^(suffix.Length + 1)]))
            {
                unit = u;
                name = name[..^suffix.Length];
                break;
            }
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
            sb.Append(i == 0 ? name[i] : char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
