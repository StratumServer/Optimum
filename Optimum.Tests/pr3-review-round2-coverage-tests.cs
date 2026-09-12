using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The PR #3 review, second round: the findings on the LOD-bias and slider commits
/// that survived verification against the branch.
///
/// Three of the four are about state that outlives the test that moved it - the
/// shared <c>NgxRuntime.Device</c>, the process-global <c>OptimumConfig</c> - and
/// the fourth about a build that ships a native library the managed side no longer
/// matches. The behavioural half of the atlas finding is
/// <c>Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests.DisposingTheSamplerFixtureReleasesItsAtlasesAndTheRegistration</c>,
/// which measures the release on the device; the snapshot finding is proved
/// directly below, on the config itself.
/// </summary>
public class Pr3ReviewRound2CoverageTests
{
    private const string GpuTests = "Optimum.Render.Vulkan.Tests/DlssUpscalerTests.cs";
    private const string TerrainTests = "Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests.cs";
    private const string PassthroughGpuTests = "Optimum.Render.Vulkan.Tests/PassthroughUpscalerTests.cs";
    private const string Project = "Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj";

    /// <summary>
    /// The whole snapshot goes back, not a set of defaults.
    ///
    /// This is the finding itself, driven on the config: put the process into a
    /// state no default matches, let a "test" move every field, restore, and
    /// require each one back. The old cleanups forced <c>Upscaler = "off"</c> and
    /// <c>ClearUpscalerPlan()</c>, so a run that reached them with an upscaler
    /// selected and a plan published handed the next test a different world - and
    /// this assembly runs its tests one at a time, so that is the run.
    /// </summary>
    [Fact]
    public void TheConfigSnapshotRestoresEveryFieldItCaptured()
    {
        OptimumConfigSnapshot outer = OptimumConfigSnapshot.Capture();
        try
        {
            int[] atlases = { 7, 11 };
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "quality";
            OptimumConfig.Taa = true;
            OptimumConfig.RenderScale = 0.75f;
            OptimumConfig.UpscalerLodBiasOffset = 0.25f;
            OptimumConfig.SetUpscalerPlan(0.6f, -1.25f);
            OptimumConfig.RegisterLodBiasedAtlases(atlases);
            OptimumConfig.NoteTerrainLodBiasApplied(-1.25f, reachedAtlases: true);

            OptimumConfigSnapshot snapshot = OptimumConfigSnapshot.Capture();

            // What a test does to it, ending the way the old cleanups ended.
            OptimumConfig.Upscaler = "off";
            OptimumConfig.UpscalerQuality = "ultraperformance";
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.RegisterLodBiasedAtlases(Array.Empty<int>());
            OptimumConfig.InvalidateTerrainLodBias();

            snapshot.Restore();

            Assert.Equal("dlss", OptimumConfig.Upscaler);
            Assert.Equal("quality", OptimumConfig.UpscalerQuality);
            Assert.True(OptimumConfig.Taa);
            Assert.Equal(0.75f, OptimumConfig.RenderScale, 5);
            Assert.Equal(0.25f, OptimumConfig.UpscalerLodBiasOffset, 5);
            Assert.Equal(0.6f, OptimumConfig.UpscalerRenderScale, 5);
            Assert.Equal(-1.25f, OptimumConfig.UpscalerLodBias, 5);
            Assert.Same(atlases, OptimumConfig.LodBiasedAtlases);
            Assert.Equal(-1.25f, OptimumConfig.AppliedTerrainLodBias, 5);

            // "Optimum has never touched the parameter" is a state of its own and
            // must not come back as a concrete bias.
            OptimumConfig.InvalidateTerrainLodBias();
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfigSnapshot untouched = OptimumConfigSnapshot.Capture();
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: true);
            untouched.Restore();
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));
            Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);
        }
        finally
        {
            outer.Restore();
        }
    }

    /// <summary>
    /// The LOD-bias suites restore through the snapshot instead of forcing defaults.
    /// </summary>
    [Fact]
    public void TheLodBiasSuitesRestoreTheConfigTheyFound()
    {
        foreach (string path in new[]
        {
            "Optimum.Tests/dlss-lod-bias-coverage-tests.cs",
            "Optimum.Tests/dlss-lod-bias-offset-coverage-tests.cs",
        })
        {
            string source = Read(path);
            Assert.Contains("OptimumConfigSnapshot.Capture()", source);
            // Every cleanup in these files is the snapshot's, nothing else: a
            // hand-written finally is what dropped a field on the floor.
            Assert.Equal(Count(source, "finally"), Count(source, "snapshot.Restore();"));
            Assert.Equal(Count(source, "finally"), Count(source, "OptimumConfigSnapshot.Capture()"));
        }
    }

    /// <summary>
    /// Both GPU tests keep the host inside the teardown.
    ///
    /// A failed assertion between <c>BeginFrame</c> and <c>Present</c> would
    /// otherwise hand the next test on the shared fixture device an open frame and a
    /// live DLSS feature; the adopted-session <c>Shutdown</c> retires the feature and
    /// drains, and the drain abandons the unsubmitted frame.
    /// </summary>
    [Fact]
    public void TheGpuTestsShutTheHostDownFromAFinally()
    {
        string tests = Read(GpuTests);

        int hosts = 0;
        const string marker = "DlssUpscaler host = Host(";
        for (int at = tests.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            hosts++;
            // Bounded by the next Host(...) - to the end of the file it would let one
            // host's try/finally be satisfied by another member's, which is exactly the
            // teardown this test exists to find missing.
            int next = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            string after = next >= 0
                ? tests[(at + marker.Length)..next]
                : tests[(at + marker.Length)..];

            // The try that guards the host comes before anything that can fail.
            int tryAt = after.IndexOf("try", StringComparison.Ordinal);
            int planAt = after.IndexOf("host.TryPlan(", StringComparison.Ordinal);
            Assert.True(tryAt >= 0, "no try after a Host(...) at " + at);
            Assert.True(planAt < 0 || tryAt < planAt,
                "a planning assertion runs before the teardown is armed, after the Host(...) at " + at);

            // And the finally it opens shuts the host down.
            int finallyAt = after.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(finallyAt > 0, "no finally after the Host(...) at " + at);
            int shutdownAt = after.IndexOf("host.Shutdown();", finallyAt, StringComparison.Ordinal);
            Assert.True(shutdownAt > 0, "the finally after the Host(...) at " + at + " does not shut the host down");
        }

        Assert.Equal(3, hosts);
    }

    /// <summary>
    /// Wave-2 review, 2026-09-12. The same rule, in the file the previous round did
    /// not read: the passthrough GPU suite builds its own hosts with
    /// <c>new DlssUpscaler(Log)</c> rather than through <c>Host(...)</c>, and one of
    /// them shut down only on the successful path. NGX's lifetime is process-wide, so
    /// a host leaked by a failed assertion here is not this test's failure - it is the
    /// next test on the shared device falling over.
    /// </summary>
    [Fact]
    public void ThePassthroughGpuTestsShutTheirHostsDownFromAFinally()
    {
        string tests = Read(PassthroughGpuTests);

        int hosts = 0;
        const string marker = "var host = new DlssUpscaler(";
        for (int at = tests.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            hosts++;
            int next = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            string after = next >= 0
                ? tests[(at + marker.Length)..next]
                : tests[(at + marker.Length)..];

            int finallyAt = after.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(finallyAt > 0, "no finally after the host at " + at);
            int shutdownAt = after.IndexOf("host.Shutdown();", finallyAt, StringComparison.Ordinal);
            Assert.True(shutdownAt > 0,
                "the finally after the host at " + at + " does not shut the host down");
        }

        Assert.Equal(2, hosts);
    }

    /// <summary>
    /// The sampler fixture gives its atlas images back to the shared device, and the
    /// config it moved back to whoever set it.
    /// </summary>
    [Fact]
    public void TheTerrainSamplerFixtureReleasesWhatItAllocated()
    {
        string tests = Read(TerrainTests);
        string dispose = Between(tests, "public void Dispose()", "private void Log(");

        Assert.Contains("_seam.DeleteTexture(_atlases[i]);", dispose);
        Assert.Contains("RegisterLodBiasedAtlases(_previousAtlases)", dispose);
        Assert.Contains("NoteTerrainLodBiasApplied(_previousAppliedBias", dispose);
        // The registration is the fixture's to restore, so no test may clear it.
        Assert.DoesNotContain("RegisterLodBiasedAtlases(Array.Empty<int>())", tests);
    }

    /// <summary>
    /// A failed shim compile ships nothing rather than the previous build.
    ///
    /// The <c>Exists()</c> condition that packages the shim cannot tell a fresh
    /// library from a stale one, and a native library older than the managed side
    /// that P/Invokes it is an ABI mismatch inside NGX - not the "DLSS unavailable"
    /// line this build is supposed to degrade to.
    /// </summary>
    [Fact]
    public void AFailedShimCompileNeverLeavesAStaleLibraryToPackage()
    {
        string windows = Between(Read(Project), "<Exec Condition=\"'$(OS)' == 'Windows_NT'\"", "ContinueOnError");
        Assert.Contains("OptimumNgx.tmp.dll", windows);
        Assert.Contains("move /y", windows);
        Assert.Contains("del /q", windows);
        // The final name is written by the move, never by the compiler.
        Assert.DoesNotContain("-o &quot;$(NgxShimOutWin)\\OptimumNgx.dll&quot;", windows);

        string script = Read("native/optimum-ngx/build.sh");
        Assert.Contains("-o \"$target.tmp\"", script);
        Assert.Contains("rm -f \"$target.tmp\" \"$target\"", script);
        Assert.Contains("mv -f \"$target.tmp\" \"$target\"", script);
    }

    // ---- helpers -----------------------------------------------------------

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
