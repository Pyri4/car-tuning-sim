using Godot;

namespace CarTuningSim;

/// <summary>
/// Headless CI check: <c>godot --headless --path game -- --smoke-test</c> loads content, builds the UI,
/// runs a dyno pull through the game state and prints a verdict line, then quits with an exit code.
/// </summary>
public static class SmokeTest
{
    public static bool Requested => System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--smoke-test") >= 0;

    public static void Run(SceneTree tree)
    {
        var state = GameState.Instance;
        bool ok = state.ContentErrors.Count == 0 && state.Content.Parts.Count > 0;
        string detail = $"content errors: {state.ContentErrors.Count}";
        var (sim, report) = state.Garage.CreateSimulation();
        if (sim == null) { ok = false; detail += "; engine cannot run: " + string.Join(" ", report.Errors); }
        else
        {
            var run = new CarSim.Core.Dyno.DynoRunner(sim, new CarSim.Core.Dyno.DynoSettings()).RunToCompletion();
            ok &= run.PeakPower != null && run.PeakPower.PowerKw > 50;
            detail += $"; {state.Garage.Engine.Definition.Name}: dyno peak {run.PeakPower?.PowerHp:F1} hp";
        }
        GD.Print($"SMOKE TEST {(ok ? "PASSED" : "FAILED")}: {detail}");
        tree.Quit(ok ? 0 : 1);
    }
}
