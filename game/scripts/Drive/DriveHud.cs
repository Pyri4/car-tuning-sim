using System;
using System.Linq;
using CarSim.Core.Damage;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;
using CarTuningSim.UI;
using Godot;

namespace CarTuningSim.Drive;

/// <summary>Driving overlay: speed, gear, rev bar, lap times, engine gauges, tyre usage, warnings and failures.</summary>
public partial class DriveHud : Control
{
    private Label _speed = null!, _gear = null!, _rpm = null!, _laps = null!, _gauges = null!, _status = null!, _help = null!;
    private ProgressBar _revBar = null!;
    private VBoxContainer _warnings = null!;
    private readonly StyleBoxFlat[] _tyreBoxes = new StyleBoxFlat[4];
    private readonly Label[] _tyreLabels = new Label[4];
    private PanelContainer _failurePanel = null!;
    private Label _failureText = null!;
    private double _redline;

    public event Action? BackToGarage;
    public event Action? DismissFailure;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // Top left: laps.
        _laps = Ui.Label("", 20);
        _laps.AddThemeConstantOverride("outline_size", 6);
        _laps.AddThemeColorOverride("font_outline_color", Colors.Black);
        AddChild(Place(Panel(_laps), 0, 0, new Vector2(16, 16), GrowDirection.End, GrowDirection.End));

        // Top centre: status line and warnings.
        var centre = Ui.VBox(4);
        _status = Ui.Label("", 18, Ui.Accent);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        centre.AddChild(_status);
        _warnings = Ui.VBox(2);
        centre.AddChild(_warnings);
        centre.CustomMinimumSize = new Vector2(640, 0);
        AddChild(Place(centre, 0.5f, 0, new Vector2(0, 16), GrowDirection.Both, GrowDirection.End));

        // Top right: tyres.
        var tyreBox = Ui.VBox(4);
        tyreBox.AddChild(Ui.Label("Tyres  °C / kPa", 14, Ui.Muted));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 18);
        for (int w = 0; w < 4; w++)
        {
            // Box colour = how hard the tyre is working; text = tread temperature and hot pressure.
            _tyreBoxes[w] = new StyleBoxFlat { BgColor = Ui.Good };
            _tyreBoxes[w].SetCornerRadiusAll(3);
            _tyreBoxes[w].SetContentMarginAll(4);
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(58, 44) };
            panel.AddThemeStyleboxOverride("panel", _tyreBoxes[w]);
            _tyreLabels[w] = Ui.Label("", 13, Colors.Black);
            _tyreLabels[w].HorizontalAlignment = HorizontalAlignment.Center;
            panel.AddChild(_tyreLabels[w]);
            grid.AddChild(panel);
        }
        tyreBox.AddChild(grid);
        AddChild(Place(Panel(tyreBox), 1, 0, new Vector2(-16, 16), GrowDirection.Begin, GrowDirection.End));

        // Bottom left: engine gauges.
        _gauges = Ui.Label("", 15);
        AddChild(Place(Panel(_gauges), 0, 1, new Vector2(16, -16), GrowDirection.End, GrowDirection.Begin));

        // Bottom right: speed, gear, revs.
        var dash = Ui.VBox(2);
        var row = Ui.HBox(18);
        _speed = Ui.Label("0", 54);
        _gear = Ui.Label("N", 54, Ui.Accent);
        row.AddChild(_speed);
        row.AddChild(Ui.Label("km/h", 16, Ui.Muted));
        row.AddChild(_gear);
        dash.AddChild(row);
        _revBar = new ProgressBar { MinValue = 0, MaxValue = 9000, ShowPercentage = false, CustomMinimumSize = new Vector2(320, 16) };
        dash.AddChild(_revBar);
        _rpm = Ui.Label("", 15, Ui.Muted);
        dash.AddChild(_rpm);
        AddChild(Place(Panel(dash), 1, 1, new Vector2(-16, -16), GrowDirection.Begin, GrowDirection.Begin));

        // Bottom centre: controls.
        _help = Ui.Label("W/↑ throttle   S/↓ brake   A D/← → steer   E/Shift up   Q/Ctrl down   Space handbrake   T starter\n"
                         + "R recover to track   P autopilot   C camera   H hide help   Esc back to garage", 13, Ui.Muted);
        _help.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(Place(_help, 0.5f, 1, new Vector2(0, -12), GrowDirection.Both, GrowDirection.Begin));

        // Failure overlay.
        var box = Ui.VBox(10);
        box.AddChild(Ui.Label("MECHANICAL FAILURE", 22, Ui.Danger));
        _failureText = Ui.Wrapped("", 14);
        var scroll = Ui.Scroll(_failureText);
        scroll.CustomMinimumSize = new Vector2(760, 420);
        box.AddChild(scroll);
        var buttons = Ui.HBox();
        buttons.AddChild(Ui.Button("Back to the garage", () => BackToGarage?.Invoke()));
        buttons.AddChild(Ui.Button("Keep watching", () => { _failurePanel.Visible = false; DismissFailure?.Invoke(); }));
        box.AddChild(buttons);
        _failurePanel = Panel(box);
        _failurePanel.Visible = false;
        AddChild(Place(_failurePanel, 0.5f, 0.5f, Vector2.Zero, GrowDirection.Both, GrowDirection.Both));
    }

    private static PanelContainer Panel(Control child)
    {
        var p = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.11f, 0.78f) };
        sb.SetCornerRadiusAll(6);
        sb.SetContentMarginAll(10);
        p.AddThemeStyleboxOverride("panel", sb);
        p.AddChild(child);
        return p;
    }

    /// <summary>Pins <paramref name="control"/> to an anchor point (fractions of the screen) plus an offset; it grows away from it.</summary>
    private static T Place<T>(T control, float ax, float ay, Vector2 offset, GrowDirection growH, GrowDirection growV) where T : Control
    {
        control.AnchorLeft = control.AnchorRight = ax;
        control.AnchorTop = control.AnchorBottom = ay;
        control.OffsetLeft = control.OffsetRight = offset.X;
        control.OffsetTop = control.OffsetBottom = offset.Y;
        control.GrowHorizontal = growH;
        control.GrowVertical = growV;
        return control;
    }

    public void ToggleHelp() => _help.Visible = !_help.Visible;

    public void SetRedline(double rpm)
    {
        _redline = rpm;
        _revBar.MaxValue = Math.Ceiling((rpm + 500) / 1000) * 1000;
    }

    public void ShowFailure(FailureReport report)
    {
        _failureText.Text = report.ToText();
        _failurePanel.Visible = true;
    }

    public void UpdateFrom(DrivingSession session, string cameraName)
    {
        var t = session.Last;
        var sim = session.Sim;
        if (t == null) return;
        _speed.Text = $"{t.SpeedKmh,3:F0}";
        _gear.Text = t.GearLabel;
        _revBar.Value = t.EngineRpm;
        bool nearRedline = t.EngineRpm > _redline - 400;
        _revBar.Modulate = nearRedline ? Ui.Danger : t.EngineRpm > _redline - 1500 ? Ui.Caution : Colors.White;
        _rpm.Text = $"{t.EngineRpm:F0} rpm   ·   clutch {t.Clutch * 100:F0} %{(Math.Abs(t.ClutchSlipRpm) > 200 && t.Clutch > 0.05 && t.Clutch < 0.98 ? "  slipping" : "")}";

        var timer = session.Timer;
        string current = timer.Timing ? Time(timer.CurrentLapTime(t.Time)) : "out lap";
        _laps.Text = $"Lap {timer.Laps + 1}   {current}\nLast  {(timer.LastLap is double last ? Time(last) : "--:--.---")}\nBest  {(timer.BestLap is double b ? Time(b) : "--:--.---")}";

        var e = t.Engine;
        string boost = sim.Engine.Config.Turbo != null ? $"\nBoost     {(e.MapKpa - 101.3) / 100.0:+0.00;-0.00} bar" : "";
        _gauges.Text = $"Coolant   {e.CoolantC:F0} °C\nOil       {e.OilC:F0} °C   {e.OilPressure / 1e5:F1} bar\nAFR       {e.Afr:F1}   knock retard {e.KnockRetard:F1}°{boost}\n"
                       + $"Clutch    {t.ClutchTemperatureC:F0} °C   holds {t.ClutchCapacityNm:F0} N·m\nBrakes    F {t.BrakeTemperatureFrontC:F0} °C   R {t.BrakeTemperatureRearC:F0} °C\n"
                       + $"Lateral   {t.LateralG:F2} g   long {t.LongitudinalG:+0.00;-0.00} g";

        for (int w = 0; w < 4; w++)
        {
            double usage = t.TyreUsage[w];
            var c = usage < 0.8 ? Ui.Good : usage < 0.98 ? Ui.Caution : Ui.Danger;
            if (sim.WheelSurface[w] == Surface.Grass) c = c.Lerp(new Color(0.3f, 0.6f, 0.2f), 0.6f);
            var tyre = sim.Config.TireOf(w);
            double temp = t.TyreTemperatureC[w];
            // Cold tyres show blue, overheated ones deep red, whatever the load.
            if (temp < tyre.OptimalTemperatureC - tyre.TemperatureWindowC * 0.6) c = c.Lerp(new Color(0.35f, 0.6f, 1f), 0.7f);
            else if (temp > tyre.OptimalTemperatureC + tyre.TemperatureWindowC * 0.8) c = c.Lerp(new Color(0.7f, 0.1f, 0.1f), 0.7f);
            _tyreBoxes[w].BgColor = c;
            _tyreLabels[w].Text = $"{temp:F0}°\n{t.TyrePressureKpa[w]:F0}";
        }

        string status = session.AutopilotEnabled ? "AUTOPILOT" : "";
        if (e.Seized) status = "ENGINE SEIZED";
        else if (!e.Running) status = "Engine stopped: hold T to crank";
        else if (session.OffTrack) status = (status + "   Off track").Trim();
        _status.Text = $"{status}   [{cameraName}]".Trim();

        Ui.Clear(_warnings);
        foreach (var wng in sim.Engine.Damage.Warnings.Concat(sim.Wear.Warnings).Take(5))
        {
            var l = Ui.Label(wng.Message, 15, wng.Level == WarningLevel.Danger ? Ui.Danger : Ui.Caution);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.AddThemeConstantOverride("outline_size", 5);
            l.AddThemeColorOverride("font_outline_color", Colors.Black);
            _warnings.AddChild(l);
        }
    }

    private static string Time(double seconds) => $"{(int)(seconds / 60)}:{seconds % 60:00.000}";
}
