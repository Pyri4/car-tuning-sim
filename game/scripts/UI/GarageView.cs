using System;
using System.Linq;
using CarSim.Core.Damage;
using CarSim.Gameplay;
using Godot;

namespace CarTuningSim.UI;

/// <summary>Project car overview: money, engine location, fuel, the shelf of loose parts, save/load and the workshop log.</summary>
public partial class GarageView : HBoxContainer
{
    private VBoxContainer _info = null!, _shelf = null!;
    private RichTextLabel _log = null!;

    private static GameState State => GameState.Instance;
    private static Garage G => State.Garage;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 12);
        _info = Ui.VBox(8);
        _info.CustomMinimumSize = new Vector2(460, 0);
        AddChild(Ui.Scroll(Ui.Margin(_info, 12)));
        _shelf = Ui.VBox(6);
        var shelfScroll = Ui.Scroll(Ui.Margin(_shelf, 12));
        shelfScroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddChild(shelfScroll);
        var logBox = Ui.VBox();
        logBox.CustomMinimumSize = new Vector2(420, 0);
        logBox.AddChild(Ui.Heading("Workshop log"));
        _log = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        logBox.AddChild(_log);
        AddChild(Ui.Margin(logBox, 12));
        State.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree() => State.Changed -= Refresh;

    private void Refresh()
    {
        Ui.Clear(_info);
        var sc = State.Content.Scenarios.Values.FirstOrDefault();
        _info.AddChild(Ui.Heading(sc?.Name ?? "Project car"));
        if (sc != null) _info.AddChild(Ui.Wrapped(sc.Description, 14, Ui.Muted));
        _info.AddChild(Ui.Label($"Money: {G.Money:N0}", 18, Ui.Good));
        if (G.Vehicle is { } car)
        {
            _info.AddChild(Ui.Label($"Car: {car.Name}", 15));
            _info.AddChild(Ui.Wrapped(car.Description, 13, Ui.Muted));
        }
        _info.AddChild(Ui.Label($"Engine: {G.Engine.Definition.Name}", 15));
        _info.AddChild(Ui.Label(G.EngineInCar ? "The engine is in the car." : "The engine is on the engine stand.", 14, Ui.Muted));
        _info.AddChild(Ui.Button(G.EngineInCar ? "Pull the engine (engine hoist)" : "Install the engine in the car", () =>
        {
            var r = G.EngineInCar ? G.RemoveEngineFromCar() : G.InstallEngineInCar();
            if (!r.Ok) Ui.Message(this, "Engine", r.Message);
            State.NotifyChanged();
        }));

        if (G.Vehicle != null)
        {
            var (_, problem) = G.CreateVehicleSimulation();
            var drive = Ui.Button("Take it to the test track", () => GetTree().ChangeSceneToFile("res://scenes/Drive.tscn"));
            drive.Disabled = problem.Length > 0;
            _info.AddChild(drive);
            if (problem.Length > 0) _info.AddChild(Ui.Wrapped(problem, 13, Ui.Caution));
        }

        var failed = G.FailedParts.ToList();
        if (failed.Count > 0)
        {
            _info.AddChild(Ui.Label("Broken parts:", 15, Ui.Danger));
            foreach (var p in failed) _info.AddChild(Ui.Wrapped($"• {p.Definition.Name}: {PartInspector.Inspect(p)[0].Text}", 13, Ui.Danger));
        }

        _info.AddChild(Ui.Heading("Fuel"));
        var fuel = new OptionButton();
        var fuels = State.Content.Fuels.Values.OrderBy(f => f.OctaneRon).ToList();
        foreach (var f in fuels) fuel.AddItem($"{f.Name}");
        fuel.Selected = fuels.FindIndex(f => f.Id == G.FuelId);
        fuel.ItemSelected += idx => { G.SetFuel(fuels[(int)idx].Id); State.NotifyChanged(); };
        _info.AddChild(fuel);
        _info.AddChild(Ui.Wrapped(G.Fuel.Description, 13, Ui.Muted));

        _info.AddChild(Ui.Heading("Game"));
        var row = Ui.HBox();
        row.AddChild(Ui.Button("Save", () => { try { Ui.Message(this, "Saved", State.Save()); } catch (Exception e) { Ui.Message(this, "Save failed", e.Message); } }));
        row.AddChild(Ui.Button("Load", () => { try { State.Load(); } catch (Exception e) { Ui.Message(this, "Load failed", e.Message); } }));
        row.AddChild(Ui.Button("New game", () => State.NewGame()));
        _info.AddChild(row);
        if (State.ContentErrors.Count > 0)
            _info.AddChild(Ui.Wrapped($"{State.ContentErrors.Count} content error(s): {string.Join("; ", State.ContentErrors.Take(3))}", 13, Ui.Danger));
        _info.AddChild(Ui.Wrapped($"Content: {State.ContentDir}", 12, Ui.Muted));
        _info.AddChild(Ui.Wrapped(State.Mods.Count == 0
            ? $"No mods (put mod folders in {State.ModsDir})."
            : $"Mods: {string.Join(", ", State.Mods)} ({State.ContentOverrides.Count} override(s))", 12, Ui.Muted));

        Ui.Clear(_shelf);
        _shelf.AddChild(Ui.Heading($"Shelf ({G.Inventory.Count} parts)"));
        if (G.Inventory.Count == 0)
            _shelf.AddChild(Ui.Wrapped("Empty. Parts you remove or buy land here. Buy parts from the Workshop tab (select a slot).", 14, Ui.Muted));
        foreach (var part in G.Inventory.OrderBy(p => p.Category).ThenBy(p => p.Definition.Name))
        {
            var box = Ui.VBox(2);
            var head = Ui.HBox();
            head.AddChild(Ui.Expand(Ui.Label(part.Definition.Name, 15)));
            head.AddChild(Ui.Label($"{part.Condition * 100:F0} %", 14, Ui.ConditionColor(part.Condition)));
            head.AddChild(Ui.Button($"Sell {Garage.SellPrice(part):N0}", () => { G.Sell(part); State.NotifyChanged(); }));
            box.AddChild(head);
            foreach (var f in PartInspector.Inspect(part).Where(f => f.Severity != FindingSeverity.Good))
                box.AddChild(Ui.Wrapped("   " + f.Text, 13, f.Severity == FindingSeverity.Minor ? Ui.Caution : Ui.Danger));
            var targets = G.SlotsFor(part).Where(s => !G.IsFilled(s.Id)).ToList();
            foreach (var slot in targets)
            {
                var check = G.CanInstall(slot.Id, part);
                var install = Ui.Button($"Install in {slot.Label}", () =>
                {
                    var r = G.InstallPart(part, slot.Id);
                    if (!r.Ok) Ui.Message(this, "Install", r.Message);
                    State.NotifyChanged();
                });
                install.Disabled = !check.Ok;
                install.TooltipText = check.Ok ? "" : check.Message;
                box.AddChild(install);
            }
            _shelf.AddChild(Ui.Panel(box, 8));
        }

        _log.Text = string.Join("\n", G.Log.TakeLast(200));
    }
}
