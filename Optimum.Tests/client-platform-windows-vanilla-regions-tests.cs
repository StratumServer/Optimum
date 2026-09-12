using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A: "OFF is vanilla" is a test. ClientPlatformWindows.cs may
/// differ from the decompiled vanilla source in <c>_ref/</c> only in the members listed here.
/// The comparison is per class member (fields, properties, methods, nested types), with
/// comment lines dropped and whitespace collapsed, and ignores member order. A member that
/// changes and is not listed fails, and so does a listed member that no longer differs, so
/// the list stays the exact set of Optimum-owned regions.
/// </summary>
public class ClientPlatformWindowsVanillaRegionsTests
{
    private const string VanillaSource = "_ref/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    private static readonly string[] OwnedRegions =
    {
        // Members Optimum adds (injected by the patcher): the TAA/FSR state and GL halves, the
        // step 3-5 graphics virtuals' GL overrides, frame pacing, the parity dump.
        "ApplyOptimumMotionAccumulateBlendState", "ApplyOptimumMotionBlendState",
        "ApplyTransparentMergeBlendState", "ApplyTransparentPassBlendState",
        "BeginFinalCompositionDrawBuffers", "BeginMotionOnlyWrite", "BeginMotionWrite",
        "BeginOcclusionQuery", "BeginOitAccumulation", "BindCurrentFrameBuffer",
        "BindCurrentFrameBufferKeepViewport", "BindOitTextures", "BindProgramTexture2D",
        "BindProgramTextureCube", "BindSampler", "BindUBO", "ClearBoundFrameBuffer", "ClearDefaultDepth",
        "ClearFrameBufferPass", "ClearSsaoTarget", "ClearTextureRegion", "CreateOitTargets",
        "CreateOptimumHistoryTargetGl", "DeleteMeshHandle", "DeleteOcclusionQuery", "DeleteUBO",
        "DeleteVertexArrayHandles", "DisableOptimumFsr", "DisableOptimumTaa",
        // DLSS plan, Phase 1: the display size (window client size) and the render size
        // (Primary's allocated size), overrides of the ClientPlatformAbstract virtuals.
        "DisplayHeight", "DisplayWidth",
        "DisposeShaderProgram",
        "EnableMotionDrawBuffers", "EnableMotionOnlyDrawBuffers", "EndFrame", "EndMotionOnlyWrite",
        "EndMotionWrite", "EndOcclusionQuery", "EnsureOptimumDefaults", "EnsureOptimumTimerResolution",
        "GenOcclusionQuery", "GraphicsBackendName", "InstallOptimumMotionWriteHooks",
        "LoadTextureFromRgbaPointer", "MotionAttachmentIndex", "OptimumAdoptFrameBufferSettings",
        "OptimumAdoptTaaTargets", "OptimumBgFpsFocusDebounceMs", "OptimumBgMaxFps", "OptimumCloudReactive",
        "OptimumFinishDeviceFrameBufferSetup", "OptimumFsrBlitActive", "OptimumFsrFramebufferIndex",
        "OptimumGlR32f", "OptimumMotionWriteActive", "OptimumOnProcessExit", "OptimumParityDumpAttachment",
        "OptimumParityReadTextureGl", "OptimumParitySlotName", "OptimumRenderSsao", "OptimumRunParityDump",
        "OptimumRunPendingTaaShaderReload", "OptimumSpinIterations", "OptimumSpinTailMinProcessorCount",
        "OptimumSsaoKernel", "OptimumTaaHistoryIndexA", "OptimumTaaHistoryIndexB", "OptimumTaaRequested",
        // DLSS plan, Phase 2: the temporal pipeline's shared questions and the
        // upscaler's half of DisableOptimumTaa.
        "OptimumTemporalRequested", "OptimumMotionWritesReady", "DisableOptimumUpscaler",
        "OptimumTaaSharpenIndex",
        // DLSS plan, Phase 3: the upscaler's placement in the frame - its
        // display-resolution target, the per-frame flag, the platform questions the
        // Vulkan platform answers and the screenshot redirect.
        "OptimumUpscaledSceneIndex", "optimumUpscaledThisFrame", "OptimumUpscalerActive",
        "optimumUpscaleSsaoApplied", "ApplyOptimumUpscaleSsao",
        "OptimumTryPlanUpscaleRenderSize", "RenderOptimumUpscale", "OptimumCompositeFrameBuffer",
        "OptimumUpscaledThisFrame", "OptimumBindCompositeForCapture",
        "OptimumTimeBeginPeriod", "OptimumTimeEndPeriod",
        "OptimumUndershootPercent", "OptimumYieldThresholdMs", "ProbeThickLineSupport",
        "ReadDefaultFramebuffer", "ReadTextureForParity", "RenderHeight", "RenderOptimumSkyMotion",
        "RenderOptimumTaaResolve", "RenderOptimumTaaSharpen", "RenderWidth", "RestorePrimaryDrawBuffers",
        "RestoreWorldDrawBuffers", "SelectBackDrawBuffer", "SelectFsrDrawBuffer", "SetBlendEnabled",
        "SetDepthRange", "SetOptimumMotionAttachmentIndex", "SetProgramSamplerUnit", "SetSamplerLodBias",
        "SetTextureDepthCompare", "SetTextureLodBias", "SetUniform", "SetUniformArray1", "SetUniformArray2",
        "SetUniformArray3", "SetUniformArray4", "SetUniformMatrices", "SetUniformMatrices4x3",
        "SetUniformMatrix", "TaaHistory", "TaaResolvedThisFrame", "TaaTargetsReady",
        "TryGetOcclusionQueryResult", "UnbindUBO", "UpdateUBO", "UseShaderProgram",
        "_optimumFocusLostStopwatch", "_optimumSettingsInitialized", "_optimumTimerResolutionRaised",
        "_taaFrameParity", "_taaHistoryValid", "optimumFsrDisabled", "optimumMotionAttachmentIndex",
        "optimumMotionDrawBuffersOff", "optimumMotionDrawBuffersOn", "optimumMotionOnlyDrawBuffers",
        "optimumMotionWriteActive", "optimumParityDumpDone", "optimumParityWorldFrames",
        "optimumTaaDisabled", "optimumTaaResolvedThisFrame", "optimumTaaShaderReloadPending",
        "optimumTaaTargetsReady", "taaResolvedColorTexture", "taaResolvedGlowTexture",

        // Vanilla members with an Optimum edit (the patcher transplant targets and the members
        // it virtualizes in place: base edits, FSR/TAA/post chain, frame pacing, mesh bulk copy),
        // some of which also carry the compile fix-ups below.
        "BlitPrimaryToDefault", "BuildMipMaps", "CheckFboStatus", "ClearFrameBuffer", "CompileShader",
        "CreateShaderProgram", "CreateUBO", "CurrentFrameBuffer", "CurrentFrameBufferKeepVw",
        "DisposeFrameBuffers", "GetGraphicsCardRenderer", "GlGetMaxTextureSize", "GlToggleBlend",
        "LoadFrameBuffer", "LogAndTestHardwareInfosStage2", "MergeTransparentRenderPass", "MouseGrabbed",
        "Mouse_WheelChanged", "RebuildFrameBuffers", "RenderFinalComposition", "RenderFullscreenTriangle",
        "RenderPostprocessingEffects", "SaveScreenshot", "GrabScreenshot",
        "SetupDefaultFrameBuffers", "Start", "UnloadFrameBuffer",
        "UpdateMesh", "UpdateSSBOMesh", "Window_Resize", "updateIndices", "updateVAO", "window_RenderFrame",

        // Compile fix-ups only: decompiler artefacts the donor tree rewrites to build
        // (((T)(ref e)).X becomes e.X, explicit OpenTK qualification, int casts). Not
        // transplanted; the vanilla IL of these stays in the patched DLL.
        "CheckGlError", "CheckGlErrorAlways", "GlGetError", "LoadMouseCursor", "Mouse_ButtonDown",
        "Mouse_ButtonUp", "Mouse_Move", "Window_FileDrop", "game_KeyDown", "game_KeyPress", "game_KeyUp",
    };

    [Fact]
    public void ClientPlatformWindowsDiffersFromVanillaOnlyInTheOwnedRegions()
    {
        string? vanillaPath = TryFind(VanillaSource);
        if (vanillaPath == null)
        {
            // _ref/ is the decompiled vanilla client, present wherever the lib is built.
            Assert.False(File.Exists(PatchReader.FindRepositoryFile(VulkanPlatformSource.ClientPlatformWindowsSource)),
                "build/ is materialised but _ref/ is not; the vanilla comparison cannot run");
            return;
        }

        List<Member> vanilla = ClassMembers(File.ReadAllText(vanillaPath), "ClientPlatformWindows");
        List<Member> patched = ClassMembers(VulkanPlatformSource.ReadClientPlatformWindows(), "ClientPlatformWindows");
        // Vanilla 1.22.7 splits into 262 members; far fewer means the splitter lost the class body.
        Assert.True(vanilla.Count > 200, "the vanilla member split found only " + vanilla.Count + " members");

        var differing = new SortedSet<string>(StringComparer.Ordinal);
        CollectUnmatched(patched, vanilla, differing);
        CollectUnmatched(vanilla, patched, differing);

        var owned = new SortedSet<string>(OwnedRegions, StringComparer.Ordinal);
        var unexpected = new SortedSet<string>(differing, StringComparer.Ordinal);
        unexpected.ExceptWith(owned);
        var stale = new SortedSet<string>(owned, StringComparer.Ordinal);
        stale.ExceptWith(differing);

        Assert.True(unexpected.Count == 0,
            "ClientPlatformWindows differs from vanilla outside the owned regions:\n" + string.Join("\n", unexpected));
        Assert.True(stale.Count == 0,
            "listed as owned but identical to vanilla (remove from the list):\n" + string.Join("\n", stale));
    }

    private static void CollectUnmatched(List<Member> members, List<Member> against, SortedSet<string> into)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Member member in against)
        {
            remaining.TryGetValue(member.Text, out int count);
            remaining[member.Text] = count + 1;
        }
        foreach (Member member in members)
        {
            if (remaining.TryGetValue(member.Text, out int count) && count > 0)
            {
                remaining[member.Text] = count - 1;
            }
            else
            {
                into.Add(member.Name);
            }
        }
    }

    private readonly record struct Member(string Name, string Text);

    /// <summary>
    /// Splits the body of the first class named <paramref name="className" /> into its
    /// brace-depth-1 members. String and character literals are skipped so braces inside
    /// them do not count; a closing brace followed by <c>;</c>, <c>,</c>, <c>)</c> or
    /// <c>.</c> continues the member (initialisers).
    /// </summary>
    private static List<Member> ClassMembers(string source, string className)
    {
        var lines = new StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            lines.Append(line).Append('\n');
        }
        string text = lines.ToString();

        Match declaration = Regex.Match(text, @"\bclass\s+" + className + @"\b");
        Assert.True(declaration.Success, "class " + className + " not found");
        int open = text.IndexOf('{', declaration.Index);
        var members = new List<Member>();
        int depth = 1;
        int start = open + 1;
        for (int i = open + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                bool verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
                i++;
                while (i < text.Length)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') break;
                    i++;
                }
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '{')
            {
                depth++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0) break;
                if (depth == 1)
                {
                    int next = i + 1;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next < text.Length && ";,).".IndexOf(text[next]) >= 0) continue;
                    AddMember(members, text.Substring(start, i - start + 1));
                    start = i + 1;
                }
                continue;
            }
            if (c == ';' && depth == 1)
            {
                AddMember(members, text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }
        return members;
    }

    private static void AddMember(List<Member> members, string raw)
    {
        string normalized = Regex.Replace(raw, @"\s+", " ").Trim();
        if (normalized.Length == 0 || normalized == ";") return;
        string header = Regex.Replace(normalized, @"^(\[[^\]]*\]\s*)+", string.Empty);
        int cut = header.Length;
        foreach (char stop in new[] { '(', '{', '=', ';' })
        {
            int index = header.IndexOf(stop);
            if (index >= 0 && index < cut) cut = index;
        }
        Match name = Regex.Match(header.Substring(0, cut), @"(\w+)\s*(<[^>]*>)?\s*$");
        members.Add(new Member(name.Success ? name.Groups[1].Value : header, normalized));
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
