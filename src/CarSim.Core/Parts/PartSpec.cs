namespace CarSim.Core.Parts;

/// <summary>
/// Base class for category-specific part specifications. Authoring properties carry their unit in
/// the name (e.g. <c>BoreMm</c> ↔ JSON <c>bore_mm</c>); derived SI properties (e.g. <c>Bore</c> in
/// metres) are what the simulation reads.
/// </summary>
public abstract class PartSpec
{
    /// <summary>Content validation: returns human-readable problems with the authored values.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Validate(new SpecChecker(problems));
        return problems;
    }

    protected abstract void Validate(SpecChecker check);
}

/// <summary>Small helper that records range problems while validating specs.</summary>
public sealed class SpecChecker
{
    private readonly List<string> _problems;
    public SpecChecker(List<string> problems) => _problems = problems;

    public void Positive(string name, double value)
    {
        if (!(value > 0) || double.IsInfinity(value)) _problems.Add($"{name} must be > 0 (was {value}).");
    }

    public void NonNegative(string name, double value)
    {
        if (!(value >= 0) || double.IsInfinity(value)) _problems.Add($"{name} must be >= 0 (was {value}).");
    }

    public void Range(string name, double value, double min, double max)
    {
        if (!(value >= min && value <= max)) _problems.Add($"{name} must be within [{min}, {max}] (was {value}).");
    }

    public void PositiveInt(string name, int value)
    {
        if (value <= 0) _problems.Add($"{name} must be a positive integer (was {value}).");
    }

    public void That(bool condition, string problem)
    {
        if (!condition) _problems.Add(problem);
    }

    /// <summary>Validates a list of [x, y] pairs: two values each, x strictly increasing, y non-negative.</summary>
    public void CurvePairs(string name, IReadOnlyList<double[]>? pairs, int minPoints = 2)
    {
        if (pairs == null || pairs.Count < minPoints)
        {
            _problems.Add($"{name} needs at least {minPoints} [x, y] points.");
            return;
        }
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i] == null || pairs[i].Length != 2)
            {
                _problems.Add($"{name}[{i}] must be a [x, y] pair.");
                return;
            }
            if (pairs[i][1] < 0) _problems.Add($"{name}[{i}] y must be >= 0.");
            if (i > 0 && !(pairs[i][0] > pairs[i - 1][0])) _problems.Add($"{name} x values must be strictly increasing (index {i}).");
        }
    }
}
