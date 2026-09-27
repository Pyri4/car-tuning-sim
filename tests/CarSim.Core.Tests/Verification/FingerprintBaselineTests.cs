using CarSim.Verification;
using CarSim.Verification.Fingerprint;
using FingerprintFile = CarSim.Verification.Fingerprint.FingerprintBaseline;

namespace CarSim.Core.Tests.Verification;

/// <summary>
/// The regression fingerprint (docs/VERIFICATION.md): every case of the verification matrix must reproduce the
/// checked-in baseline (<c>tests/baselines/fingerprint.txt</c>) bit for bit — the K20 and M54 in every build, fuel,
/// sweep, dyno mode, cold start, failure hold, scenario and lap, and every synthetic family. A deliberate change is
/// re-baselined with <c>carsim fingerprint --write tests/baselines/fingerprint.txt</c> and documented; a change nobody
/// intended fails here. The cases are split over several classes so xUnit runs them in parallel.
/// </summary>
public static class FingerprintCheck
{
    private static readonly Lazy<FingerprintFile> Baseline =
        new(() => FingerprintFile.Load(RepoPaths.FingerprintBaseline));

    private static readonly Lazy<FingerprintContent> Content = new(() => new FingerprintContent(RepoPaths.Root));

    public static TheoryData<string> CasesStartingWith(params string[] prefixes)
    {
        var data = new TheoryData<string>();
        foreach (var c in FingerprintMatrix.Cases.Where(c => prefixes.Any(p => c.Id.StartsWith(p, StringComparison.Ordinal)))) data.Add(c.Id);
        return data;
    }

    public static void MatchesBaseline(string caseId)
    {
        var baseline = Baseline.Value;
        Assert.True(baseline.Descriptions.ContainsKey(caseId), $"Case '{caseId}' is not in the baseline: re-baseline with `carsim fingerprint --write tests/baselines/fingerprint.txt`.");
        var record = FingerprintRunner.RunCase(FingerprintMatrix.Get(caseId), Content.Value, baseline.Schema);
        var comparison = FingerprintComparison.Compare(baseline, new[] { record });
        Assert.True(comparison.Identical,
            $"{caseId} no longer reproduces the fingerprint baseline:\n{string.Join("\n", comparison.Changed.Concat(comparison.Missing))}\n" +
            "If the change is deliberate (a documented generic correction), re-baseline with " +
            "`carsim fingerprint --write tests/baselines/fingerprint.txt` and report the diff.");
    }
}

public class FingerprintK20Tests
{
    public static TheoryData<string> Cases => FingerprintCheck.CasesStartingWith("k20_", "fail_k20_");

    [Theory, MemberData(nameof(Cases))]
    public void MatchesTheBaseline(string caseId) => FingerprintCheck.MatchesBaseline(caseId);
}

public class FingerprintM54Tests
{
    public static TheoryData<string> Cases => FingerprintCheck.CasesStartingWith("m54_", "fail_m54_");

    [Theory, MemberData(nameof(Cases))]
    public void MatchesTheBaseline(string caseId) => FingerprintCheck.MatchesBaseline(caseId);
}

public class FingerprintVehicleTests
{
    public static TheoryData<string> Cases => FingerprintCheck.CasesStartingWith("scenario_", "drive_");

    [Theory, MemberData(nameof(Cases))]
    public void MatchesTheBaseline(string caseId) => FingerprintCheck.MatchesBaseline(caseId);
}

public class FingerprintSyntheticTests
{
    public static TheoryData<string> Cases => FingerprintCheck.CasesStartingWith("syn_");

    [Theory, MemberData(nameof(Cases))]
    public void MatchesTheBaseline(string caseId) => FingerprintCheck.MatchesBaseline(caseId);
}

public class FingerprintMatrixTests
{
    [Fact]
    public void EveryCaseIsCoveredByATestClassAndTheBaseline()
    {
        var covered = new[] { FingerprintK20Tests.Cases, FingerprintM54Tests.Cases, FingerprintVehicleTests.Cases, FingerprintSyntheticTests.Cases }
            .SelectMany(d => d.Cast<object[]>().Select(row => (string)row[0])).ToList();
        Assert.Equal(covered.Count, covered.Distinct().Count());
        Assert.Equal(FingerprintMatrix.Cases.Select(c => c.Id).OrderBy(x => x), covered.OrderBy(x => x));
        var baseline = FingerprintFile.Load(RepoPaths.FingerprintBaseline);
        Assert.Equal(FingerprintMatrix.Cases.Select(c => c.Id).OrderBy(x => x), baseline.CaseIds.OrderBy(x => x));
    }

    [Fact]
    public void TheMatrixCoversWhatTheMilestoneRequires()
    {
        var ids = FingerprintMatrix.Cases.Select(c => c.Id).ToHashSet();
        // Real engines on stock fuels and tunes, built and turbo configurations, both dyno modes, cold starts, abuse and
        // failure holds, the game's scenarios and laps; every synthetic family.
        Assert.Subset(ids, new HashSet<string>
        {
            "k20_stock_95", "k20_stock_98", "k20_short_runner_95", "k20_na_built_98", "k20_t28_98", "k20_t28_95", "k20_t35_98",
            "m54_stock_98", "m54_stock_95", "m54_parked_98", "drive_k20_stock", "drive_k20_t28", "drive_m54_stock",
            "scenario_project_car", "scenario_isar_c30_six", "fail_k20_overrev", "fail_m54_overrev", "fail_k20_overheat", "fail_m54_overheat",
        });
        Assert.Equal(TestContent.MatrixFamilies.OrderBy(x => x), ids.Where(id => id.StartsWith("syn_", StringComparison.Ordinal)).OrderBy(x => x));
        var baseline = FingerprintFile.Load(RepoPaths.FingerprintBaseline);
        foreach (var section in new[] { "wot", "partload", "cold", "dyno_sweep", "dyno_steady", "abuse" })
        {
            Assert.Contains(("k20_stock_95", section), baseline.Order);
            Assert.Contains(("m54_stock_98", section), baseline.Order);
            Assert.Contains(("k20_t28_98", section), baseline.Order);
        }
    }

    /// <summary>
    /// A knife-edge diagnostic section (the worn project car's autopilot lap) is reported when it differs but does not fail
    /// the fingerprint; any other section of the same case still does.
    /// </summary>
    [Fact]
    public void AKnifeEdgeSectionIsReportedButIsNotARegression()
    {
        Assert.True(FingerprintMatrix.KnifeEdgeSections.ContainsKey("scenario_project_car/drive"));
        Assert.All(FingerprintMatrix.KnifeEdgeSections.Keys, k => Assert.Contains(FingerprintMatrix.Cases, c => c.Id == k.Split('/')[0]));
        string Digest(char c) => new(c, 64);
        var baseline = FingerprintFile.Parse(
            "case scenario_project_car worn K20\n" +
            $"digest scenario_project_car dyno_sweep {Digest('a')} samples=10 channels=3\n" +
            $"digest scenario_project_car drive {Digest('b')} samples=4001 channels=3\n");
        CaseRecord Run(char dynoDigest, char driveDigest, int driveSamples)
        {
            var record = new CaseRecord("scenario_project_car");
            record.Sections.Add(new SectionRecord("dyno_sweep") { Digest = Digest(dynoDigest), Samples = 10, ChannelCount = 3 });
            record.Sections.Add(new SectionRecord("drive") { Digest = Digest(driveDigest), Samples = driveSamples, ChannelCount = 3 });
            return record;
        }

        var lapFlipped = FingerprintComparison.Compare(baseline, new[] { Run('a', 'c', 3513) });
        Assert.True(lapFlipped.Identical);
        Assert.Equal(2, lapFlipped.Diagnostics.Count); // the digest and the sample count
        Assert.Contains("knife edge", lapFlipped.Diagnostics.Single(d => d.Contains("digest")));

        var dynoMoved = FingerprintComparison.Compare(baseline, new[] { Run('d', 'b', 4001) });
        Assert.False(dynoMoved.Identical);
        Assert.Single(dynoMoved.Changed);
        Assert.Empty(dynoMoved.Diagnostics);
    }

    [Fact]
    public void TheBaselineRoundTripsThroughItsTextForm()
    {
        string text = File.ReadAllText(RepoPaths.FingerprintBaseline);
        var parsed = FingerprintFile.Parse(text);
        Assert.Equal(text.Replace("\r\n", "\n"), parsed.ToText(FingerprintMatrix.Cases.Select(c => c.Id).ToList()).Replace("\r\n", "\n"));
    }
}
