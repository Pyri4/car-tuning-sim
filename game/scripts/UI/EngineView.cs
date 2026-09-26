using System.Collections.Generic;
using System.Linq;
using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Gameplay;
using Godot;

namespace CarTuningSim.UI;

/// <summary>Engine component tree, part details, inspection, removal/installation and the parts shop.</summary>
public partial class EngineView : HSplitContainer
{
    private static readonly (string Group, string[] Categories)[] Groups =
    {
        ("Bottom end", new[] { "block", "main_bearings", "crankshaft", "rod_bearings", "connecting_rods", "pistons", "oil_pump", "oil_pan", "flywheel" }),
        ("Top end", new[] { "head_gasket", "cylinder_head", "valve_springs", "camshafts" }),
        ("Induction", new[] { "intake_manifold", "throttle_body", "turbocharger", "intercooler" }),
        ("Fuel", new[] { "injectors", "fuel_pump" }),
        ("Exhaust", new[] { "exhaust_manifold", "exhaust" }),
        ("Cooling & electronics", new[] { "radiator", "ecu" }),
    };

    private Tree _tree = null!;
    private VBoxContainer _details = null!;
    private RichTextLabel _report = null!;
    private Label _engineStatus = null!;
    private Button _engineToggle = null!;
    private string? _selectedSlot;

    private static GameState State => GameState.Instance;
    private static Garage G => State.Garage;

    public string? InitialSelection { get; set; }

    public override void _Ready()
    {
        SplitOffsets = new[] { 560 };
        var left = Ui.VBox();
        left.CustomMinimumSize = new Vector2(540, 0);
        var header = Ui.HBox();
        _engineStatus = Ui.Label("", 14);
        _engineToggle = Ui.Button("", ToggleEngine);
        header.AddChild(Ui.Expand(_engineStatus));
        header.AddChild(_engineToggle);
        left.AddChild(header);

        _tree = new Tree { HideRoot = true, Columns = 3, ColumnTitlesVisible = true, SelectMode = Tree.SelectModeEnum.Row };
        _tree.SetColumnTitle(0, "Slot / part");
        _tree.SetColumnTitle(1, "Condition");
        _tree.SetColumnTitle(2, "Status");
        _tree.SetColumnExpand(0, true);
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnExpand(2, false);
        _tree.SetColumnCustomMinimumWidth(1, 90);
        _tree.SetColumnCustomMinimumWidth(2, 130);
        _tree.SizeFlagsVertical = SizeFlags.ExpandFill;
        _tree.ItemSelected += OnItemSelected;
        left.AddChild(_tree);

        _report = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false };
        _report.CustomMinimumSize = new Vector2(0, 150);
        left.AddChild(Ui.Panel(_report, 6));
        AddChild(left);

        _details = Ui.VBox(8);
        AddChild(Ui.Scroll(Ui.Margin(_details, 12)));

        _selectedSlot = InitialSelection;
        State.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree() => State.Changed -= Refresh;

    private void ToggleEngine()
    {
        var r = G.EngineInCar ? G.RemoveEngineFromCar() : G.InstallEngineInCar();
        if (!r.Ok) Ui.Message(this, "Engine", r.Message);
        State.NotifyChanged();
    }

    private void Refresh()
    {
        _engineStatus.Text = G.EngineInCar
            ? "Engine is in the car: only bolt-on parts are reachable."
            : "Engine is on the stand: everything is reachable.";
        _engineToggle.Text = G.EngineInCar ? "Pull engine" : "Install engine in car";
        BuildTree();
        BuildReport();
        BuildDetails();
    }

    private void BuildTree()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        var engine = G.Engine;
        foreach (var (group, categories) in Groups)
        {
            var slots = engine.Definition.Slots.Where(s => categories.Contains(s.Category)).ToList();
            if (slots.Count == 0) continue;
            var groupItem = _tree.CreateItem(root);
            groupItem.SetText(0, group);
            groupItem.SetCustomColor(0, Ui.Accent);
            groupItem.SetSelectable(0, false);
            groupItem.SetSelectable(1, false);
            groupItem.SetSelectable(2, false);
            foreach (var slot in slots)
            {
                var item = _tree.CreateItem(groupItem);
                var part = engine.PartIn(slot.Id);
                item.SetText(0, $"{slot.Label}: {(part == null ? "— empty —" : part.Definition.Name)}");
                item.SetMetadata(0, slot.Id);
                if (part != null)
                {
                    item.SetText(1, $"{part.Condition * 100:F0} %");
                    item.SetCustomColor(1, Ui.ConditionColor(part.Condition));
                }
                string status = part == null ? (slot.Required ? "missing" : "optional")
                    : part.IsFailed ? "FAILED"
                    : PartInspector.Inspect(part).Any(f => f.Severity == FindingSeverity.Major) ? "damaged"
                    : PartInspector.Inspect(part).Any(f => f.Severity == FindingSeverity.Minor) ? "worn"
                    : "ok";
                if (G.EngineInCar && !slot.AccessibleInVehicle) status += " · in car";
                item.SetText(2, status);
                item.SetCustomColor(2, status.StartsWith("FAILED") || status.StartsWith("missing") ? Ui.Danger
                    : status.StartsWith("damaged") || status.StartsWith("worn") ? Ui.Caution : Ui.Muted);
                if (part == null) item.SetCustomColor(0, Ui.Muted);
                if (slot.Id == _selectedSlot) item.Select(0);
            }
        }
    }

    private void BuildReport()
    {
        var report = G.Validate();
        var sb = new System.Text.StringBuilder();
        var geometry = EngineGeometry.TryCreate(G.Engine, out _);
        if (geometry != null)
        {
            sb.Append($"[b]{Units.M3ToCc(geometry.Displacement):F0} cc[/b]  ·  bore × stroke {Units.MToMm(geometry.Bore):F1} × {Units.MToMm(geometry.Stroke):F1} mm  ·  ");
            sb.Append($"CR [b]{geometry.CompressionRatio:F2}:1[/b]  ·  piston-to-head {Units.MToMm(geometry.PistonToHeadClearance):F2} mm\n");
        }
        sb.Append($"Compression test: [b]{EngineDiagnostics.CompressionTestBar(G.Engine):F1} bar[/b]\n");
        sb.Append(report.CanRun ? "[color=#73d973]Engine can run.[/color]\n" : "[color=#f2594d]Engine cannot run:[/color]\n");
        foreach (var issue in report.Issues.Where(i => i.Severity != IssueSeverity.Info))
        {
            string color = issue.Severity == IssueSeverity.Error ? "#f2594d" : "#f2cc4d";
            sb.Append($"[color={color}]• {issue.Message}[/color]\n");
        }
        _report.Text = sb.ToString();
    }

    private void OnItemSelected()
    {
        var item = _tree.GetSelected();
        if (item == null) return;
        var meta = item.GetMetadata(0);
        if (meta.VariantType != Variant.Type.String) return;
        _selectedSlot = meta.AsString();
        BuildDetails();
    }

    private void BuildDetails()
    {
        Ui.Clear(_details);
        if (_selectedSlot == null || G.Engine.Definition.FindSlot(_selectedSlot) is not { } slot)
        {
            _details.AddChild(Ui.Heading("Select a part"));
            _details.AddChild(Ui.Wrapped("Pick a slot in the tree to inspect the part, see its specifications, remove it, or fit a replacement from the shelf or the shop.", 14, Ui.Muted));
            return;
        }
        var part = G.Engine.PartIn(slot.Id);
        _details.AddChild(Ui.Heading(slot.Label));
        if (part == null)
        {
            _details.AddChild(Ui.Label("Empty.", 15, Ui.Muted));
        }
        else
        {
            var def = part.Definition;
            _details.AddChild(Ui.Label(def.Name, 16));
            _details.AddChild(Ui.Label($"{def.Manufacturer}  ·  {def.MassKg:F1} kg  ·  new price {def.Price:N0}", 13, Ui.Muted));
            if (def.Description.Length > 0) _details.AddChild(Ui.Wrapped(def.Description, 13, Ui.Muted));
            _details.AddChild(Ui.Label($"Condition {part.Condition * 100:F0} %   (wear {part.Wear * 100:F0} %, fatigue {part.Damage.MaxFatigue * 100:F0} %)", 14, Ui.ConditionColor(part.Condition)));

            _details.AddChild(Ui.Label("Inspection", 15, Ui.Accent));
            foreach (var f in PartInspector.Inspect(part))
            {
                var color = f.Severity switch { FindingSeverity.Good => Ui.Good, FindingSeverity.Minor => Ui.Caution, _ => Ui.Danger };
                _details.AddChild(Ui.Wrapped("• " + f.Text, 14, color));
            }

            _details.AddChild(Ui.Label("Specifications", 15, Ui.Accent));
            _details.AddChild(SpecGrid(def));

            var actions = Ui.HBox();
            var remove = Ui.Button("Remove to shelf", () => Act(G.RemovePart(slot.Id)));
            actions.AddChild(remove);
            _details.AddChild(actions);
            var blocked = G.Engine.CanRemove(slot.Id);
            if (!G.CanAccess(slot.Id))
            {
                remove.Disabled = true;
                _details.AddChild(Ui.Wrapped("Not reachable with the engine in the car: pull the engine first.", 13, Ui.Caution));
            }
            else if (!blocked.Ok)
            {
                remove.Disabled = true;
                var seq = G.Engine.RemovalSequenceFor(slot.Id).Select(s => G.Engine.Definition.GetSlot(s).Label);
                _details.AddChild(Ui.Wrapped($"In the way: {string.Join(" → ", seq)}", 13, Ui.Caution));
                _details.AddChild(Ui.Button("Remove everything in the way", () => RemoveChain(slot.Id)));
            }
        }

        // Replacement candidates from the shelf.
        var shelf = G.Inventory.Where(p => p.Category == slot.Category).ToList();
        if (part == null && shelf.Count > 0)
        {
            _details.AddChild(Ui.Label("On the shelf", 15, Ui.Accent));
            foreach (var p in shelf)
            {
                var row = Ui.HBox();
                row.AddChild(Ui.Expand(Ui.Label($"{p.Definition.Name}  ({p.Condition * 100:F0} %)", 14, Ui.ConditionColor(p.Condition))));
                var install = Ui.Button("Install", () => Act(G.InstallPart(p, slot.Id)));
                install.Disabled = !G.CanAccess(slot.Id) || !G.Engine.CanInstall(slot.Id, p).Ok;
                row.AddChild(install);
                _details.AddChild(row);
            }
            var check = G.Engine.CanInstall(slot.Id, shelf[0]);
            if (!check.Ok) _details.AddChild(Ui.Wrapped(check.Message, 13, Ui.Caution));
        }

        // Shop.
        _details.AddChild(Ui.Label($"Shop — {slot.Category.Replace('_', ' ')}", 15, Ui.Accent));
        foreach (var def in State.Content.PartsInCategory(slot.Category))
        {
            var row = Ui.HBox();
            var name = Ui.Expand(Ui.Wrapped(def.Name + (def.Description.Length > 0 ? $" — {def.Description}" : ""), 13));
            row.AddChild(name);
            row.AddChild(Ui.Label($"{def.Price:N0}", 14, def.Price <= G.Money ? Ui.Good : Ui.Danger));
            var buy = Ui.Button("Buy", () => Act(G.Buy(def.Id)));
            buy.Disabled = def.Price > G.Money;
            row.AddChild(buy);
            _details.AddChild(row);
        }
    }

    private static GridContainer SpecGrid(PartDefinition def)
    {
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 18);
        foreach (var (label, value) in SpecDescriber.Describe(def.Spec))
        {
            grid.AddChild(Ui.Label(label, 13, Ui.Muted));
            grid.AddChild(Ui.Wrapped(value, 13));
        }
        return grid;
    }

    private void RemoveChain(string slotId)
    {
        foreach (var s in G.Engine.RemovalSequenceFor(slotId))
        {
            var r = G.RemovePart(s);
            if (!r.Ok) { Ui.Message(this, "Cannot remove", r.Message); break; }
        }
        State.NotifyChanged();
    }

    private void Act(ActionResult r)
    {
        if (!r.Ok) Ui.Message(this, "Workshop", r.Message);
        State.NotifyChanged();
    }
}
