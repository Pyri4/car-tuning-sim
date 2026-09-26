using CarSim.Core.Content;
using CarSim.Core.Parts;

namespace CarSim.Core.Engines;

/// <summary>Outcome of an install/remove request. <see cref="BlockingSlots"/> lists what is in the way.</summary>
public sealed record AssemblyResult(bool Ok, string Message, IReadOnlyList<string> BlockingSlots)
{
    public static AssemblyResult Success(string message = "") => new(true, message, Array.Empty<string>());
    public static AssemblyResult Fail(string message, params string[] blocking) => new(false, message, blocking);
}

/// <summary>
/// A concrete engine build: which part instance sits in which slot. Enforces the engine family's
/// assembly order (see <see cref="EngineSlotDefinition.InstallAfter"/>). Physical compatibility of
/// the chosen parts is checked separately by <see cref="AssemblyValidator"/>, because players are
/// allowed to assemble a bad combination and discover the consequences.
/// </summary>
public sealed class EngineAssembly
{
    private readonly Dictionary<string, PartInstance> _installed = new(StringComparer.Ordinal);

    public EngineAssembly(EngineDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public EngineDefinition Definition { get; }

    /// <summary>Incremented on every install/remove so derived data can be cached safely.</summary>
    public int Revision { get; private set; }

    public IReadOnlyDictionary<string, PartInstance> Installed => _installed;

    public PartInstance? PartIn(string slotId) => _installed.GetValueOrDefault(slotId);

    public bool IsInstalled(string slotId) => _installed.ContainsKey(slotId);

    /// <summary>First installed part of <paramref name="category"/>, in slot declaration order.</summary>
    public PartInstance? FindByCategory(string category)
    {
        foreach (var slot in Definition.Slots)
            if (slot.Category == category && _installed.TryGetValue(slot.Id, out var p)) return p;
        return null;
    }

    /// <summary>Spec of the first installed part of <paramref name="category"/>, or null.</summary>
    public T? SpecOf<T>(string category) where T : PartSpec => FindByCategory(category)?.EffectiveSpec as T;

    public IEnumerable<PartInstance> AllParts => Definition.Slots
        .Where(s => _installed.ContainsKey(s.Id)).Select(s => _installed[s.Id]);

    public double TotalMassKg => _installed.Values.Sum(p => p.Definition.MassKg);

    public IReadOnlyList<EngineSlotDefinition> MissingRequiredSlots() =>
        Definition.Slots.Where(s => s.Required && !_installed.ContainsKey(s.Id)).ToList();

    public bool IsComplete => MissingRequiredSlots().Count == 0;

    public AssemblyResult CanInstall(string slotId, PartInstance part)
    {
        var slot = Definition.FindSlot(slotId);
        if (slot == null) return AssemblyResult.Fail($"This engine has no '{slotId}' slot.");
        if (part.Category != slot.Category)
            return AssemblyResult.Fail($"{part.Definition.Name} is a {part.Category} part; the {slot.Label} slot takes {slot.Category}.");
        if (_installed.TryGetValue(slotId, out var existing))
            return AssemblyResult.Fail($"{slot.Label} already has {existing.Definition.Name} installed. Remove it first.", slotId);
        if (_installed.Values.Any(p => ReferenceEquals(p, part) || p.InstanceId == part.InstanceId))
            return AssemblyResult.Fail($"{part.Definition.Name} (#{part.InstanceId}) is already installed in this engine.");
        var missing = slot.InstallAfter.Where(d => !_installed.ContainsKey(d)).ToArray();
        if (missing.Length > 0)
        {
            var names = missing.Select(m => Definition.GetSlot(m).Label);
            return AssemblyResult.Fail($"Install {string.Join(", ", names)} before {slot.Label}.", missing);
        }
        return AssemblyResult.Success();
    }

    public AssemblyResult Install(string slotId, PartInstance part)
    {
        var check = CanInstall(slotId, part);
        if (!check.Ok) return check;
        _installed[slotId] = part;
        Revision++;
        return AssemblyResult.Success($"Installed {part.Definition.Name} in {Definition.GetSlot(slotId).Label}.");
    }

    public AssemblyResult CanRemove(string slotId)
    {
        var slot = Definition.FindSlot(slotId);
        if (slot == null) return AssemblyResult.Fail($"This engine has no '{slotId}' slot.");
        if (!_installed.ContainsKey(slotId)) return AssemblyResult.Fail($"{slot.Label} is empty.");
        var blocking = Definition.DependentsOf(slotId).Where(d => _installed.ContainsKey(d.Id)).Select(d => d.Id).ToArray();
        if (blocking.Length > 0)
        {
            var names = blocking.Select(b => Definition.GetSlot(b).Label);
            return AssemblyResult.Fail($"Remove {string.Join(", ", names)} before {slot.Label}.", blocking);
        }
        return AssemblyResult.Success();
    }

    public AssemblyResult Remove(string slotId, out PartInstance? removed)
    {
        removed = null;
        var check = CanRemove(slotId);
        if (!check.Ok) return check;
        removed = _installed[slotId];
        _installed.Remove(slotId);
        Revision++;
        return AssemblyResult.Success($"Removed {removed.Definition.Name} from {Definition.GetSlot(slotId).Label}.");
    }

    /// <summary>
    /// Every installed slot that must come off before <paramref name="slotId"/> can be removed, in a
    /// valid removal order (outermost first). Does not include <paramref name="slotId"/> itself.
    /// </summary>
    public IReadOnlyList<string> RemovalSequenceFor(string slotId)
    {
        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            foreach (var dep in Definition.DependentsOf(id))
            {
                if (!_installed.ContainsKey(dep.Id) || !visited.Add(dep.Id)) continue;
                Visit(dep.Id);
                order.Add(dep.Id);
            }
        }
        Visit(slotId);
        return order;
    }

    /// <summary>Builds the engine family's factory configuration from content.</summary>
    public static EngineAssembly CreateStock(EngineDefinition definition, ContentDatabase content, PartInstanceFactory factory)
    {
        var assembly = new EngineAssembly(definition);
        foreach (var slot in definition.AssemblyOrder())
        {
            if (!definition.StockParts.TryGetValue(slot.Id, out var partId)) continue;
            var result = assembly.Install(slot.Id, factory.Create(content.GetPart(partId)));
            if (!result.Ok) throw new InvalidOperationException($"Stock build of '{definition.Id}' failed: {result.Message}");
        }
        return assembly;
    }
}
