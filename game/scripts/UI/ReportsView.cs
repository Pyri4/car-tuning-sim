using System.Linq;
using CarSim.Core.Damage;
using Godot;

namespace CarTuningSim.UI;

/// <summary>Failure reports from this session and a full inspection of the engine and chassis.</summary>
public partial class ReportsView : HSplitContainer
{
    private ItemList _list = null!;
    private Label _text = null!;
    private VBoxContainer _inspection = null!;

    private static GameState State => GameState.Instance;

    public override void _Ready()
    {
        SplitOffsets = new[] { 420 };
        var left = Ui.VBox();
        left.CustomMinimumSize = new Vector2(400, 0);
        left.AddChild(Ui.Heading("Failure reports"));
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.ItemSelected += idx => Show((int)idx);
        left.AddChild(_list);
        left.AddChild(Ui.Heading("Inspection (engine and car)"));
        _inspection = Ui.VBox(2);
        left.AddChild(Ui.Scroll(_inspection));
        AddChild(Ui.Margin(left, 10));
        _text = Ui.Wrapped("", 15);
        AddChild(Ui.Scroll(Ui.Margin(_text, 14)));
        State.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree() => State.Changed -= Refresh;

    private void Refresh()
    {
        _list.Clear();
        foreach (var r in State.FailureReports) _list.AddItem($"{r.Title} ({r.PartName})");
        if (State.FailureReports.Count > 0)
        {
            _list.Select(State.FailureReports.Count - 1);
            Show(State.FailureReports.Count - 1);
        }
        else _text.Text = "No failures yet. Push the engine on the dyno or the test track and see what gives.";

        Ui.Clear(_inspection);
        var g = State.Garage;
        var slots = g.Engine.Definition.Slots.Concat(g.Vehicle?.Slots ?? System.Array.Empty<CarSim.Core.Engines.EngineSlotDefinition>());
        foreach (var slot in slots)
        {
            var part = g.PartIn(slot.Id);
            if (part == null) continue;
            var findings = PartInspector.Inspect(part).Where(f => f.Severity != FindingSeverity.Good).ToList();
            if (findings.Count == 0) continue;
            foreach (var f in findings)
                _inspection.AddChild(Ui.Wrapped($"{slot.Label}: {f.Text}", 13, f.Severity == FindingSeverity.Minor ? Ui.Caution : Ui.Danger));
        }
    }

    private void Show(int index)
    {
        if (index < 0 || index >= State.FailureReports.Count) return;
        _text.Text = State.FailureReports[index].ToText();
    }
}
