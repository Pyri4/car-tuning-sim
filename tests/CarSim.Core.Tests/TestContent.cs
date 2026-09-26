using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Tests;

/// <summary>Loads the real base content once for tests that exercise shipped data.</summary>
public static class TestContent
{
    private static readonly Lazy<ContentDatabase> Base = new(() =>
    {
        var result = ContentLoader.LoadDirectory(BaseContentPath);
        return result.GetOrThrow();
    });

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CarTuningSim.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root (CarTuningSim.sln).");
        }
    }

    public static string BaseContentPath => Path.Combine(RepoRoot, "content", "base");

    public static ContentDatabase Database => Base.Value;

    public const string K20 = "kestrel_k20";

    /// <summary>Shared factory for swapped-in parts so instance ids never collide within a test run.</summary>
    private static readonly PartInstanceFactory SwapFactory = new(1_000_000);

    public static EngineAssembly StockK20(PartInstanceFactory? factory = null) =>
        EngineAssembly.CreateStock(Database.GetEngine(K20), Database, factory ?? new PartInstanceFactory());

    /// <summary>Replaces the part in <paramref name="slot"/> (removing and re-installing whatever is in the way).</summary>
    public static void Swap(EngineAssembly a, string slot, string partId, PartInstanceFactory? factory = null)
    {
        factory ??= SwapFactory;
        var removedStack = new Stack<(string slot, PartInstance part)>();
        foreach (var s in a.RemovalSequenceFor(slot))
        {
            var r = a.Remove(s, out var p);
            Assert.True(r.Ok, r.Message);
            removedStack.Push((s, p!));
        }
        var rr = a.Remove(slot, out _);
        Assert.True(rr.Ok, rr.Message);
        var ir = a.Install(slot, factory.Create(Database.GetPart(partId)));
        Assert.True(ir.Ok, ir.Message);
        while (removedStack.Count > 0)
        {
            var (s, p) = removedStack.Pop();
            var r = a.Install(s, p);
            Assert.True(r.Ok, r.Message);
        }
    }
}
