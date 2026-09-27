using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Gameplay;
using Godot;

namespace CarTuningSim;

/// <summary>
/// Session state shared by all views: content, the garage, dyno runs and failure history. Plain C#
/// (not a Node) so it can be reasoned about independently of the scene tree.
/// </summary>
public sealed class GameState
{
    public const string SavePath = "user://savegame.json";

    private static GameState? _instance;
    public static GameState Instance => _instance ??= new GameState();

    private GameState()
    {
        ContentDir = ResolveContentDir();
        ModsDir = System.Environment.GetEnvironmentVariable("CARSIM_MODS_DIR") is { Length: > 0 } modsEnv
            ? modsEnv
            : Path.GetFullPath(Path.Combine(ContentDir, "..", "mods"));
        var result = ContentLoader.LoadWithMods(ContentDir, ModsDir);
        ContentErrors = result.Errors;
        Content = result.Database;
        Mods = result.Mods;
        ContentOverrides = result.Overrides;
        Garage = Garage.NewGame(Content, StartScenario(Content));
    }

    /// <summary>The scenario a session starts with: <c>--scenario=&lt;id&gt;</c> on the command line, else <see cref="DefaultScenario"/>.</summary>
    public static string StartScenario(ContentDatabase content)
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--scenario=", StringComparison.Ordinal)) return arg["--scenario=".Length..];
        return content.Scenarios.ContainsKey(DefaultScenario) ? DefaultScenario : content.Scenarios.Keys.First();
    }

    /// <summary>The new-game scenario offered first.</summary>
    public const string DefaultScenario = "project_car";

    public string ContentDir { get; }

    /// <summary>Folder whose subfolders are mods, loaded after the base content (CARSIM_MODS_DIR, else content/mods).</summary>
    public string ModsDir { get; }
    public IReadOnlyList<string> Mods { get; }
    public IReadOnlyList<string> ContentOverrides { get; }
    public IReadOnlyList<ContentError> ContentErrors { get; }
    public ContentDatabase Content { get; }
    public Garage Garage { get; private set; }
    public List<DynoRun> Runs { get; } = new();
    public List<FailureReport> FailureReports { get; } = new();

    /// <summary>Tab the main screen opens on when returning from another scene (consumed once).</summary>
    public string? ReturnTab { get; set; }

    /// <summary>Message shown once when returning to the main screen (e.g. why the car could not be driven).</summary>
    public string? ReturnMessage { get; set; }

    /// <summary>Raised after any change that views should reflect.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public void NewGame(string scenario = DefaultScenario)
    {
        Garage = Garage.NewGame(Content, scenario);
        Runs.Clear();
        FailureReports.Clear();
        NotifyChanged();
    }

    public string Save()
    {
        string path = ProjectSettings.GlobalizePath(SavePath);
        File.WriteAllText(path, SaveSystem.Serialize(Garage));
        return path;
    }

    public string Load()
    {
        string path = ProjectSettings.GlobalizePath(SavePath);
        if (!File.Exists(path)) throw new FileNotFoundException("No saved game yet.", path);
        Garage = SaveSystem.Deserialize(File.ReadAllText(path), Content);
        Runs.Clear();
        NotifyChanged();
        return path;
    }

    /// <summary>
    /// Content search order: CARSIM_CONTENT_DIR, "content/base" next to the executable (exported builds),
    /// then the repository's content/base next to the Godot project (editor/development).
    /// </summary>
    public static string ResolveContentDir()
    {
        var env = System.Environment.GetEnvironmentVariable("CARSIM_CONTENT_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        var exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var beside = Path.Combine(exeDir, "content", "base");
        if (Directory.Exists(beside)) return beside;
        var projectDir = ProjectSettings.GlobalizePath("res://");
        return Path.GetFullPath(Path.Combine(projectDir, "..", "content", "base"));
    }
}
