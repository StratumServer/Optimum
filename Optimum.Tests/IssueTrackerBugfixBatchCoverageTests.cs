using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

public class IssueTrackerBugfixBatchCoverageTests
{
    [Fact]
    public void FallingBlockRendererDoesNotMutateTheSharedDefaultBlockMesh()
    {
        // #9220-class: GetDefaultBlockMesh returns ShapeTesselatorManager's
        // shared blockModelDatas[block.BlockId], not a fresh copy. The
        // renderer must keep that mesh out of its reusable scratch field.
        string source = File.ReadAllText(FindRepositoryFile("VSEssentials/Entities/EntityBlockFalling.cs"));

        Assert.Contains("GetDefaultBlockMesh(entity.Block)", source);
        Assert.Contains("mesh.Clear();", source);
        Assert.Contains("MeshData meshToUpload;", source);
        Assert.Contains("meshToUpload = capi.TesselatorManager.GetDefaultBlockMesh(entity.Block);", source);
        Assert.Contains("UploadMultiTextureMesh(meshToUpload)", source);
        Assert.DoesNotContain("mesh = capi.TesselatorManager.GetDefaultBlockMesh", source);
    }

    [Fact]
    public void FallingBlockRendererUploadsCustomMeshesBeforeCachingThem()
    {
        string source = File.ReadAllText(FindRepositoryFile("VSEssentials/Entities/EntityBlockFalling.cs"));

        Assert.Contains("if (entity.meshRef == null)", source);
        Assert.Contains("dict[cacheKey] = entity.meshRef;", source);
        Assert.DoesNotContain("dict[cacheKey] = entity.meshRef = capi.Render.UploadMultiTextureMesh(mesh);", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs.patch")]
    public void RenderMultiTextureMeshSkipsDisposedMeshrefs(string relativePath)
    {
        // #8881/#8950/#8982-class: rendering a disposed meshref feeds freed
        // GL handles into plat.RenderMesh.
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("if (mmr == null || mmr.Disposed) return;", source);
        Assert.Contains("if (vao == null || vao.Disposed) continue;", source);
    }

    [Fact]
    public void RenderMultiTextureMeshIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.RenderAPIBase\", \"RenderMultiTextureMesh\", 3", programSource);
    }

    [Fact]
    public void PsychedelicPitchDriftIsClampedToTheSameBoundsAsNormalMouseLook()
    {
        // #9381: Pos.Pitch/MousePitch accumulate unbounded here, outside
        // ClientMain's normal clamp-then-sync path (UpdateCameraYawPitch),
        // letting prolonged psychedelic intensity drift pitch past the
        // poles and flip the camera. Same bounds ClientMain uses.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Client/Render/PerceptionEffects/PsychedelicPerceptionEffect.cs"));

        Assert.DoesNotContain("Pos.Pitch += dp;", source);
        Assert.DoesNotContain("MousePitch += dp;", source);
        Assert.Contains("GameMath.Clamp(capi.World.Player.Entity.Pos.Pitch + dp, 1.5857964f, 4.697389f)", source);
        Assert.Contains("GameMath.Clamp(capi.Input.MousePitch + dp, 1.5857964f, 4.697389f)", source);
    }

    [Theory]
    [InlineData("scripts/package-linux.ps1")]
    [InlineData("scripts/package-macos.ps1")]
    [InlineData("scripts/package-linux.sh")]
    [InlineData("scripts/package-macos.sh")]
    public void PackageScriptsPatchFromPristineVanillaLib(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("VintagestoryLib.vanilla.dll", source);
    }

    [Fact]
    public void IlHookSkipsAlreadyInsertedHookCalls()
    {
        string source = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/ILHook.cs"));

        Assert.Contains("AlreadyCallsHook", source);
        Assert.Contains("continue;", source);
    }

    [Fact]
    public void DeployValidatesShaderOverlaysBeforeCopying()
    {
        string makefile = File.ReadAllText(FindRepositoryFile("Makefile"));
        string validator = File.ReadAllText(FindRepositoryFile("scripts/validate-shader-assets.sh"));

        Assert.Contains("deploy: patch-il check-shaders", makefile);
        Assert.Contains("void\\s+main", validator);
    }

    [Fact]
    public void OptimumSettingsTabMovesBackButtonAfterInjectedTab()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");

        Assert.Contains("oButtonBounds.WithFixedWidth(w);", source);
        Assert.Contains("backButtonBounds.FixedRightOf(oButtonBounds, 15.0);", source);
        // oButtonBounds.ParentBounds (elementBounds on the main menu,
        // elementBounds3 in-game) is what the tab buttons are actually
        // parented to and must widen to fit the shifted Back button.
        Assert.Contains("tabRowParent.fixedWidth = contentRight;", source);
        // ...but it must NEVER widen alone. GuiElementDialogBackground passes
        // its own Bounds.OuterWidth/OuterHeight to
        // SurfaceTransformBlur.BlurPartial as the absolute right/bottom edge
        // of the blur region, and neither BlurPartial nor
        // GaussianBlur.boxBlur{H,T}_4RGBPartial clamps that against the
        // surface - the loops write destP[y * w + x] through a raw int* for x
        // in [xs, xe). GuiComposer.Compose sizes that surface from
        // GuiComposer.Bounds.OuterWidth, so a background bounds wider than the
        // composer bounds overruns the Cairo pixel buffer: a native heap
        // corruption that shows up as a 0xc0000005 access violation with no
        // managed stack trace (the earlier "silent hang/crash on tab switch",
        // wrongly blamed on the c.Bounds widening that was reverted twice).
        // Every fixed-width ancestor up to and including the composer's own
        // bounds therefore takes the same delta.
        Assert.Contains("ancestor.fixedWidth += grewBy;", source);
        Assert.Contains("if (ancestor == c.Bounds)", source);
        // The window bounds is the only bounds with a null ParentBounds and
        // must stay untouched, and FitToChildren ancestors re-measure
        // themselves, so the walk stops at the first non-fixed one.
        Assert.Contains("ancestor != null && ancestor.ParentBounds != null", source);
        Assert.Contains("if (ancestor.horizontalSizing != ElementSizing.Fixed)", source);
        // The old asymmetric widening (inner box only, +2 * padding on top of
        // the content width) is exactly what broke the invariant - keep it out.
        Assert.DoesNotContain("oButtonBounds.ParentBounds.fixedWidth = needed + 2.0 * GuiStyle.ElementToDialogPadding;", source);
    }

    [Fact]
    public void OptimumExtraTabSwitchesAreVerticallyCenteredOnTheirLabelRow()
    {
        // Measured pixel-for-pixel against a vanilla settings tab (Interface)
        // and the Optimum Extra tab from real screenshots: vanilla's own
        // AddSwitch/label pairs land within ~2px of each other vertically,
        // but OnOptimumOptions's original "+2" row offset put every switch
        // ~5-7px (displayed) below its label's center - consistent across
        // all 14 switch rows, not a progressive drift. Row spacing measured
        // at ~30 design units -> ~34px displayed (scale ~1.13), so the
        // needed correction is ~5 design units: "+2" -> "-3". Sliders
        // (measured ~1px off already) are untouched.
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");

        Assert.DoesNotMatch(new Regex(@"AddSwitch\(onOptimum\w+Changed, ElementBounds\.Fixed\(450, y0[^)]*\+ 2, 200, 20\)"), source);
        Assert.Contains("AddSwitch(onOptimumBackgroundFpsChanged, ElementBounds.Fixed(450, y0 - 3, 200, 20), \"optBgFps\")", source);
        Assert.Contains("AddSwitch(onOptimumEntityShaderCacheChanged, ElementBounds.Fixed(450, y0 + rowH * 16 - 3, 200, 20), \"optEntityShaderCache\")", source);
        // Sliders were already close to vanilla's alignment - untouched.
        Assert.Contains("AddSlider(onOptimumShadowDistChanged, ElementBounds.Fixed(450, y0 + rowH * 3 + 2, 200, 20), \"optShadowDist\")", source);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
