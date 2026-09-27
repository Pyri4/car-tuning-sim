using System.Security.Cryptography;
using System.Text;

namespace CarSim.Verification.Fingerprint;

/// <summary>What one case of the matrix produced: a digest per section, readable key numbers and the channels seen.</summary>
public sealed class CaseRecord
{
    public CaseRecord(string caseId) => CaseId = caseId;

    public string CaseId { get; }
    public List<SectionRecord> Sections { get; } = new();

    /// <summary>Every channel (path with indices removed) any section of this case produced.</summary>
    public SortedSet<string> Channels { get; } = new(StringComparer.Ordinal);
}

public sealed class SectionRecord
{
    public SectionRecord(string name) => Name = name;

    public string Name { get; }

    /// <summary>SHA-256 over every recorded value of the section's channels that the schema holds, in order.</summary>
    public string Digest { get; set; } = "";

    public int Samples { get; set; }

    /// <summary>Distinct schema channels the section recorded.</summary>
    public int ChannelCount { get; set; }

    /// <summary>Readable key numbers (peak power, lap times…), also part of the digest; full precision.</summary>
    public List<KeyValuePair<string, string>> Values { get; } = new();
}

/// <summary>
/// Records one case. Every sample is flattened to full-precision <c>path = value</c> pairs
/// (<see cref="TelemetryFlattener"/>) and folded into its section's SHA-256; only channels in the schema are digested
/// (a channel added by later code is reported, not a difference). With a dump writer attached, every pair is also written
/// out, for <c>carsim fingerprint-diff</c>.
/// </summary>
public sealed class FingerprintRecorder
{
    private readonly IReadOnlySet<string>? _schema;
    private readonly TextWriter? _dump;
    private readonly List<KeyValuePair<string, string>> _scratch = new(512);
    private IncrementalHash? _hash;
    private SectionRecord? _section;
    private readonly HashSet<string> _sectionChannels = new(StringComparer.Ordinal);

    /// <param name="caseId">Case being recorded.</param>
    /// <param name="schema">Channels to digest; null = every channel (when writing a new baseline).</param>
    /// <param name="dump">Optional writer for the full-precision dump.</param>
    public FingerprintRecorder(string caseId, IReadOnlySet<string>? schema, TextWriter? dump = null)
    {
        Record = new CaseRecord(caseId);
        _schema = schema;
        _dump = dump;
    }

    public CaseRecord Record { get; }

    public void BeginSection(string name)
    {
        EndSection();
        _section = new SectionRecord(name);
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _sectionChannels.Clear();
    }

    /// <summary>One sample of <paramref name="telemetry"/> (every public property, recursively).</summary>
    public void Sample(object? telemetry, string label = "")
    {
        var s = Current;
        _scratch.Clear();
        TelemetryFlattener.Flatten(telemetry, "", _scratch);
        string prefix = $"{s.Name}[{s.Samples}]{(label.Length > 0 ? "{" + label + "}" : "")}.";
        foreach (var (path, value) in _scratch) Leaf(prefix, path, value);
        s.Samples++;
    }

    /// <summary>A named key number: readable in the baseline, compared on its own and digested with the section.</summary>
    public void Value(string name, double value) => Text(name, TelemetryFlattener.Format(value));

    public void Text(string name, string value)
    {
        var s = Current;
        value = value.Replace("\n", "\\n", StringComparison.Ordinal);
        s.Values.Add(new(name, value));
        Leaf($"{s.Name}.", "#" + name, value);
    }

    public CaseRecord Finish()
    {
        EndSection();
        return Record;
    }

    private SectionRecord Current => _section ?? throw new InvalidOperationException("BeginSection first.");

    private void Leaf(string prefix, string path, string value)
    {
        _dump?.Write(prefix);
        _dump?.Write(path);
        _dump?.Write('=');
        _dump?.WriteLine(value);
        // Key numbers are compared by name (Values), not through the channel schema.
        bool keyNumber = path.StartsWith('#');
        string channel = keyNumber ? path : TelemetryFlattener.Channel(path);
        if (!keyNumber)
        {
            Record.Channels.Add(channel);
            if (_schema != null && !_schema.Contains(channel)) return;
            _sectionChannels.Add(channel);
        }
        _hash!.AppendData(Encoding.UTF8.GetBytes(prefix + path + "=" + value + "\n"));
    }

    private void EndSection()
    {
        if (_section == null) return;
        _section.Digest = Convert.ToHexString(_hash!.GetHashAndReset()).ToLowerInvariant();
        _section.ChannelCount = _sectionChannels.Count;
        _hash.Dispose();
        _hash = null;
        Record.Sections.Add(_section);
        _section = null;
    }
}
