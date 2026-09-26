namespace CarSim.Core.Damage;

/// <summary>A recorded failure of one part.</summary>
public sealed record PartFailure(FailureMode Mode, double Time, bool Collateral, string Note = "");

/// <summary>
/// Lifetime record of the load that damaged a part in one failure mode: the highest stress ratio it saw
/// while taking damage (0 for process-driven modes such as knock) and how long it spent taking damage.
/// Persisted with the part, so a failure report can explain damage built up over earlier sessions.
/// </summary>
public readonly record struct DamageExposure(double PeakRatio, double Seconds);

/// <summary>
/// Accumulated fatigue per failure mode for one part instance (0 = fresh, 1 = failed) plus the
/// failure, if any. Lives on the <see cref="Parts.PartInstance"/> so damage travels with the part —
/// pull a cracked piston out and it is still cracked.
/// </summary>
public sealed class PartDamage
{
    private readonly Dictionary<FailureMode, double> _fatigue = new();
    private readonly Dictionary<FailureMode, DamageExposure> _exposure = new();

    public IReadOnlyDictionary<FailureMode, double> Fatigue => _fatigue;

    /// <summary>Damage ledger: per failure mode, the peak stress ratio and the time spent taking damage.</summary>
    public IReadOnlyDictionary<FailureMode, DamageExposure> Exposure => _exposure;

    public DamageExposure ExposureOf(FailureMode mode) => _exposure.GetValueOrDefault(mode);

    /// <summary>Records <paramref name="dt"/> seconds of damaging load at <paramref name="stressRatio"/> (0 when the mode has none).</summary>
    public void RecordExposure(FailureMode mode, double stressRatio, double dt)
    {
        if (dt <= 0) return;
        var e = _exposure.GetValueOrDefault(mode);
        _exposure[mode] = new DamageExposure(Math.Max(e.PeakRatio, stressRatio), e.Seconds + dt);
    }

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
    public void Restore(IEnumerable<KeyValuePair<FailureMode, double>> fatigue, PartFailure? failure,
        IEnumerable<KeyValuePair<FailureMode, DamageExposure>>? exposure = null)
    {
        _fatigue.Clear();
        foreach (var (k, v) in fatigue) _fatigue[k] = Math.Clamp(v, 0.0, 1.0);
        _exposure.Clear();
        foreach (var (k, v) in exposure ?? Array.Empty<KeyValuePair<FailureMode, DamageExposure>>())
            _exposure[k] = new DamageExposure(Math.Max(0.0, v.PeakRatio), Math.Max(0.0, v.Seconds));
        Failure = failure;
    }
}
