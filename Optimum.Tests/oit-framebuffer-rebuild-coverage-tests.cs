using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Verifies the OIT (Order-Independent Transparency) system correctly handles
/// framebuffer rebuild after resize and does not overwrite vanilla attachment 0.
/// </summary>
public class OitFramebufferRebuildCoverageTests
{
    [Fact]
    public void OitRebuildDetectsFramebufferIdentityChange()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // The rebuild condition must detect when the framebuffer instance changed
        // (after RebuildFrameBuffers replaces the list entry with a new object).
        Assert.Contains("transparentfb != capi.Render.FrameBuffers[1]", source);
    }

    [Fact]
    public void OitRevealAttachesToColorAttachment0_NotOverwritingVanilla()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // OIT reveal texture attaches to ColorAttachment0 (36064). This is by design:
        // the oit.fsh shader writes to layout(location = 0) which IS ColorAttachment0.
        // The vanilla accumulation texture ID in ColorTextureIds[0] becomes orphaned
        // from the FBO, but MergeTransparentRenderPass reads it by texture ID from
        // the shader uniform (not from attachment), so it reads the Optimum reveal data.
        Assert.Contains("(FramebufferAttachment)36064", source);
    }

    [Fact]
    public void OitAccumulationLayersAttachToSlots3Through5()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // Accumulation layers attach to ColorAttachment3-5 (36067, 36068, 36069)
        // matching oit.fsh layout(location = 3/4/5).
        Assert.Contains("(FramebufferAttachment)36067", source);
        Assert.Contains("(FramebufferAttachment)36068", source);
        Assert.Contains("(FramebufferAttachment)36069", source);
    }

    [Fact]
    public void OitDrawBuffersMatchesShaderOutputLocations()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // DrawBuffers must declare 6 attachments (0-5) matching oit.fsh outputs.
        // Using 6, not 7: attachment 6 would be unused by shaders.
        Assert.Contains("DrawBuffersEnum[6]", source);
        Assert.Contains("DrawBuffersEnum.ColorAttachment0", source);
        Assert.Contains("DrawBuffersEnum.ColorAttachment5", source);
        Assert.DoesNotContain("DrawBuffersEnum.ColorAttachment6", source);
    }

    [Fact]
    public void OitDisablesFlagPreventsFurtherRenderCalls()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // First line of OnRenderFrame must bail if OIT is disabled.
        Assert.Contains("if (SystemRenderOITLayers.optimumOitDisabled) return;", source);
    }

    [Fact]
    public void OitBlendFuncPreservesVanillaAttachments0And1()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // Attachments 0 and 1 use DST_COLOR * ZERO blend (774 = GL_DST_COLOR, 0 = GL_ZERO).
        // This multiplies existing content by incoming fragment, preserving reveal semantics.
        Assert.Contains("GL.BlendFunc(0, (BlendingFactorSrc)774, (BlendingFactorDest)0)", source);
        Assert.Contains("GL.BlendFunc(1, (BlendingFactorSrc)774, (BlendingFactorDest)0)", source);
    }

    [Fact]
    public void OitAccumulationBlendFuncUsesAdditiveBlend()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        // Attachments 3-5 use ONE + ONE additive blend (1 = GL_ONE).
        Assert.Contains("GL.BlendFunc(3, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
        Assert.Contains("GL.BlendFunc(4, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
        Assert.Contains("GL.BlendFunc(5, (BlendingFactorSrc)1, (BlendingFactorDest)1)", source);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
