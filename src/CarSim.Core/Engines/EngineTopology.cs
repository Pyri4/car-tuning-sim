using CarSim.Core.Parts;

namespace CarSim.Core.Engines;

/// <summary>
/// What the engine model can represent, as rules on an engine family's data. The model has one crankshaft, one
/// lubrication, cooling and fuel system and one ECU for the whole engine (<see cref="EngineWideCategories"/>), and
/// one air path per bank (<see cref="BankCategories"/>): each bank is served by exactly one cylinder head, gasket,
/// spring set, camshaft set, intake manifold, throttle, exhaust manifold and exhaust, and by at most one turbocharger
/// and intercooler. A part may serve several banks — a pushrod camshaft or a common plenum on a V8, one turbocharger
/// on a flat four — and several banks sharing it split it by their cylinders. Families outside these rules are
/// rejected when the content loads, with the reason; assemblies missing a part for some bank fail validation, so
/// building a configuration never throws on valid data. Superchargers, dry sumps and per-cylinder resolution need
/// model work first (see ARCHITECTURE.md and ENGINE_AUTHORING_GUIDE.md).
/// </summary>
public static class EngineTopology
{
    /// <summary>Categories the simulation reads every step; an engine cannot run without one (per bank, where banked).</summary>
    public static readonly IReadOnlyList<string> RequiredCategories = new[]
    {
        PartCategory.Block, PartCategory.MainBearings, PartCategory.Crankshaft, PartCategory.RodBearings,
        PartCategory.ConnectingRods, PartCategory.Pistons, PartCategory.HeadGasket, PartCategory.CylinderHead,
        PartCategory.ValveSprings, PartCategory.Camshafts, PartCategory.IntakeManifold, PartCategory.ThrottleBody,
        PartCategory.Injectors, PartCategory.FuelPump, PartCategory.ExhaustManifold, PartCategory.Exhaust,
        PartCategory.OilPump, PartCategory.OilPan, PartCategory.Radiator, PartCategory.Flywheel, PartCategory.Ecu,
    };

    /// <summary>
    /// Categories with an explicit fallback when empty: no turbocharger = naturally aspirated, no
    /// intercooler = the compressor outlet goes straight to the throttle.
    /// </summary>
    public static readonly IReadOnlyList<string> OptionalCategories = new[] { PartCategory.Turbocharger, PartCategory.Intercooler };

    /// <summary>
    /// Categories with one part for the whole engine: the rotating assembly and the lubrication, cooling, fuel and
    /// control systems. Per-cylinder parts (pistons, rods, injectors) are one set of N.
    /// </summary>
    public static readonly IReadOnlyList<string> EngineWideCategories = new[]
    {
        PartCategory.Block, PartCategory.MainBearings, PartCategory.Crankshaft, PartCategory.RodBearings,
        PartCategory.ConnectingRods, PartCategory.Pistons, PartCategory.Injectors, PartCategory.FuelPump,
        PartCategory.OilPump, PartCategory.OilPan, PartCategory.Radiator, PartCategory.Flywheel, PartCategory.Ecu,
    };

    /// <summary>
    /// Categories resolved per bank: every bank is served by exactly one part of each (at most one for the optional
    /// ones), and one part may serve several banks.
    /// </summary>
    public static readonly IReadOnlyList<string> BankCategories = new[]
    {
        PartCategory.HeadGasket, PartCategory.CylinderHead, PartCategory.ValveSprings, PartCategory.Camshafts,
        PartCategory.IntakeManifold, PartCategory.ThrottleBody, PartCategory.ExhaustManifold, PartCategory.Exhaust,
        PartCategory.Turbocharger, PartCategory.Intercooler,
    };

    /// <summary>Recognised <see cref="EngineDefinition.Layout"/> values.</summary>
    public static readonly IReadOnlyList<string> Layouts = new[] { "inline", "v", "flat" };

    public static bool IsBankScoped(string category) => BankCategories.Contains(category);

    /// <summary>Problems that make an engine family impossible to simulate; empty when it fits the model.</summary>
    public static IReadOnlyList<string> CheckFamily(EngineDefinition engine)
    {
        var problems = new List<string>();
        CheckArchitecture(engine, problems);
        foreach (var slot in engine.Slots)
            foreach (var bankId in slot.Banks)
                if (engine.BankIndex(bankId) < 0)
                    problems.Add($"Slot '{slot.Id}' names bank '{bankId}', which this engine does not have " +
                                 $"(banks: {string.Join(", ", engine.Banks.Select(b => $"'{b.Id}'"))}).");

        foreach (string category in RequiredCategories.Concat(OptionalCategories))
        {
            bool required = RequiredCategories.Contains(category);
            var slots = engine.Slots.Where(s => s.Category == category).ToList();
            if (required && slots.Any(s => !s.Required))
                problems.Add($"Slot '{slots.First(s => !s.Required).Id}' ({category}) cannot be optional: the engine model has no fallback without one.");
            if (!IsBankScoped(category))
            {
                if (required && slots.Count == 0)
                    problems.Add($"No slot for '{category}': the engine model needs one.");
                else if (slots.Count > 1)
                    problems.Add($"Slots {string.Join(", ", slots.Select(s => $"'{s.Id}'"))} all take '{category}': the engine model has one for the " +
                                 "whole engine (sell per-cylinder parts as a set in one slot).");
                foreach (var s in slots.Where(s => s.Banks.Count > 0))
                    problems.Add($"Slot '{s.Id}' ({category}) serves only some banks: the engine model has one {category.Replace('_', ' ')} for the whole engine.");
                continue;
            }
            for (int b = 0; b < engine.Banks.Count; b++)
            {
                var serving = slots.Where(s => engine.Serves(s, b)).ToList();
                string where = engine.Banks.Count > 1 ? $"Bank '{engine.Banks[b].Id}'" : "The engine";
                if (required && serving.Count == 0)
                    problems.Add($"{where} has no slot for '{category}': every bank needs one.");
                else if (serving.Count > 1)
                    problems.Add($"Slots {string.Join(", ", serving.Select(s => $"'{s.Id}'"))} all serve {(engine.Banks.Count > 1 ? $"bank '{engine.Banks[b].Id}'" : "the engine")} " +
                                 $"with '{category}': each bank takes one.");
            }
        }
        if (!Layouts.Contains(engine.Layout))
            problems.Add($"Unknown layout '{engine.Layout}' (expected {string.Join(", ", Layouts)}).");
        return problems;
    }

    private static void CheckArchitecture(EngineDefinition engine, List<string> problems)
    {
        int n = engine.Cylinders;
        var banks = engine.Banks;
        if (banks.Count == 0) { problems.Add("An engine needs at least one bank."); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bank in banks)
        {
            if (string.IsNullOrWhiteSpace(bank.Id)) problems.Add("Every bank needs an 'id'.");
            else if (!ids.Add(bank.Id)) problems.Add($"Duplicate bank id '{bank.Id}'.");
            if (bank.Cylinders.Count == 0) problems.Add($"Bank '{bank.Id}' has no cylinders.");
        }
        var numbers = banks.SelectMany(b => b.Cylinders).ToList();
        foreach (int c in numbers.Where(c => c < 1 || c > n).Distinct())
            problems.Add($"Cylinder {c} is outside 1–{n} (the engine has {n} cylinders).");
        foreach (var dup in numbers.GroupBy(c => c).Where(g => g.Count() > 1))
            problems.Add($"Cylinder {dup.Key} is in more than one bank.");
        var missing = Enumerable.Range(1, Math.Max(0, n)).Except(numbers).ToList();
        if (missing.Count > 0 && n > 0)
            problems.Add($"Cylinder{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing)} {(missing.Count > 1 ? "are" : "is")} in no bank.");

        switch (engine.Layout)
        {
            case "inline" when banks.Count != 1:
                problems.Add($"An inline engine has one bank; this one declares {banks.Count}.");
                break;
            case "v" when banks.Count < 2:
                problems.Add("A V engine needs at least two banks.");
                break;
            case "flat" when banks.Count != 2:
                problems.Add($"A flat engine has two opposed banks; this one declares {banks.Count}.");
                break;
        }
        if (engine.BankAngleDeg is double angle)
        {
            if (engine.Layout == "inline") problems.Add("bank_angle_deg is meaningless on an inline engine.");
            else if (engine.Layout == "v" && !(angle > 0 && angle < 180)) problems.Add($"A V engine's bank angle must be within (0°, 180°) (was {angle}°).");
            else if (engine.Layout == "flat" && angle != 180) problems.Add($"A flat engine's banks are 180° apart (was {angle}°).");
        }

        var order = engine.FiringOrder;
        if (order.Count > 0)
        {
            if (order.Count != n || order.Distinct().Count() != n || order.Any(c => c < 1 || c > n))
                problems.Add($"Firing order {string.Join("-", order)} must name each of the {n} cylinders exactly once.");
        }
    }

    /// <summary>Required categories with no part installed for some bank, as (category, bank index).</summary>
    public static IEnumerable<(string Category, int Bank)> MissingCategories(EngineAssembly assembly)
    {
        var def = assembly.Definition;
        foreach (string category in RequiredCategories)
        {
            if (!IsBankScoped(category))
            {
                if (assembly.FindByCategory(category) == null) yield return (category, -1);
                continue;
            }
            for (int b = 0; b < def.Banks.Count; b++)
                if (assembly.PartFor(category, b) == null) yield return (category, b);
        }
    }
}
