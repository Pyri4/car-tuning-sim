using System;
using System.Linq;
using CarSim.Core.Common;
using CarSim.Core.Ecu;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using Godot;

namespace CarTuningSim.UI;

/// <summary>ECU hardware limits, calibration values and editable fuel (VE, λ)/ignition/boost tables.</summary>
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
        _table.AddItem("Volumetric efficiency (fuel map)");
        _table.AddItem("Boost target (kPa absolute)");
        _table.AddItem("Intake cam advance (° crank)");
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
        var caps = CarSim.Core.Engines.EngineCapabilities.Resolve(State.Garage.Engine);
        if (caps.IntakeCamPhasers)
        {
            double phaserRange = State.Garage.Engine.PartsOf(PartCategory.Camshafts).Max(p => p.Part.Spec<CamshaftSpec>().IntakePhaserRangeDeg);
            _left.AddChild(Ui.Label(hw.CamPhaseControl ? $"Cam phasing: intake phaser, {phaserRange:F0}° of advance"
                : "Cam phasing: this ECU cannot drive the intake phaser (it stays parked)", 13, hw.CamPhaseControl ? Ui.Muted : Ui.Caution));
        }
        if (caps.VariableLiftCams)
            _left.AddChild(Ui.Label(hw.ValveLiftControl ? "Variable valve lift: two cam profiles, switched by engine speed"
                : "Variable valve lift: this ECU cannot switch the cams (base profile only)", 13, hw.ValveLiftControl ? Ui.Muted : Ui.Caution));
        int runnerStages = State.Garage.Engine.PartsOf(PartCategory.IntakeManifold).Max(p => p.Part.Spec<IntakeManifoldSpec>().AllStages().Count);
        if (caps.SwitchedRunners)
            _left.AddChild(Ui.Label(hw.IntakeRunnerControl ? $"Variable intake: {runnerStages} runner stages, switched by engine speed"
                : "Variable intake: this ECU cannot switch the runners (primary only)", 13, hw.IntakeRunnerControl ? Ui.Muted : Ui.Caution));
        _left.AddChild(Ui.Label($"Max rev limit: {hw.MaxRevLimitRpm:F0} rpm", 13, Ui.Muted));

        _left.AddChild(Ui.Heading("Calibration"));
        _left.AddChild(Ui.Label($"Tune: {Tune.Name}", 13, Ui.Muted));
        AddSpin("Rev limit (rpm)", 3000, hw.MaxRevLimitRpm, 100, Tune.RevLimitRpm, v => Tune.RevLimitRpm = v);
        AddSpin("Idle speed (rpm)", 600, 1500, 25, Tune.IdleRpm, v => Tune.IdleRpm = v);
        if (caps.VariableValveLift)
            AddSpin("Valve-lift switch (rpm)", 1000, hw.MaxRevLimitRpm, 50, Tune.ValveLiftSwitchRpm ?? hw.MaxRevLimitRpm, v => Tune.ValveLiftSwitchRpm = v);
        if (caps.VariableIntakeRunner)
        {
            AddSpin(runnerStages > 2 ? "Runner stage 2 from (rpm)" : "Intake runner switch (rpm)", 1000, hw.MaxRevLimitRpm, 50,
                Tune.IntakeRunnerSwitchRpm ?? hw.MaxRevLimitRpm, v => Tune.IntakeRunnerSwitchRpm = v);
            for (int k = 2; k < runnerStages; k++)
            {
                int index = k - 2;
                AddSpin($"Runner stage {k + 1} from (rpm)", 1000, hw.MaxRevLimitRpm, 50, Tune.IntakeRunnerSwitchRpmOf(k) ?? hw.MaxRevLimitRpm, v =>
                {
                    var upper = Tune.IntakeRunnerUpperSwitchRpm ?? Array.Empty<double>();
                    if (upper.Length <= index) upper = upper.Concat(Enumerable.Repeat(hw.MaxRevLimitRpm, index + 1 - upper.Length)).ToArray();
                    upper[index] = v;
                    Tune.IntakeRunnerUpperSwitchRpm = upper;
                });
            }
        }
        var knock = new CheckBox { Text = "Knock control enabled", ButtonPressed = Tune.KnockControlEnabled, Disabled = !hw.KnockControl };
        knock.Toggled += on => { Tune.KnockControlEnabled = on; State.Garage.TuneChanged(); };
        _left.AddChild(knock);

        var injectors = State.Garage.Engine.FindByCategory(PartCategory.Injectors)?.Spec<InjectorSpec>();
        AddSpin("Injector scaling (cc/min)", 100, 3000, 10, Tune.InjectorFlowCcMin, v => Tune.InjectorFlowCcMin = v);
        AddSpin("Engine displacement (cc)", 50, 20000, 1, Tune.DisplacementCc, v => Tune.DisplacementCc = v);
        var geometry = CarSim.Core.Engines.EngineGeometry.TryCreate(State.Garage.Engine, out _);
        if (geometry != null && Math.Abs(Units.M3ToCc(geometry.Displacement) - Tune.DisplacementCc) > 5)
            _left.AddChild(Ui.Wrapped($"The engine now displaces {Units.M3ToCc(geometry.Displacement):F0} cc: the fuel map's air estimate is off by " +
                $"{Units.M3ToCc(geometry.Displacement) / Tune.DisplacementCc * 100 - 100:+0;-0} % until this or the VE table is changed.", 13, Ui.Caution));
        if (injectors != null && Math.Abs(injectors.FlowCcMin - Tune.InjectorFlowCcMin) > 1)
        {
            _left.AddChild(Ui.Wrapped($"Installed injectors flow {injectors.FlowCcMin:F0} cc/min: the ECU will meter the wrong amount of fuel.", 13, Ui.Danger));
            _left.AddChild(Ui.Button($"Set scaling to {injectors.FlowCcMin:F0} cc/min", () => { Tune.InjectorFlowCcMin = injectors.FlowCcMin; State.Garage.TuneChanged(); State.NotifyChanged(); }));
        }
        AddSpin("Injector dead time (ms)", 0, 3, 0.01, Tune.InjectorDeadTimeMs, v => Tune.InjectorDeadTimeMs = v);
        if (injectors != null && Math.Abs(injectors.DeadTimeMs - Tune.InjectorDeadTimeMs) > 0.02)
        {
            _left.AddChild(Ui.Wrapped($"The installed injectors' data sheet gives {injectors.DeadTimeMs:F2} ms dead time: short pulses (idle, light load) will be metered wrong.", 13, Ui.Caution));
            _left.AddChild(Ui.Button($"Set dead time to {injectors.DeadTimeMs:F2} ms", () => { Tune.InjectorDeadTimeMs = injectors.DeadTimeMs; State.Garage.TuneChanged(); State.NotifyChanged(); }));
        }
        var fuel = State.Garage.Fuel;
        AddSpin("Fuel stoichiometric AFR", 5, 17, 0.1, Tune.FuelStoichAfr, v => Tune.FuelStoichAfr = v);
        AddSpin("Fuel density (kg/L)", 0.5, 1.2, 0.001, Tune.FuelDensityKgL, v => Tune.FuelDensityKgL = v);
        double mixtureError = fuel.StoichiometricAfr / Tune.FuelStoichAfr * fuel.DensityKgL / Tune.FuelDensityKgL - 1.0;
        if (Math.Abs(fuel.StoichiometricAfr - Tune.FuelStoichAfr) > 0.05 || Math.Abs(fuel.DensityKgL - Tune.FuelDensityKgL) > 0.004)
        {
            _left.AddChild(Ui.Wrapped($"The tank holds {fuel.Name} (stoich {fuel.StoichiometricAfr:F1}:1, {fuel.DensityKgL:F3} kg/L): the mixture will be off by {mixtureError * 100:+0;-0} %.", 13, Ui.Danger));
            _left.AddChild(Ui.Button($"Calibrate for {fuel.Name}", () => { Tune.FuelStoichAfr = fuel.StoichiometricAfr; Tune.FuelDensityKgL = fuel.DensityKgL; State.Garage.TuneChanged(); State.NotifyChanged(); }));
        }

        _left.AddChild(Ui.Heading("Base maps"));
        foreach (var doc in State.Content.Tunes.Values.OrderBy(t => t.Id))
            _left.AddChild(Ui.Button($"Load: {doc.Name}", () =>
            {
                State.Garage.Tune = EcuTune.FromDocument(doc);
                State.Garage.TuneChanged();
                State.NotifyChanged();
            }));
        _left.AddChild(Ui.Wrapped("Tips: the ECU fuels from its VE table — after changing cams, head, exhaust or turbo, hold each load point " +
            "on the dyno and scale the VE cell by measured λ ÷ target λ until the wideband agrees. Find best-torque timing by " +
            "adding advance until torque stops rising; back off if the knock sensor hears anything. " +
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
        const int boostTable = 3;
        Table2D? table = _table.Selected switch { 0 => Tune.IgnitionAdvance, 1 => Tune.TargetLambda, 2 => Tune.VolumetricEfficiency, 3 => Tune.BoostTarget, _ => Tune.IntakeCamAdvance };
        _tableHelp.Text = _table.Selected switch
        {
            0 => "Rows: manifold pressure the ECU reads (kPa). Columns: rpm. More advance → more torque up to MBT, then knock and higher cylinder pressure.",
            1 => "Target λ per load/rpm. Below 1.0 is rich. The actual λ depends on the VE table being right and the injectors and pump keeping up.",
            2 => "Speed-density fuel map: how well the engine fills relative to the manifold pressure and air temperature the ECU reads. " +
                 "Fuel = VE × MAP × displacement ÷ (R × IAT) ÷ (target λ × stoich AFR). A cell 5 % too low runs 5 % lean there.",
            3 => Tune.BoostTarget == null ? "This tune has no boost table: boost is set by the wastegate spring." :
                 "Absolute manifold pressure target per rpm. Only used by ECUs with boost control, and never below the wastegate spring.",
            _ => Tune.IntakeCamAdvance == null ? "This tune has no cam table: a cam phaser stays at its park position." :
                 "How far the ECU advances the intake cam from its park position. Earlier intake closing fills better at low rpm, " +
                 "later closing at high rpm; the VE table was measured with this schedule, so changing it moves λ too.",
        };
        if (table == null) return;
        _grid.Columns = table.Columns + 1;
        _grid.AddChild(Ui.Label(_table.Selected == boostTable ? "" : "kPa \\ rpm", 12, Ui.Muted));
        for (int c = 0; c < table.Columns; c++) _grid.AddChild(Ui.Label($"{table.XAxis[c]:F0}", 12, Ui.Muted));
        double min = double.MaxValue, max = double.MinValue;
        for (int r = 0; r < table.Rows; r++) for (int c = 0; c < table.Columns; c++) { min = Math.Min(min, table[r, c]); max = Math.Max(max, table[r, c]); }
        for (int r = table.Rows - 1; r >= 0; r--)
        {
            _grid.AddChild(Ui.Label(_table.Selected == boostTable ? "target" : $"{table.YAxis[r]:F0}", 12, Ui.Muted));
            for (int c = 0; c < table.Columns; c++)
            {
                int rr = r, cc = c;
                var spin = new SpinBox
                {
                    MinValue = _table.Selected switch { 0 => -20, 1 => 0.5, 2 => 0.05, 3 => 50, _ => 0 },
                    MaxValue = _table.Selected switch { 0 => 60, 1 => 1.6, 2 => 3.0, 3 => 400, _ => 80 },
                    Step = _table.Selected switch { 0 => 0.5, 1 => 0.01, 2 => 0.005, 3 => 5, _ => 0.5 },
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
