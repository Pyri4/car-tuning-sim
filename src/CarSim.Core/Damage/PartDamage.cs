namespace CarSim.Core.Damage;

/// <summary>A recorded failure of one part.</summary>
public sealed record PartFailure(FailureMode Mode, double Time, bool Collateral, string Note = "");

/// <summary>
/// Accumulated fatigue per failure mode for one part instance (0 = fresh, 1 = failed) plus the
/// failure, if any. Lives on the <see cref="Parts.PartInstance"/> so damage travels with the part —
/// pull a cracked piston out and it is still cracked.
/// </summary>
public sealed class PartDamage
{
    private readonly Dictionary<FailureMode, double> _fatigue = new();

    public IReadOnlyDictionary<FailureMode, double> Fatigue => _fatigue;

    public PartFailure? Failure { get; private set; }

    public bool IsFailed => Failure != null;

    public double MaxFatigue => _fatigue.Count == 0 ? 0.0 : _fatigue.Values.Max();

    public double FatigueOf(FailureMode mode) => _fatigue.GetValueOrDefault(mode);

    /// <summary>Adds fatigue; returns true if this pushed the part to failure.</summary>
    public bool Accumulate(FailureMode mode, double amount)
    {
        if (amount <= 0 || IsFailed) return false;
        double v = Math.Min(1.0, _fatigue.GetValueOrDefault(mode) + amount);
        _fatigue[mode] = v;
        return v >= 1.0;
    }

    public void Fail(FailureMode mode, double time, bool collateral = false, string note = "")
    {
        if (IsFailed) return;
        _fatigue[mode] = 1.0;
        Failure = new PartFailure(mode, time, collateral, note);
    }

    /// <summary>Restores internal state (used by save/load).</summary>
    public void Restore(IEnumerable<KeyValuePair<FailureMode, double>> fatigue, PartFailure? failure)
    {
        _fatigue.Clear();
        foreach (var (k, v) in fatigue) _fatigue[k] = Math.Clamp(v, 0.0, 1.0);
        Failure = failure;
    }
}
