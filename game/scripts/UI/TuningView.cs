using System;
using System.Linq;
using CarSim.Core.Common;
using CarSim.Core.Ecu;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using Godot;

namespace CarTuningSim.UI;

/// <summary>ECU hardware limits, calibration values and editable fuel/ignition/boost tables.</summary>
public partial class TuningView : HSplitContainer
{
    private VBoxContainer _left = null!;
    private GridContainer _grid = null!;
    private OptionButton _table = null!;
    private Label _tableHelp = null!;
    private int _lastRevision = -1;

    private static GameState State => GameState.Instance;
    private static EcuTune Tune => State.Garage.Tune;

    public override void _Ready()
    {
        SplitOffsets = new[] { 470 };
        _left = Ui.VBox(8);
        _left.CustomMinimumSize = new Vector2(450, 0);
        AddChild(Ui.Scroll(Ui.Margin(_left, 10)));

        var right = Ui.VBox(8);
        var bar = Ui.HBox();
        bar.AddChild(Ui.Label("Table:"));
        _table = new OptionButton();
        _table.AddItem("Ignition advance (° BTDC)");
        _table.AddItem("Target λ (1.00 = stoichiometric)");
        _table.AddItem("Boost target (kPa absolute)");
        _table.ItemSelected += _ => BuildGrid();
        bar.AddChild(_table);
        bar.AddChild(Ui.Button("−1° boost rows (≥120 kPa)", () => Edit(() => Tune.OffsetIgnition(-1, 110))));
        bar.AddChild(Ui.Button("+1° boost rows", () => Edit(() => Tune.OffsetIgnition(1, 110))));
        bar.AddChild(Ui.Button("−1° all", () => Edit(() => Tune.OffsetIgnition(-1))));
        bar.AddChild(Ui.Button("+1° all", () => Edit(() => Tune.OffsetIgnition(1))));
        right.AddChild(bar);
        _tableHelp = Ui.Wrapped("", 13, Ui.Muted);
        right.AddChild(_tableHelp);
        _grid = new GridContainer();
        _grid.AddThemeConstantOverride("h_separation", 2);
        _grid.AddThemeConstantOverride("v_separation", 2);
        var scroll = new ScrollContainer();
        scroll.AddChild(_grid);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        right.AddChild(scroll);
        AddChild(Ui.Margin(right, 10));

        State.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree() => State.Changed -= Refresh;

    private void Edit(Action a)
    {
        a();
        State.Garage.TuneChanged();
        BuildGrid();
    }

    private void Refresh()
    {
        if (State.Garage.Revision == _lastRevision) return;
        _lastRevision = State.Garage.Revision;
        BuildLeft();
        BuildGrid();
    }

    private void BuildLeft()
    {
        Ui.Clear(_left);
        var ecuPart = State.Garage.Engine.FindByCategory(PartCategory.Ecu);
        var hw = ecuPart?.Spec<EcuSpec>();
        _left.AddChild(Ui.Heading("ECU hardware"));
        if (hw == null)
        {
            _left.AddChild(Ui.Label("No ECU installed.", 14, Ui.Danger));
            return;
        }
        _left.AddChild(Ui.Label(ecuPart!.Definition.Name, 15));
        _left.AddChild(Ui.Wrapped($"MAP sensor range: {hw.MapSensorMaxKpa:F0} kPa absolute" +
            (hw.MapSensorMaxKpa < 150 ? " — cannot measure boost." : ""), 13, hw.MapSensorMaxKpa < 150 ? Ui.Caution : Ui.Muted));
        _left.AddChild(Ui.Label($"Boost control: {(hw.BoostControl ? "yes" : "no (wastegate spring only)")}", 13, Ui.Muted));
        _left.AddChild(Ui.Label($"Knock control: {(hw.KnockControl ? "yes" : "no")}", 13, Ui.Muted));
        _left.AddChild(Ui.Label($"Max rev limit: {hw.MaxRevLimitRpm:F0} rpm", 13, Ui.Muted));

        _left.AddChild(Ui.Heading("Calibration"));
        _left.AddChild(Ui.Label($"Tune: {Tune.Name}", 13, Ui.Muted));
        AddSpin("Rev limit (rpm)", 3000, hw.MaxRevLimitRpm, 100, Tune.RevLimitRpm, v => Tune.RevLimitRpm = v);
        AddSpin("Idle speed (rpm)", 600, 1500, 25, Tune.IdleRpm, v => Tune.IdleRpm = v);
        var knock = new CheckBox { Text = "Knock control enabled", ButtonPressed = Tune.KnockControlEnabled, Disabled = !hw.KnockControl };
        knock.Toggled += on => { Tune.KnockControlEnabled = on; State.Garage.TuneChanged(); };
        _left.AddChild(knock);

        var injectors = State.Garage.Engine.FindByCategory(PartCategory.Injectors)?.Spec<InjectorSpec>();
        AddSpin("Injector scaling (cc/min)", 100, 3000, 10, Tune.InjectorFlowCcMin, v => Tune.InjectorFlowCcMin = v);
        if (injectors != null && Math.Abs(injectors.FlowCcMin - Tune.InjectorFlowCcMin) > 1)
        {
            _left.AddChild(Ui.Wrapped($"Installed injectors flow {injectors.FlowCcMin:F0} cc/min: the ECU will meter the wrong amount of fuel.", 13, Ui.Danger));
            _left.AddChild(Ui.Button($"Set scaling to {injectors.FlowCcMin:F0} cc/min", () => { Tune.InjectorFlowCcMin = injectors.FlowCcMin; State.Garage.TuneChanged(); State.NotifyChanged(); }));
        }
        var fuel = State.Garage.Fuel;
        AddSpin("Fuel stoichiometric AFR", 5, 17, 0.1, Tune.FuelStoichAfr, v => Tune.FuelStoichAfr = v);
        if (Math.Abs(fuel.StoichiometricAfr - Tune.FuelStoichAfr) > 0.05)
        {
            _left.AddChild(Ui.Wrapped($"The tank holds {fuel.Name} (stoich {fuel.StoichiometricAfr:F1}:1): the mixture will be off by {fuel.StoichiometricAfr / Tune.FuelStoichAfr * 100 - 100:+0;-0} %.", 13, Ui.Danger));
            _left.AddChild(Ui.Button($"Calibrate for {fuel.Name}", () => { Tune.FuelStoichAfr = fuel.StoichiometricAfr; State.Garage.TuneChanged(); State.NotifyChanged(); }));
        }

        _left.AddChild(Ui.Heading("Base maps"));
        foreach (var doc in State.Content.Tunes.Values.OrderBy(t => t.Id))
            _left.AddChild(Ui.Button($"Load: {doc.Name}", () =>
            {
                State.Garage.Tune = EcuTune.FromDocument(doc);
                State.Garage.TuneChanged();
                State.NotifyChanged();
            }));
        _left.AddChild(Ui.Wrapped("Tips: MBT (best-torque timing) and the knock limit are shown on the dyno's spark graph. " +
            "Rich mixtures (λ 0.78–0.82) cool the chamber under boost; λ ≈ 0.88 makes best power naturally aspirated.", 13, Ui.Muted));
    }

    private void AddSpin(string label, double min, double max, double step, double value, Action<double> set)
    {
        var row = Ui.HBox();
        row.AddChild(Ui.Expand(Ui.Label(label, 14)));
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(120, 0) };
        s.ValueChanged += v => { set(v); State.Garage.TuneChanged(); };
        row.AddChild(s);
        _left.AddChild(row);
    }

    private void BuildGrid()
    {
        Ui.Clear(_grid);
        bool editable = State.Garage.Engine.FindByCategory(PartCategory.Ecu)?.Spec<EcuSpec>().TablesEditable ?? false;
        Table2D? table = _table.Selected switch { 0 => Tune.IgnitionAdvance, 1 => Tune.TargetLambda, _ => Tune.BoostTarget };
        _tableHelp.Text = _table.Selected switch
        {
            0 => "Rows: manifold pressure the ECU reads (kPa). Columns: rpm. More advance → more torque up to MBT, then knock and higher cylinder pressure.",
            1 => "Target λ per load/rpm. Below 1.0 is rich. The actual λ depends on the injectors, pump and calibration keeping up.",
            _ => Tune.BoostTarget == null ? "This tune has no boost table: boost is set by the wastegate spring." :
                 "Absolute manifold pressure target per rpm. Only used by ECUs with boost control, and never below the wastegate spring.",
        };
        if (table == null) return;
        _grid.Columns = table.Columns + 1;
        _grid.AddChild(Ui.Label(_table.Selected == 2 ? "" : "kPa \\ rpm", 12, Ui.Muted));
        for (int c = 0; c < table.Columns; c++) _grid.AddChild(Ui.Label($"{table.XAxis[c]:F0}", 12, Ui.Muted));
        double min = double.MaxValue, max = double.MinValue;
        for (int r = 0; r < table.Rows; r++) for (int c = 0; c < table.Columns; c++) { min = Math.Min(min, table[r, c]); max = Math.Max(max, table[r, c]); }
        for (int r = table.Rows - 1; r >= 0; r--)
        {
            _grid.AddChild(Ui.Label(_table.Selected == 2 ? "target" : $"{table.YAxis[r]:F0}", 12, Ui.Muted));
            for (int c = 0; c < table.Columns; c++)
            {
                int rr = r, cc = c;
                var spin = new SpinBox
                {
                    MinValue = _table.Selected switch { 0 => -20, 1 => 0.5, _ => 50 },
                    MaxValue = _table.Selected switch { 0 => 60, 1 => 1.6, _ => 400 },
                    Step = _table.Selected switch { 0 => 0.5, 1 => 0.01, _ => 5 },
                    Value = table[r, c],
                    Editable = editable,
                    CustomMinimumSize = new Vector2(64, 0),
                };
                spin.GetLineEdit().AddThemeFontSizeOverride("font_size", 12);
                float t = (float)((table[r, c] - min) / Math.Max(1e-9, max - min));
                spin.GetLineEdit().AddThemeColorOverride("font_color", new Color(0.55f + 0.45f * t, 0.85f - 0.3f * t, 1.0f - 0.6f * t));
                spin.ValueChanged += v => { table[rr, cc] = v; State.Garage.TuneChanged(); };
                _grid.AddChild(spin);
            }
        }
    }
}
