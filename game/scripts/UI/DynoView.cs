using System;
using System.Collections.Generic;
using System.Linq;
using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Simulation;
using Godot;

namespace CarTuningSim.UI;

/// <summary>The player's laboratory: run pulls, watch telemetry live, compare runs, read failure reports.</summary>
public partial class DynoView : HSplitContainer
{
    private static readonly Color[] RunColors =
    {
        new(0.40f, 0.70f, 1.00f), new(0.60f, 0.90f, 0.50f), new(0.85f, 0.55f, 0.95f), new(0.95f, 0.75f, 0.35f),
    };

    private static readonly string[] Channels =
    {
        "Boost & λ", "Spark advance & knock sensor", "Temperatures (EGT, coolant, oil, intake air)",
        "Oil pressure vs requirement", "Volumetric efficiency & injector duty", "Turbo: shaft rpm & wastegate",
        "Peak cylinder pressure & rod load",
    };

    private OptionButton _mode = null!, _cooling = null!, _speed = null!, _channel = null!;
    private SpinBox _start = null!, _end = null!, _ramp = null!;
    private Button _run = null!, _stop = null!;
    private LineGraph _powerGraph = null!, _auxGraph = null!;
    private GridContainer _gauges = null!;
    private VBoxContainer _warnings = null!, _runList = null!;
    private RichTextLabel _failure = null!;
    private Label _status = null!;
    private DynoRunner? _runner;
    private EngineSimulation? _sim;
    private readonly Dictionary<string, Label> _gaugeValues = new();

    private static GameState State => GameState.Instance;

    public bool AutoRun { get; set; }
    public double? AutoEndRpm { get; set; }

    public override void _Ready()
    {
        SplitOffsets = new[] { 1150 };
        var main = Ui.VBox(8);

        // Wraps onto a second line on narrow windows instead of pushing the telemetry column off-screen.
        var controls = new HFlowContainer();
        controls.AddThemeConstantOverride("h_separation", 10);
        controls.AddThemeConstantOverride("v_separation", 6);
        _mode = new OptionButton();
        _mode.AddItem("Sweep (ramp)");
        _mode.AddItem("Steady state");
        controls.AddChild(Ui.Label("Test:"));
        controls.AddChild(_mode);
        _start = Spin(1000, 9000, 250, 2000);
        _end = Spin(2000, 12000, 250, State.Garage.Tune.RevLimitRpm);
        _ramp = Spin(100, 3000, 50, 500);
        controls.AddChild(Ui.Label("From"));
        controls.AddChild(_start);
        controls.AddChild(Ui.Label("to"));
        controls.AddChild(_end);
        controls.AddChild(Ui.Label("rpm, ramp"));
        controls.AddChild(_ramp);
        controls.AddChild(Ui.Label("rpm/s"));
        _cooling = new OptionButton();
        _cooling.AddItem("Test-cell coolant (90 °C)");
        _cooling.AddItem("Engine radiator + dyno fan");
        controls.AddChild(_cooling);
        _speed = new OptionButton();
        foreach (var s in new[] { "×1 real time", "×4", "×16", "Instant" }) _speed.AddItem(s);
        _speed.Selected = 1;
        controls.AddChild(_speed);
        _run = Ui.Button("▶ Run", StartRun);
        _stop = Ui.Button("■ Stop", () => { _runner = null; _status.Text = "Stopped."; });
        controls.AddChild(_run);
        controls.AddChild(_stop);
        controls.AddChild(Ui.Button("Clear runs", () => { State.Runs.Clear(); RefreshGraphs(); RefreshRunList(); }));
        main.AddChild(controls);

        _status = Ui.Label("Ready. Set up a test and press Run.", 14, Ui.Muted);
        main.AddChild(_status);

        _powerGraph = new LineGraph { LeftLabel = "Torque (N·m)", RightLabel = "Power (hp)" };
        _powerGraph.SizeFlagsVertical = SizeFlags.ExpandFill;
        _powerGraph.SizeFlagsStretchRatio = 1.4f;
        main.AddChild(_powerGraph);

        var auxBar = Ui.HBox();
        auxBar.AddChild(Ui.Label("Second graph:"));
        _channel = new OptionButton();
        foreach (var c in Channels) _channel.AddItem(c);
        _channel.ItemSelected += _ => RefreshGraphs();
        auxBar.AddChild(_channel);
        main.AddChild(auxBar);
        _auxGraph = new LineGraph();
        _auxGraph.SizeFlagsVertical = SizeFlags.ExpandFill;
        main.AddChild(_auxGraph);
        AddChild(Ui.Margin(main, 8));

        var side = Ui.VBox(8);
        side.CustomMinimumSize = new Vector2(380, 0);
        side.AddChild(Ui.Heading("Live telemetry"));
        _gauges = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _gauges.AddThemeConstantOverride("h_separation", 16);
        foreach (var name in new[] { "Engine speed", "Torque", "Power", "Manifold pressure", "Boost", "λ / AFR", "Injector duty",
                     "Spark advance", "Intake cam", "Knock", "Peak cyl. pressure", "EGT", "Coolant", "Oil", "Intake air temp", "Turbo" })
        {
            _gauges.AddChild(Ui.Label(name, 14, Ui.Muted));
            var v = Ui.Label("—", 14);
            v.ClipText = true;
            v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _gaugeValues[name] = v;
            _gauges.AddChild(v);
        }
        side.AddChild(_gauges);
        side.AddChild(Ui.Heading("Warnings"));
        _warnings = Ui.VBox(2);
        side.AddChild(_warnings);
        side.AddChild(Ui.Heading("Runs"));
        _runList = Ui.VBox(2);
        side.AddChild(_runList);
        _failure = new RichTextLabel { BbcodeEnabled = false, FitContent = true, ScrollActive = false, Visible = false };
        _failure.AddThemeColorOverride("default_color", Ui.Danger);
        side.AddChild(_failure);
        AddChild(Ui.Scroll(Ui.Margin(side, 8)));

        RefreshGraphs();
        RefreshRunList();
        if (AutoEndRpm is double end) _end.Value = end;
        if (AutoRun)
        {
            _speed.Selected = 3;
            CallDeferred(nameof(StartRun));
        }
    }

    private static SpinBox Spin(double min, double max, double step, double value)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value };
        s.CustomMinimumSize = new Vector2(96, 0);
        return s;
    }

    private void StartRun()
    {
        var (sim, report) = State.Garage.CreateSimulation();
        _failure.Visible = false;
        if (sim == null)
        {
            _status.Text = "The engine cannot run: " + string.Join(" ", report.Errors.Select(e => e.Message));
            _status.AddThemeColorOverride("font_color", Ui.Danger);
            return;
        }
        _status.RemoveThemeColorOverride("font_color");
        _sim = sim;
        var settings = new DynoSettings
        {
            Mode = _mode.Selected == 0 ? DynoMode.Sweep : DynoMode.SteadyState,
            StartRpm = _start.Value,
            EndRpm = Math.Max(_start.Value + 250, _end.Value),
            RampRpmPerSecond = _ramp.Value,
            StepRpm = 250,
            SettleSeconds = 1.0,
            CoolantTemperatureK = _cooling.Selected == 0 ? 363.15 : null,
        };
        _runner = new DynoRunner(sim, settings, $"Run {State.Runs.Count + 1}");
        _status.Text = $"{_runner.Name}: stabilising at {settings.StartRpm:F0} rpm…";
        if (_speed.Selected == 3) FinishRun(_runner.RunToCompletion());
    }

    public override void _Process(double delta)
    {
        if (_runner == null) return;
        double factor = _speed.Selected switch { 0 => 1, 1 => 4, 2 => 16, _ => 1000 };
        _runner.Advance(Math.Min(delta, 0.1) * factor);
        if (_runner.Current is { } t) UpdateGauges(t);
        if (_sim != null) UpdateWarnings(_sim.Damage.Warnings);
        _status.Text = $"{_runner.Name}: {_runner.Phase} — {_runner.Current?.Rpm:F0} rpm ({_runner.Progress * 100:F0} %)";
        RefreshGraphs(_runner.Samples, _runner.Current?.Rpm);
        if (_runner.IsDone) FinishRun(_runner.Result());
    }

    private void FinishRun(DynoRun run)
    {
        _runner = null;
        var last = run.Samples.Count > 0 ? run.Samples[^1] : null;
        if (last != null) UpdateGauges(last);
        if (_sim != null) UpdateWarnings(_sim.Damage.Warnings);
        if (run.Samples.Count > 0) State.Runs.Add(run);
        State.FailureReports.AddRange(run.Failures);
        var prev = State.Runs.Count >= 2 ? State.Runs[^2] : null;
        string summary = run.PeakPower is { } pp
            ? $"{run.Name}: {pp.PowerHp:F1} hp @ {pp.Rpm:F0} rpm, {run.PeakTorque!.Torque:F1} N·m @ {run.PeakTorque.Rpm:F0} rpm"
            : $"{run.Name}: no data";
        if (prev != null && run.PeakPower != null && prev.PeakPower != null)
        {
            var cmp = DynoComparison.Compare(prev, run);
            summary += $"   (vs {prev.Name}: {Units.WToHp(cmp.PeakPowerDeltaW):+0.0;-0.0} hp peak, {cmp.AreaUnderPowerDeltaPercent:+0.0;-0.0} % area)";
        }
        if (!run.Completed) summary += $"   ABORTED: {run.AbortReason}";
        else if (run.Note.Length > 0) summary += $"   {run.Note}";
        _status.Text = summary;
        if (run.Failures.Count > 0)
        {
            _failure.Visible = true;
            _failure.Text = string.Join("\n\n", run.Failures.Select(f => f.ToText()));
        }
        RefreshGraphs();
        RefreshRunList();
        State.NotifyChanged();
    }

    private void UpdateGauges(EngineTelemetry t)
    {
        void Set(string name, string text, Color? color = null)
        {
            var l = _gaugeValues[name];
            l.Text = text;
            if (color is { } c) l.AddThemeColorOverride("font_color", c); else l.RemoveThemeColorOverride("font_color");
        }
        Set("Engine speed", $"{t.Rpm:F0} rpm" + (t.RevLimiterActive ? "  LIMITER" : ""), t.RevLimiterActive ? Ui.Caution : null);
        Set("Torque", $"{t.Torque:F1} N·m  ({t.TorqueLbFt:F0} lb·ft)");
        Set("Power", $"{t.PowerHp:F1} hp  ({t.PowerKw:F1} kW)");
        Set("Manifold pressure", $"{t.MapKpa:F0} kPa" + (t.MapSensorSaturated ? "  (ECU reads " + Units.PaToKpa(t.EcuMapReading).ToString("F0") + ")" : ""), t.MapSensorSaturated ? Ui.Danger : null);
        Set("Boost", $"{t.BoostKpa:F0} kPa ({Units.PaToPsi(t.BoostPressure):F1} psi)");
        Set("λ / AFR", t.Firing ? $"{t.Lambda:F2} / {t.Afr:F1}  (target {t.TargetLambda:F2})" : "—", t.Firing && t.Lambda > t.TargetLambda + 0.07 ? Ui.Danger : null);
        Set("Injector duty", $"{t.InjectorDuty * 100:F0} %" + (t.FuelLimit != FuelLimit.None ? $"  {t.FuelLimit}" : ""), t.InjectorDuty > 0.9 || t.FuelLimit != FuelLimit.None ? Ui.Danger : t.InjectorDuty > 0.8 ? Ui.Caution : null);
        Set("Spark advance", $"{t.IgnitionAdvance:F1}° BTDC" + (t.KnockRetard > 0.05 ? $"  (knock retard {t.KnockRetard:F1}°)" : ""));
        Set("Intake cam", t.IntakeCamAdvance > 0.05 ? $"{t.IntakeCamAdvance:F1}° advanced" : "parked");
        Set("Knock", KnockSensor.Describe(t.KnockSensorLevel), t.KnockSensorLevel >= KnockLevel.Light ? Ui.Danger : t.KnockSensorLevel > KnockLevel.None ? Ui.Caution : null);
        Set("Peak cyl. pressure", $"{t.PeakCylinderPressureBar:F0} bar");
        Set("EGT", $"{t.EgtC:F0} °C", t.EgtC > 950 ? Ui.Caution : null);
        Set("Coolant", $"{t.CoolantC:F0} °C" + (t.CoolantLevel < 0.99 ? $"  level {t.CoolantLevel * 100:F0} %" : ""), t.CoolantC > 110 ? Ui.Danger : t.CoolantC > 100 ? Ui.Caution : null);
        Set("Oil", $"{t.OilC:F0} °C, {t.OilPressureBar:F2} bar (need {Units.PaToBar(t.OilPressureRequired):F2})", t.OilPressure < t.OilPressureRequired ? Ui.Danger : null);
        Set("Intake air temp", $"{Units.KToC(t.ManifoldTemperature):F0} °C");
        Set("Turbo", t.TurboRpm > 1 ? $"{t.TurboRpm / 1000:F0} krpm, PR {t.CompressorPressureRatio:F2}, η {t.CompressorEfficiency * 100:F0} %, WG {t.WastegateOpening * 100:F0} %" : "—", t.TurboOverspeed ? Ui.Danger : null);
    }

    private void UpdateWarnings(IReadOnlyList<EngineWarning> warnings)
    {
        Ui.Clear(_warnings);
        if (warnings.Count == 0) { _warnings.AddChild(Ui.Label("None", 13, Ui.Muted)); return; }
        foreach (var w in warnings.Take(8))
            _warnings.AddChild(Ui.Wrapped(w.Message, 13, w.Level == WarningLevel.Danger ? Ui.Danger : Ui.Caution));
    }

    private void RefreshRunList()
    {
        Ui.Clear(_runList);
        if (State.Runs.Count == 0) { _runList.AddChild(Ui.Label("No runs yet.", 13, Ui.Muted)); return; }
        for (int i = 0; i < State.Runs.Count; i++)
        {
            var run = State.Runs[i];
            string text = run.PeakPower is { } pp
                ? $"{run.Name}: {pp.PowerHp:F1} hp @ {pp.Rpm:F0}, {run.PeakTorque!.Torque:F0} N·m ({run.FuelName})"
                : $"{run.Name}: no data";
            if (!run.Completed) text += " — aborted";
            _runList.AddChild(Ui.Wrapped(text, 13, RunColors[i % RunColors.Length]));
        }
    }

    private static List<Vector2> Pts(IEnumerable<EngineTelemetry> samples, Func<EngineTelemetry, double> f) =>
        samples.Where(s => s.Firing).Select(s => new Vector2((float)s.Rpm, (float)f(s))).ToList();

    private void RefreshGraphs(IReadOnlyList<EngineTelemetry>? live = null, double? cursor = null)
    {
        _powerGraph.SeriesList.Clear();
        _auxGraph.SeriesList.Clear();
        var runs = State.Runs.Select((r, i) => (Samples: r.Samples, Name: r.Name, Color: RunColors[i % RunColors.Length], Live: false)).ToList();
        if (live != null) runs.Add((live, "Live", Ui.Accent, true));
        foreach (var (samples, name, color, isLive) in runs)
        {
            _powerGraph.SeriesList.Add(new LineGraph.Series { Name = $"{name} torque", Color = color, Points = Pts(samples, s => s.Torque), Width = isLive ? 3 : 2 });
            _powerGraph.SeriesList.Add(new LineGraph.Series { Name = $"{name} power", Color = color.Lightened(0.35f), RightAxis = true, Dashed = true, Points = Pts(samples, s => s.PowerHp), Width = isLive ? 3 : 2 });
            AddAux(samples, name, color);
        }
        _powerGraph.XMin = (float)_start.Value;
        _powerGraph.XMax = (float)_end.Value;
        _powerGraph.LeftMin = 0;
        _powerGraph.RightMin = 0;
        _powerGraph.CursorX = cursor is double c ? (float)c : null;
        _auxGraph.XMin = _powerGraph.XMin;
        _auxGraph.XMax = _powerGraph.XMax;
        _auxGraph.CursorX = _powerGraph.CursorX;
        _powerGraph.QueueRedraw();
        _auxGraph.QueueRedraw();
    }

    /// <summary>
    /// What a knock sensor tells a tuner: how hard it is knocking, not how far the timing is from the limit
    /// (MBT and the knock limit are found by experiment; the CLI's hold/sweep still print them for development).
    /// </summary>
    private void AddAux(IReadOnlyList<EngineTelemetry> s, string name, Color color)
    {
        var g = _auxGraph;
        g.LeftMin = g.LeftMax = g.RightMin = g.RightMax = null;
        void L(string n, Func<EngineTelemetry, double> f, Color c, bool dashed = false) => g.SeriesList.Add(new LineGraph.Series { Name = $"{name} {n}", Color = c, Points = Pts(s, f), Dashed = dashed });
        void R(string n, Func<EngineTelemetry, double> f, Color c, bool dashed = false) => g.SeriesList.Add(new LineGraph.Series { Name = $"{name} {n}", Color = c, Points = Pts(s, f), RightAxis = true, Dashed = dashed });
        switch (_channel.Selected)
        {
            case 0:
                g.LeftLabel = "Boost (kPa)"; g.RightLabel = "λ";
                L("boost", t => t.BoostKpa, color); R("λ", t => t.Lambda, color.Lightened(0.4f), true); R("λ target", t => t.TargetLambda, Ui.Muted, true);
                g.RightMin = 0.6f; g.RightMax = 1.3f;
                break;
            case 1:
                g.LeftLabel = "Degrees BTDC"; g.RightLabel = "Knock sensor (0 quiet – 4 heavy)";
                L("advance", t => t.IgnitionAdvance, color); L("knock retard", t => t.KnockRetard, Ui.Danger, true);
                R("knock", t => (double)t.KnockSensorLevel, Ui.Caution);
                break;
            case 2:
                g.LeftLabel = "°C"; g.RightLabel = "";
                L("EGT", t => t.EgtC, color); L("coolant", t => t.CoolantC, Ui.Good, true); L("oil", t => t.OilC, Ui.Caution, true); L("intake air", t => Units.KToC(t.ManifoldTemperature), Ui.Muted, true);
                g.LeftMin = 0;
                break;
            case 3:
                g.LeftLabel = "bar"; g.RightLabel = "";
                L("oil pressure", t => t.OilPressureBar, color); L("required", t => Units.PaToBar(t.OilPressureRequired), Ui.Danger, true);
                g.LeftMin = 0;
                break;
            case 4:
                g.LeftLabel = "VE"; g.RightLabel = "Injector duty";
                L("VE", t => t.VolumetricEfficiency, color); R("duty", t => t.InjectorDuty, Ui.Caution, true);
                g.LeftMin = 0; g.RightMin = 0; g.RightMax = 1.2f;
                break;
            case 5:
                g.LeftLabel = "Turbo krpm"; g.RightLabel = "Wastegate (0–1)";
                L("turbo", t => t.TurboRpm / 1000, color); R("wastegate", t => t.WastegateOpening, Ui.Muted, true);
                g.LeftMin = 0; g.RightMin = 0; g.RightMax = 1;
                break;
            case 6:
                g.LeftLabel = "Peak cylinder pressure (bar)"; g.RightLabel = "Rod tensile load (kN)";
                L("PCP", t => t.PeakCylinderPressureBar, color); R("rod load", t => t.RodTensileLoad / 1000, Ui.Caution, true);
                g.LeftMin = 0; g.RightMin = 0;
                break;
        }
    }
}
