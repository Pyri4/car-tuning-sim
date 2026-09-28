namespace CarSim.Verification;

/// <summary>Locates the repository (the directory holding <c>CarTuningSim.sln</c>) and its content layers.</summary>
public static class RepoPaths
{
    /// <summary>Walks up from the working directory, then from the executable, to the directory holding the solution.</summary>
    public static string Root
    {
        get
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "CarTuningSim.sln"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new InvalidOperationException("Could not locate the repository root (CarTuningSim.sln).");
        }
    }

    public static string BaseContent => Path.Combine(Root, "content", "base");

    /// <summary>Test-only content layers (the synthetic engine matrix), loaded like mods.</summary>
    public static string TestLayers => Path.Combine(Root, "content", "test");

    /// <summary>The checked-in regression fingerprint.</summary>
    public static string FingerprintBaseline => Path.Combine(Root, "tests", "baselines", "fingerprint.txt");

    /// <summary>How every shipped and test tune is regenerated with the dev calibrators.</summary>
    public static string TuneManifest => Path.Combine(Root, "tools", "CarSim.Verification", "tune-manifest.json");
}
