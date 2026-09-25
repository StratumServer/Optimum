using System;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
/// <summary>
/// Issue #74: the GUI item render path reuses a per-thread scratch ItemRenderInfo
/// instead of allocating one per visible slot per frame (measured ~104 B/slot,
/// up to ~136 slots => ~14 KB/frame with an inventory open). Correctness rests on
/// two things these tests pin:
///   1. ResetItemRenderInfo must clear the reused instance to exactly the same
///      values a freshly-constructed ItemRenderInfo has, or a stale field could
///      leak from one item into the next. This test derives the expected defaults
///      from a real `new ItemRenderInfo()` via reflection, so it fails if the
///      engine adds/changes a field and the reset list is not updated.
///   2. The public IRenderAPI path must keep allocating a fresh instance (mods
///      call it and may retain the result), only the internal per-slot path reuses.
/// Plus the config round-trip and the patch/patcher wiring.
/// </summary>
public class ItemRenderInfoReuseTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // The fields ResetItemRenderInfo(...) is responsible for clearing, with the
    // default value a fresh ItemRenderInfo has for each. TextureSize is a reused
    // sub-object (Width/Height cleared to 0); OverlayTexture is intentionally NOT
    // reset (kept for reuse, gated behind OverlayOpacity>0), matching the impl.
    [Fact]
    public void ResetDefaults_CoverAllValueFields_OfFreshInstance()
    {
        var fresh = new ItemRenderInfo();

        // Every public instance field on ItemRenderInfo that carries per-item state.
        // If the engine adds a new one, this list (and the impl's reset) must grow.
        var expected = new (string name, object? def)[]
        {
            (nameof(ItemRenderInfo.ModelRef), null),
            (nameof(ItemRenderInfo.Transform), null),
            (nameof(ItemRenderInfo.CullFaces), false),
            (nameof(ItemRenderInfo.TextureId), 0),
            (nameof(ItemRenderInfo.AlphaTest), 0f),
            (nameof(ItemRenderInfo.HalfTransparent), false),
            (nameof(ItemRenderInfo.NormalShaded), false),
            (nameof(ItemRenderInfo.ApplyColor), false),
            (nameof(ItemRenderInfo.OverlayOpacity), 0f),
            (nameof(ItemRenderInfo.DamageEffect), 0f),
            (nameof(ItemRenderInfo.InSlot), null),
            (nameof(ItemRenderInfo.dt), 0f),
        };

        foreach (var (name, def) in expected)
        {
            var f = typeof(ItemRenderInfo).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(f != null, $"ItemRenderInfo.{name} missing - reset list is stale");
            Assert.Equal(def, f!.GetValue(fresh));
        }

        // TextureSize exists and is a fresh Size2i (Width/Height 0) - the impl
        // resets it in place rather than reallocating.
        var ts = typeof(ItemRenderInfo).GetField(nameof(ItemRenderInfo.TextureSize));
        Assert.NotNull(ts);
        var size = ts!.GetValue(fresh);
        Assert.NotNull(size);

        // Guard against the engine adding a public instance field we do not handle.
        // OverlayTexture is the one deliberately-not-reset field.
        var handled = new System.Collections.Generic.HashSet<string>(Array.ConvertAll(expected, e => e.name))
        {
            nameof(ItemRenderInfo.TextureSize),
            nameof(ItemRenderInfo.OverlayTexture),
        };
        foreach (var f in typeof(ItemRenderInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(handled.Contains(f.Name),
                $"ItemRenderInfo has unhandled public field '{f.Name}'. Update ResetItemRenderInfo and this test.");
        }
    }

    [Fact]
    public void ItemRenderInfoReuse_ConfigRoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optimum-item-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            OptimumConfig.SetDataPath(dir);
            OptimumConfig.ItemRenderInfoReuseEnabled = false;
            OptimumConfig.Save();
            OptimumConfig.ItemRenderInfoReuseEnabled = true; // clobber before reload
            OptimumConfig.Load();
            Assert.False(OptimumConfig.ItemRenderInfoReuseEnabled);

            OptimumConfig.ItemRenderInfoReuseEnabled = true;
            OptimumConfig.Save();
            OptimumConfig.ItemRenderInfoReuseEnabled = false;
            OptimumConfig.Load();
            Assert.True(OptimumConfig.ItemRenderInfoReuseEnabled);
        }
        finally
        {
            OptimumConfig.ItemRenderInfoReuseEnabled = true;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void InventoryItemRendererSource_ReusesScratchAndKeepsPublicApiFresh()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(),
            "build/VintagestoryLib/Vintagestory.Client.NoObf/InventoryItemRenderer.cs"));
        // Internal fill helper + reset dependency exist.
        Assert.Contains("FillItemStackRenderInfo", src);
        Assert.Contains("ResetItemRenderInfo", src);
        // The per-thread scratch is reused in the GUI path, gated by the flag.
        Assert.Contains("optimumGuiRenderInfoScratch", src);
        Assert.Contains("OptimumConfig.ItemRenderInfoReuseEnabled", src);
        // The public API method still hands back a fresh instance for mods.
        Assert.Contains("return FillItemStackRenderInfo(game, inSlot, target, dt, new ItemRenderInfo())", src);
    }

    [Fact]
    public void PatcherRegistersItemRenderMembersAndTransplant()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "Optimum.Patcher/Program.cs"));
        Assert.Contains("optimumGuiRenderInfoScratch", program);
        Assert.Contains("FillItemStackRenderInfo", program);
        Assert.Contains("ResetItemRenderInfo", program);
        Assert.Contains("\"RenderItemstackToGui\"", program);
    }

    [Fact]
    public void ItemRenderProfilerCounterIsWired()
    {
        // Diagnostic profiler (OPTIMUM_ITEM_PROFILE) exists and starts at zero frames.
        OptimumDiagnostics.ItemRenderEndFrame();
        Assert.Contains("item-render profile", OptimumDiagnostics.GetItemRenderProfileSummary());
    }
}
