using System.Linq;
using CarTuningSim.UI;
using Godot;

namespace CarTuningSim;

/// <summary>
/// Root of the prototype UI: a status bar and tabs for the garage, engine, tuning, dyno and reports.
/// Command-line (after "--"): --tab=garage|workshop|tuning|dyno|reports|drive, --select=slot, --autorun,
/// --dyno-end=rpm, --screenshot=file.png, --frames=N.
/// </summary>
public partial class Main : Control
{
    private Label _status = null!;
    private TabContainer _tabs = null!;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..];
        bool Flag(string name) => args.Contains($"--{name}");

        Theme = Ui.BuildTheme();
        var background = new ColorRect { Color = new Color(0.13f, 0.14f, 0.17f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var root = Ui.VBox(0);
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var top = Ui.HBox(20);
        top.AddChild(Ui.Label("CAR TUNING SIMULATOR", 20, Ui.Accent));
        _status = Ui.Label("", 15);
        top.AddChild(_status);
        root.AddChild(Ui.Margin(top, 8));

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_tabs);
        var engine = new EngineView { Name = "Workshop", InitialSelection = Arg("select") };
        var dyno = new DynoView { Name = "Dyno", AutoRun = Flag("autorun") };
        if (double.TryParse(Arg("dyno-end"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var end)) dyno.AutoEndRpm = end;
        _tabs.AddChild(new GarageView { Name = "Garage" });
        _tabs.AddChild(engine);
        _tabs.AddChild(new TuningView { Name = "Tuning" });
        _tabs.AddChild(dyno);
        _tabs.AddChild(new ReportsView { Name = "Reports" });

        var tab = Arg("tab");
        if (tab != null)
        {
            for (int i = 0; i < _tabs.GetTabCount(); i++)
                if (_tabs.GetTabTitle(i).Equals(tab, System.StringComparison.OrdinalIgnoreCase)) _tabs.CurrentTab = i;
        }

        GameState.Instance.Changed += UpdateStatus;
        UpdateStatus();
        AddChild(new ScreenshotHelper());
        if (SmokeTest.Requested) CallDeferred(nameof(RunSmokeTest));
        GD.Print($"Car Tuning Simulator ready. Content: {GameState.Instance.ContentDir} ({GameState.Instance.Content.Parts.Count} parts, {GameState.Instance.ContentErrors.Count} errors)");
    }

    public override void _ExitTree() => GameState.Instance.Changed -= UpdateStatus;

    private void RunSmokeTest() => SmokeTest.Run(GetTree());

    private void UpdateStatus()
    {
        var g = GameState.Instance.Garage;
        var report = g.Validate();
        string health = report.CanRun ? "engine can run" : $"engine cannot run ({report.Errors.Count()} problems)";
        _status.Text = $"Money {g.Money:N0}   ·   {(g.EngineInCar ? "engine in car" : "engine on stand")}   ·   {health}   ·   {g.Fuel.Name}   ·   tune: {g.Tune.Name}";
    }
}
