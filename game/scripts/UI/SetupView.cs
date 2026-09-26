using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;
using Godot;

namespace CarTuningSim.UI;

/// <summary>
/// Car set-up: every adjustable setting of the installed parts (tyre pressures, camber, damping, bars,
/// brake balance, LSD preload, ...), the balance figures they produce, and a chassis bench test to
/// compare set-ups. Settings are stored on the parts (and saved); adding adjustable parts adds rows.
/// </summary>
public partial class SetupView : HSplitContainer
{
    private VBoxContainer _settings = null!, _summary = null!, _bench = null!;
    private Button _benchButton = null!;
    private Label _benchStatus = null!;
    private bool _selfChange;
    private readonly List<string> _benchHistory = new();

    private static GameState State => GameState.Instance;
    private static Garage G => State.Garage;

    public override void _Ready()
    {
        SplitOffsets = new[] { 820 };
        _settings = Ui.VBox(6);
        AddChild(Ui.Scroll(Ui.Margin(_settings, 12)));

        var right = Ui.VBox(8);
        right.AddChild(Ui.Heading("Balance"));
        _summary = Ui.VBox(4);
        right.AddChild(_summary);
        right.AddChild(Ui.Heading("Chassis bench"));
        right.AddChild(Ui.Wrapped("Skidpad at 80 km/h and a 100–0 km/h threshold stop with the current set-up. Nothing wears on the bench.", 13, Ui.Muted));
        _benchButton = Ui.Button("Run bench test", RunBench);
        right.AddChild(_benchButton);
        _benchStatus = Ui.Label("", 14, Ui.Muted);
        right.AddChild(_benchStatus);
        _bench = Ui.VBox(4);
        right.AddChild(_bench);
        AddChild(Ui.Scroll(Ui.Margin(right, 12)));

        State.Changed += OnStateChanged;
        Refresh();
    }

    public override void _ExitTree() => State.Changed -= OnStateChanged;

    private void OnStateChanged()
    {
        // Rebuilding the rows while the player is dragging a value would steal focus.
        if (_selfChange) { RefreshSummary(); return; }
        Refresh();
    }

    private void Refresh()
    {
        Ui.Clear(_settings);
        _settings.AddChild(Ui.Heading("Set-up"));
        var slots = G.Engine.Definition.Slots.Concat(G.Vehicle?.Slots ?? System.Array.Empty<EngineSlotDefinition>()).ToList();
        int rows = 0;
        foreach (var slot in slots)
        {
            var part = G.PartIn(slot.Id);
            if (part == null || part.Definition.Adjustments.Count == 0) continue;
            _settings.AddChild(Ui.Label($"{slot.Label} — {part.Definition.Name}", 15, Ui.Accent));
            foreach (var adj in part.Definition.Adjustments)
            {
                _settings.AddChild(Row(slot.Id, part, adj));
                rows++;
            }
        }
        if (rows == 0) _settings.AddChild(Ui.Wrapped("No installed part is adjustable.", 14, Ui.Muted));
        var fixedParts = slots.Select(s => (s, p: G.PartIn(s.Id))).Where(x => x.p != null && x.p.Definition.Adjustments.Count == 0 && x.s.Category is "suspension" or "brakes" or "differential").ToList();
        foreach (var (s, p) in fixedParts)
        {
            string upgrade = s.Category switch { "suspension" => "coilovers", "brakes" => "a big brake kit with a balance bar", _ => "a limited-slip differential" };
            _settings.AddChild(Ui.Wrapped($"{s.Label}: {p!.Definition.Name} has no adjustments — {upgrade} would add them.", 13, Ui.Muted));
        }
        RefreshSummary();
    }

    private Control Row(string slotId, PartInstance part, PartAdjustment adj)
    {
        var row = Ui.HBox(10);
        row.AddChild(Ui.Expand(Ui.Label(adj.Label, 14)));
        var spin = new SpinBox
        {
            MinValue = adj.Min, MaxValue = adj.Max, Step = adj.Step, Value = part.SettingOf(adj.Field),
            CustomMinimumSize = new Vector2(130, 0), Suffix = SpecDescriber.UnitOfField(adj.Field),
        };
        spin.ValueChanged += v => Apply(slotId, adj.Field, v);
        row.AddChild(spin);
        var reset = Ui.Button("Default", () => { spin.Value = adj.Default; });
        reset.TooltipText = $"{adj.Default:0.##} {SpecDescriber.UnitOfField(adj.Field)}";
        row.AddChild(reset);
        row.AddChild(Ui.Label($"{adj.Min:0.##} … {adj.Max:0.##}", 12, Ui.Muted));
        return row;
    }

    private void Apply(string slotId, string field, double value)
    {
        var r = G.Adjust(slotId, field, value);
        if (!r.Ok) { Ui.Message(this, "Set-up", r.Message); return; }
        _selfChange = true;
        try { State.NotifyChanged(); }
        finally { _selfChange = false; }
    }

    private void RefreshSummary()
    {
        Ui.Clear(_summary);
        if (G.Vehicle == null || G.Chassis == null || G.Chassis.MissingRequiredSlots().Count > 0)
        {
            _summary.AddChild(Ui.Wrapped("Fit every chassis part to see the car's balance.", 13, Ui.Muted));
            return;
        }
        var c = new VehicleConfiguration(G.Vehicle, G.Chassis, G.Engine, State.Content);
        double front = c.Brakes.FrontMaxTorqueNm, rear = c.Brakes.RearMaxTorqueNm * c.Brakes.RearPressureFactor;
        void Line(string label, string value, string hint = "")
        {
            var row = Ui.HBox();
            row.AddChild(Ui.Expand(Ui.Label(label, 14, Ui.Muted)));
            row.AddChild(Ui.Label(value, 14));
            _summary.AddChild(row);
            if (hint.Length > 0) _summary.AddChild(Ui.Wrapped(hint, 12, Ui.Muted));
        }
        Line("Mass / front weight", $"{c.Mass:F0} kg / {c.FrontWeightFraction * 100:F0} %");
        Line("Roll stiffness at the front", $"{c.FrontRollShare * 100:F0} %", "More at the front = more understeer; more at the rear = more oversteer.");
        Line("Roll / pitch frequency", $"{c.RollFrequency / (2 * Mathf.Pi):F2} / {c.PitchFrequency / (2 * Mathf.Pi):F2} Hz");
        Line("Centre of gravity height", $"{c.CgHeight * 1000:F0} mm");
        Line("Brake torque at the front", $"{front / (front + rear) * 100:F0} %", "Too much rear brake and the rears lock first: the car spins under braking.");
        Line("Static camber F / R", $"{c.Suspension.FrontCamberDeg:0.0}° / {c.Suspension.RearCamberDeg:0.0}°", "Some negative camber keeps the loaded outside tyre flat in corners; it costs a little braking and wears the inner edge.");
        Line("Tyre pressure F / R", $"{c.TiresFront.PressureKpa:F0} / {c.TiresRear.PressureKpa:F0} kPa",
            $"Best grip at {c.TiresFront.OptimalPressureKpa:F0} / {c.TiresRear.OptimalPressureKpa:F0} kPa. Softer: lazier and hotter; harder: nervous, smaller contact patch.");
        if (c.Differential.Type == "clutch_lsd") Line("LSD preload", $"{c.Differential.PreloadNm:F0} N·m");
    }

    private void RunBench()
    {
        _benchButton.Disabled = true;
        _benchStatus.Text = "Testing…";
        var garage = G;
        Task.Run(() => ChassisBench.Run(garage)).ContinueWith(t =>
            Callable.From(() => ShowBench(t.Result)).CallDeferred());
    }

    private void ShowBench(BenchResult r)
    {
        _benchButton.Disabled = false;
        if (!r.Ok) { _benchStatus.Text = r.Problem; return; }
        _benchStatus.Text = "";
        var changes = new List<string>();
        foreach (var slot in G.Engine.Definition.Slots.Concat(G.Vehicle?.Slots ?? System.Array.Empty<EngineSlotDefinition>()))
        {
            var part = G.PartIn(slot.Id);
            if (part == null) continue;
            foreach (var (field, value) in part.Settings)
                changes.Add($"{slot.Label}: {part.Definition.FindAdjustment(field)?.Label ?? field} {value:0.##}{SpecDescriber.UnitOfField(field)}");
        }
        string setup = string.Join(" · ", changes);
        _benchHistory.Insert(0, $"Skidpad {r.SkidpadG:F3} g   ·   100–0 km/h {r.BrakingDistance100M:F1} m   ·   brakes {r.FrontBrakeShare * 100:F0} % front\n{(setup.Length > 0 ? setup : "default settings")}");
        Ui.Clear(_bench);
        for (int i = 0; i < _benchHistory.Count && i < 8; i++)
            _bench.AddChild(Ui.Wrapped(_benchHistory[i], i == 0 ? 14 : 12, i == 0 ? Ui.Good : Ui.Muted));
    }
}
