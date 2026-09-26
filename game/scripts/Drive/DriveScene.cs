using System;
using System.Collections.Generic;
using System.Linq;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;
using Godot;

namespace CarTuningSim.Drive;

/// <summary>
/// The test track. Builds the circuit and car from the garage's current build, runs a
/// <see cref="DrivingSession"/> from frame times and player input, and draws the result. All physics
/// lives in the core; this node only converts input and presents state.
/// Command-line (after "--"): --drive (start here), --autodrive, --warp=seconds (simulate ahead before the
/// first frame), --camera=chase|bumper|trackside, --smoke-test.
/// </summary>
public partial class DriveScene : Node3D
{
    private enum CameraMode { Chase, Bumper, Trackside }

    private const int SmokeFrames = 900;

    private DrivingSession _session = null!;
    private CarVisual _car = null!;
    private Camera3D _camera = null!;
    private DriveHud _hud = null!;
    private readonly KeyboardInputFilter _keys = new();
    private readonly List<Vector3> _tracksideCameras = new();
    private CameraMode _cameraMode = CameraMode.Chase;
    private Vector3 _chaseDirection = Vector3.Right;
    private bool _smoke;
    private int _frame;
    private double _startDistance;

    private static GameState State => GameState.Instance;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..];
        _smoke = args.Contains("--smoke-test");

        var (sim, problem) = State.Garage.CreateVehicleSimulation();
        if (sim == null)
        {
            GD.PrintErr($"Cannot drive: {problem}");
            if (_smoke) { GD.Print($"DRIVE SMOKE TEST FAILED: {problem}"); GetTree().Quit(1); return; }
            State.ReturnTab = "Garage";
            State.ReturnMessage = problem;
            CallDeferred(nameof(ReturnToGarage));
            return;
        }
        var track = TrackLayout.TestFacility();
        _session = new DrivingSession(sim, track) { AutopilotEnabled = args.Contains("--autodrive") || _smoke };
        _keys.Update(0, false, false, 0, 0, sim.Config);

        BuildEnvironment();
        AddChild(TrackMeshBuilder.Build(track));
        _car = CarVisual.Create(sim.Config);
        AddChild(_car);
        _camera = new Camera3D { Far = 2500, Fov = 70, Current = true };
        AddChild(_camera);
        for (int i = 0; i < track.Count; i += 60)
        {
            var (x, y) = track.Point(i);
            double h = track.HeadingAt(i);
            double off = -(track.Width / 2 + 14); // outside-ish, to the right of the centreline
            _tracksideCameras.Add(TrackMeshBuilder.ToGodot(x - Math.Sin(h) * off, y + Math.Cos(h) * off, 7));
        }
        _cameraMode = (Arg("camera") ?? "chase").ToLowerInvariant() switch
        {
            "bumper" => CameraMode.Bumper,
            "trackside" => CameraMode.Trackside,
            _ => CameraMode.Chase,
        };

        var layer = new CanvasLayer();
        AddChild(layer);
        _hud = new DriveHud { Theme = UI.Ui.BuildTheme() };
        layer.AddChild(_hud);
        _hud.SetRedline(sim.Engine.Ecu.Tune.RevLimitRpm);
        _hud.BackToGarage += ReturnToGarage;
        _hud.DismissFailure += () => GetTree().Paused = false;
        ProcessMode = ProcessModeEnum.Always;

        DriveInput.EnsureActions();

        if (double.TryParse(Arg("warp"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var warp))
        {
            bool autopilot = _session.AutopilotEnabled;
            _session.AutopilotEnabled = true;
            for (double t = 0; t < warp; t += DrivingSession.MaxFrameSeconds) Advance(DrivingSession.MaxFrameSeconds, default);
            _session.AutopilotEnabled = autopilot;
        }
        _startDistance = sim.State.Distance;
        _car.UpdateFrom(sim, 0, 0);
        SnapCamera();
        AddChild(new ScreenshotHelper());
    }

    private void BuildEnvironment()
    {
        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial { SkyTopColor = new Color(0.32f, 0.52f, 0.80f), SkyHorizonColor = new Color(0.72f, 0.80f, 0.88f), GroundHorizonColor = new Color(0.45f, 0.50f, 0.42f) } };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.66f, 0.72f),
            AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogLightColor = new Color(0.70f, 0.77f, 0.85f),
            FogDensity = 0.0012f,
            FogSkyAffect = 0.25f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50, -35, 0),
            ShadowEnabled = true,
            LightEnergy = 0.95f,
            DirectionalShadowMaxDistance = 120,
        });
    }

    public override void _Process(double delta)
    {
        if (_session == null) return;
        _frame++;
        double dt = _smoke ? 1.0 / 60.0 : delta;
        if (GetTree().Paused) dt = 0;
        var sim = _session.Sim;
        double speed = Math.Sqrt(sim.State.U * sim.State.U + sim.State.V * sim.State.V);

        var input = ReadInput(dt, speed);
        Advance(dt, input);

        _car.UpdateFrom(sim, dt, _session.Last?.Brake ?? 0);
        UpdateCamera(dt);
        _hud.UpdateFrom(_session, _cameraMode.ToString());

        if (_smoke && _frame >= SmokeFrames) FinishSmokeTest();
    }

    private void Advance(double dt, VehicleInputs input)
    {
        var step = _session.Advance(dt, input);
        if (step.NewFailures.Count > 0)
        {
            State.FailureReports.AddRange(step.NewFailures);
            if (!_smoke && _hud != null)
            {
                _hud.ShowFailure(step.NewFailures[0]);
                GetTree().Paused = true;
            }
        }
    }

    private VehicleInputs ReadInput(double dt, double speed)
    {
        if (Input.IsActionJustPressed(DriveInput.ShiftUp)) _session.RequestShift(+1);
        if (Input.IsActionJustPressed(DriveInput.ShiftDown)) _session.RequestShift(-1);
        if (Input.IsActionJustPressed(DriveInput.Reset)) _session.ResetToTrack();
        if (Input.IsActionJustPressed(DriveInput.Autopilot)) _session.AutopilotEnabled = !_session.AutopilotEnabled;
        if (Input.IsActionJustPressed(DriveInput.Camera)) { _cameraMode = (CameraMode)(((int)_cameraMode + 1) % 3); SnapCamera(); }
        if (Input.IsActionJustPressed(DriveInput.Help)) _hud.ToggleHelp();
        if (Input.IsActionJustPressed(DriveInput.Exit)) { ReturnToGarage(); return default; }

        double handbrake = Input.GetActionStrength(DriveInput.Handbrake);
        bool starter = Input.IsActionPressed(DriveInput.Starter);
        var pad = DriveInput.ReadJoypad();
        if (pad is { } p)
            return new VehicleInputs { Throttle = p.Throttle, Brake = p.Brake, Steer = p.Steer, Handbrake = handbrake, Starter = starter };
        int steer = (Input.IsActionPressed(DriveInput.Left) ? 1 : 0) - (Input.IsActionPressed(DriveInput.Right) ? 1 : 0);
        _keys.Update(dt, Input.IsActionPressed(DriveInput.Throttle), Input.IsActionPressed(DriveInput.Brake), steer, speed, _session.Sim.Config);
        return _keys.ToInputs(handbrake, starter);
    }

    private void SnapCamera()
    {
        var s = _session.Sim.State;
        _chaseDirection = new Vector3((float)Math.Cos(s.Heading), 0, (float)-Math.Sin(s.Heading));
        UpdateCamera(1.0);
    }

    private void UpdateCamera(double dt)
    {
        var s = _session.Sim.State;
        var carPos = _car.GlobalPosition;
        var heading = new Vector3((float)Math.Cos(s.Heading), 0, (float)-Math.Sin(s.Heading));
        switch (_cameraMode)
        {
            case CameraMode.Chase:
            {
                // Follow the direction of travel (so slides are visible), falling back to the heading when slow.
                var velocity = new Vector3((float)(s.U * Math.Cos(s.Heading) - s.V * Math.Sin(s.Heading)), 0, (float)-(s.U * Math.Sin(s.Heading) + s.V * Math.Cos(s.Heading)));
                var wanted = velocity.Length() > 3 ? velocity.Normalized().Lerp(heading, 0.5f).Normalized() : heading;
                _chaseDirection = _chaseDirection.Lerp(wanted, (float)(1 - Math.Exp(-dt * 4))).Normalized();
                var target = carPos - _chaseDirection * 6.2f + Vector3.Up * 2.1f;
                _camera.GlobalPosition = _camera.GlobalPosition.Lerp(target, (float)(1 - Math.Exp(-dt * 12)));
                _camera.LookAt(carPos + Vector3.Up * 0.9f + _chaseDirection * 3f, Vector3.Up);
                _camera.Fov = 68;
                break;
            }
            case CameraMode.Bumper:
                _camera.GlobalPosition = carPos + heading * 0.3f + Vector3.Up * 1.05f;
                _camera.LookAt(_camera.GlobalPosition + heading * 10f, Vector3.Up);
                _camera.Fov = 75;
                break;
            case CameraMode.Trackside:
            {
                var nearest = _tracksideCameras.OrderBy(c => c.DistanceSquaredTo(carPos)).First();
                _camera.GlobalPosition = nearest;
                _camera.LookAt(carPos + Vector3.Up * 0.6f, Vector3.Up);
                _camera.Fov = Mathf.Clamp(2400f / Math.Max(10f, nearest.DistanceTo(carPos)), 12f, 60f);
                break;
            }
        }
    }

    private void ReturnToGarage()
    {
        GetTree().Paused = false;
        if (_session != null)
        {
            var t = _session.Timer;
            double km = (_session.Sim.State.Distance - _startDistance) / 1000.0;
            string best = t.BestLap is double b ? $", best lap {b / 60:0}:{b % 60:00.000}" : "";
            State.Garage.LogEvent($"Test drive: {km:F1} km, {t.Laps} timed lap(s){best}{(_session.Failures.Count > 0 ? $", {_session.Failures.Count} failure(s)" : "")}.");
            State.ReturnTab = _session.Failures.Count > 0 ? "Reports" : "Garage";
        }
        State.NotifyChanged();
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }

    private void FinishSmokeTest()
    {
        var sim = _session.Sim;
        double metres = sim.State.Distance - _startDistance;
        bool ok = metres > 150 && !sim.Engine.Damage.Seized;
        GD.Print($"DRIVE SMOKE TEST {(ok ? "PASSED" : "FAILED")}: {metres:F0} m in {sim.State.Time:F1} s, {_session.Last?.SpeedKmh:F0} km/h, gear {_session.Last?.GearLabel}");
        GetTree().Quit(ok ? 0 : 1);
    }
}
