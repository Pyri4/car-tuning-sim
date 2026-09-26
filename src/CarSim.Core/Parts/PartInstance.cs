using CarSim.Core.Damage;

namespace CarSim.Core.Parts;

/// <summary>
/// A physical copy of a part with its own condition. Wear is gradual degradation that changes how
/// the part performs (e.g. worn bearings → larger clearance → lower oil pressure). Fatigue and
/// failure are tracked by the damage model.
/// </summary>
public sealed class PartInstance
{
    private double _wear;

    public PartInstance(string instanceId, PartDefinition definition, double wear = 0.0)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance id required.", nameof(instanceId));
        InstanceId = instanceId;
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Wear = wear;
    }

    public string InstanceId { get; }
    public PartDefinition Definition { get; }
    public string Category => Definition.Category;

    /// <summary>Wear, 0 = new, 1 = worn out. Clamped to [0, 1].</summary>
    public double Wear
    {
        get => _wear;
        set => _wear = double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Fatigue and failure state.</summary>
    public PartDamage Damage { get; } = new();

    /// <summary>Overall condition 0–1 (1 = new): limited by the worse of wear and fatigue; 0 if failed.</summary>
    public double Condition => Damage.IsFailed ? 0.0 : 1.0 - Math.Max(Wear, Damage.MaxFatigue);

    public bool IsFailed => Damage.IsFailed;

    private readonly Dictionary<string, double> _settings = new(StringComparer.Ordinal);
    private PartSpec? _effectiveSpec;

    /// <summary>Setup settings that differ from the part's defaults (field → value).</summary>
    public IReadOnlyDictionary<string, double> Settings => _settings;

    /// <summary>The spec the simulation uses: the definition's, with this part's settings applied.</summary>
    public PartSpec EffectiveSpec => _effectiveSpec ??= _settings.Count == 0 ? Definition.Spec : SpecAdjuster.With(Definition.Spec, _settings);

    /// <summary>Current value of an adjustable field (its default unless changed).</summary>
    public double SettingOf(string field) =>
        _settings.TryGetValue(field, out var v) ? v
        : Definition.FindAdjustment(field)?.Default ?? throw new ArgumentException($"{Definition.Name} has no adjustment '{field}'.");

    /// <summary>
    /// Sets an adjustable field (clamped to its range and snapped to its step). Returns false if the part
    /// has no such adjustment.
    /// </summary>
    public bool Adjust(string field, double value)
    {
        var adjustment = Definition.FindAdjustment(field);
        if (adjustment == null) return false;
        double v = adjustment.Snap(value);
        if (Math.Abs(v - adjustment.Default) < 1e-9) _settings.Remove(field);
        else _settings[field] = v;
        _effectiveSpec = null;
        return true;
    }

    public T Spec<T>() where T : PartSpec =>
        EffectiveSpec as T ?? throw new InvalidOperationException($"Part '{Definition.Id}' has spec {EffectiveSpec.GetType().Name}, not {typeof(T).Name}.");

    public override string ToString() => $"{Definition.Name} #{InstanceId} (condition {Condition:P0})";
}

/// <summary>Creates part instances with deterministic, sequential ids (stable across save/load).</summary>
public sealed class PartInstanceFactory
{
    private long _next;

    public PartInstanceFactory(long firstId = 1) => _next = firstId;

    public long NextId => Interlocked.Read(ref _next);

    public PartInstance Create(PartDefinition definition, double wear = 0.0) =>
        new($"p{Interlocked.Increment(ref _next) - 1}", definition, wear);
}
