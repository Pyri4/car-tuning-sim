using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CarSim.Core.Parts;

namespace CarSim.Verification.Fingerprint;

/// <summary>
/// Turns a telemetry record (or any value object) into an ordered list of <c>path = value</c> pairs: every public
/// property and field, recursively, in ordinal name order; list elements indexed (<c>Banks[1].Lambda</c>). Doubles are written in
/// round-trip form ("R"), so two equal strings are two equal bit patterns and nothing is lost to formatting. A channel is
/// a path with its indices removed (<c>Banks[].Lambda</c>): the same quantity across samples and banks.
/// </summary>
public static partial class TelemetryFlattener
{
    private const int MaxDepth = 6;
    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    private sealed record Member(string Name, Func<object, object?> Get);

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Appends <paramref name="value"/>'s leaves under <paramref name="prefix"/> to <paramref name="into"/>.</summary>
    public static void Flatten(object? value, string prefix, List<KeyValuePair<string, string>> into) => Visit(value, prefix, into, 0);

    /// <summary>The channel of a path: indices removed.</summary>
    public static string Channel(string path) => Indices().Replace(path, "[]");

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Indices();

    private static void Visit(object? value, string path, List<KeyValuePair<string, string>> into, int depth)
    {
        switch (value)
        {
            case null: Add(into, path, "null"); return;
            case double d: Add(into, path, Format(d)); return;
            case float f: Add(into, path, f.ToString("R", CultureInfo.InvariantCulture)); return;
            case string s: Add(into, path, Escape(s)); return;
            case bool b: Add(into, path, b ? "true" : "false"); return;
            case Enum e: Add(into, path, e.ToString()); return;
            case PartDefinition part: Add(into, path, "part:" + part.Id); return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type == typeof(decimal))
        {
            Add(into, path, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            return;
        }
        if (depth >= MaxDepth) throw new InvalidOperationException($"Telemetry nests deeper than {MaxDepth} levels at '{path}'.");
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys.Cast<object>().Select(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? "")
                         .OrderBy(k => k, StringComparer.Ordinal))
                Visit(dictionary[key], $"{path}[{key}]", into, depth + 1);
            return;
        }
        if (value is IEnumerable sequence)
        {
            int i = 0;
            foreach (var item in sequence) Visit(item, $"{path}[{i++}]", into, depth + 1);
            Add(into, path + ".count", i.ToString(CultureInfo.InvariantCulture));
            return;
        }
        foreach (var m in Members.GetOrAdd(type, Discover))
            Visit(m.Get(value), path.Length == 0 ? m.Name : path + "." + m.Name, into, depth + 1);
    }

    /// <summary>Public instance properties (non-indexed) and fields, in ordinal name order.</summary>
    private static Member[] Discover(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .Select(p => new Member(p.Name, p.GetValue))
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => new Member(f.Name, f.GetValue)))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

    private static void Add(List<KeyValuePair<string, string>> into, string path, string value) => into.Add(new(path, value));

    private static string Escape(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else b.Append(c);
        }
        return b.ToString();
    }
}
