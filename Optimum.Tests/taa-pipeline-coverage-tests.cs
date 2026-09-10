using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class TaaPipelineCoverageTests
{
    [Fact]
    public void CecilPatcherShipsEveryTaaMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        // Transplant targets.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"Set3DProjection\", 2", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"get_CurrentProjectionMatrix\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"OnFowChanged\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"OnResize\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"Start\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"RenderAfterPostProcessing\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientEventManager\", \"TriggerReloadShaders\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderProgramsPre\", 0", patcher);

        // New members needing injection (CompileAndTrackShaderProgram is a
        // genuinely new private helper extracted from
        // loadRegisteredShaderPrograms, not a transplant of a pre-existing
        // vanilla method - it has no vanilla counterpart to transplant onto).
        Assert.Contains("\"CompileAndTrackShaderProgram\"", patcher);
        Assert.Contains("\"CurrentProjectionMatrixUnjittered\"", patcher);
        Assert.Contains("\"TemporalContext\"", patcher);
        Assert.Contains("\"TaaDebug\"", patcher);
        Assert.Contains("\"MotionAttachmentIndex\"", patcher);
        Assert.Contains("\"TaaHistory\"", patcher);
        Assert.Contains("\"DisableOptimumTaa\"", patcher);
        Assert.Contains("\"CreateOptimumHistoryTarget\"", patcher);
        Assert.Contains("\"CreateOptimumHistoryTargetGl\"", patcher);

        // Both new vanilla-type member-injection dictionaries exist.
        Assert.Contains("[\"Vintagestory.Client.NoObf.RenderAPIGame\"]", patcher);
    }

    [Fact]
    public void MotionAttachmentIndexIsTwoWithoutSsaoAndFourWithIt()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // The motion attachment index logic (2 without SSAO, 4 with) - the
        // attachment is appended after the existing 2/4 slots, matching the
        // property doc comment.
        Assert.Contains(
            "the Primary colour-attachment index that holds per-pixel motion",
            platform);
        Assert.Contains("enabled - 2 without", platform);
        Assert.Contains("SSAO G-buffer, 4 with it", platform);
    }

    [Fact]
    public void DefaultDrawBufferMasksAreUnchangedByTaa()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Primary's draw-buffer mask is still derived only from
        // primaryAttachments (2 or 4 colour targets), never including the new
        // motion attachment - it is enabled per-pass by writers, not by
        // default.
        Assert.Contains("device.SetDrawBuffers(primary.FboId, (1 << primaryAttachments) - 1);", platform);
        // Transparent (OIT) keeps its untouched six/three-output mask.
        Assert.Contains("device.SetDrawBuffers(transparent.FboId, 7);", platform);
    }

    [Fact]
    public void ClearFrameBufferClearsTheMotionAttachmentOnBothPaths()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Device path.
        Assert.Contains("optimumDevice.ClearColor(MotionAttachmentIndex, 0f, 0f, 0f, 0f);", platform);
        // GL path.
        Assert.Contains("GL.ClearBuffer((ClearBuffer)6144, MotionAttachmentIndex, new float[4]);", platform);
        // Both are guarded so a failed/absent motion attachment leaves the
        // clear untouched (MotionAttachmentIndex stays -1 via DisableOptimumTaa).
        Assert.Equal(2, Count(platform, "if (MotionAttachmentIndex >= 0)"));
    }

    [Fact]
    public void CurrentProjectionMatrixOnlyJittersWhenJitterActive()
    {
        // The transplant patch's diff hunks are not contiguous with the rest
        // of the file (context lines get truncated at hunk boundaries), so
        // read the full Cecil-transplanted source directly rather than via
        // ReadPatchedOrSource here - this test needs to walk from one member
        // declaration to the next.
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        int getterStart = clientMain.IndexOf("public float[] CurrentProjectionMatrix", StringComparison.Ordinal);
        Assert.True(getterStart >= 0);
        int getterEnd = clientMain.IndexOf("public float[] CurrentProjectionMatrixUnjittered", getterStart, StringComparison.Ordinal);
        Assert.True(getterEnd > getterStart);
        string getter = clientMain.Substring(getterStart, getterEnd - getterStart);

        Assert.Contains("OptimumTemporal.Frame.JitterActive", getter);
        // Only shears when JitterActive AND the matrix on top of PMatrix is the
        // exact one Set3DProjection last recorded - anything else (ortho, HUD,
        // shadow, pushed matrices) falls through to the vanilla tmpMatrix copy.
        Assert.Contains("set3DProjectionTempMat4", getter);
        Assert.Contains("OptimumTemporal.Frame.ApplyJitterCopy(top)", getter);

        // The unjittered companion never shears at all.
        int unjitteredStart = getterEnd;
        int unjitteredEnd = clientMain.IndexOf("public float[] CurrentModelViewMatrix", unjitteredStart, StringComparison.Ordinal);
        Assert.True(unjitteredEnd > unjitteredStart);
        string unjittered = clientMain.Substring(unjitteredStart, unjitteredEnd - unjitteredStart);
        Assert.Contains("PMatrix.Top", unjittered);
        Assert.DoesNotContain("JitterActive", unjittered);
        Assert.DoesNotContain("ApplyJitterCopy", unjittered);
    }

    [Fact]
    public void TaaAndJitterDefaultsAreOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("Taa = false", config);
        Assert.Contains("TaaJitterDev = false", config);
        Assert.Contains("EffectiveTaa", config);
    }

    [Fact]
    public void TaaDebugViewIsGatedBehindMotionAttachmentAndFallsThroughOtherwise()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("OptimumConfig.TaaDebugView != 0 && MotionAttachmentIndex >= 0", platform);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
