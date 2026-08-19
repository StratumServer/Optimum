using System.IO;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// A method transplanted whole via Cecil member injection (mod-patcher.cs's
/// Methods list, Program.cs's targets) that contains a lambda compiled into
/// a *cached static delegate* (a compiler-generated `<>c` class, typical of
/// LINQ calls like `.All(x => ...)`) references that class's method by
/// MethodReference - but injection only clones the named method, not its
/// dependent nested closure type. SelfConsistencyVerifier correctly detects
/// this and refuses to write output ("N self-reference error(s), output not
/// written"), but ModPatcher.Patch then throws on total &lt;= 0, which is an
/// *unhandled* exception in Optimum.exe - it kills the launcher mid-sequence,
/// so every DLL not yet patched that run (VintagestoryLib, VintagestoryAPI in
/// this incident) stays fully vanilla with zero warning to the user beyond
/// "Game Version: v1.22.7 (Stable)" missing its "+ Optimum" suffix.
///
/// WeatherSimulationSound::updateSounds hit this from
/// `rainSoundsLeafless.All(s => s.IsReady)` / `rainSoundsLeafy.All(...)`,
/// added when the volume-deadzone patch first got a patches/runtime
/// counterpart. Fixed by replacing both with explicit loops (docs/il-patcher-plan.md
/// documents this exact constraint: "methods [containing lambdas] cannot be
/// transplanted" - this was a known rule, just not checked before adding this
/// particular method to the Methods list).
///
/// Instance-capturing lambdas (e.g. the TyronThreadPool.QueueTask(() => ...)
/// a few lines below in the same method) are not necessarily unsafe - that
/// one transplants fine, verified by actually running Optimum.Patcher --mod
/// against the real vanilla DLL, not just by building the donor project.
/// The .All(...) pattern specifically (LINQ predicate cached in a shared
/// `<>c` class) is the one confirmed to break.
/// </summary>
public class CecilTransplantLambdaTests
{
    /// <summary>
    /// ClientMain::Start retains vanilla's `rand = new ThreadLocal&lt;Random&gt;(() =&gt;
    /// new Random(Environment.TickCount));` - a lambda capturing nothing, so the
    /// compiler caches it as a static delegate field on ClientMain/&lt;&gt;c
    /// (&lt;&gt;9__&lt;memberOrdinal&gt;_0). That ordinal is the member's declaration
    /// index within the type, which differs between the vanilla assembly and this
    /// decompiled-and-rebuilt donor (confirmed empirically: donor ordinal 345,
    /// vanilla's slot 345 holds an unrelated Func&lt;ClientPlayer,ClientPlayer&gt; from
    /// a different property), so InjectMissingFieldsForMethod throws a field
    /// signature mismatch the moment Start is added as a transplant target - this
    /// was the blocker preventing Start (and therefore the tesselation worker
    /// registration and pool wiring it does) from shipping at all. Fixed by
    /// replacing the lambda with an instance method group (CreateRandom) -
    /// verified via `make patch-il` with Start temporarily added to targets
    /// (PATCHED cleanly, 0 verifier errors) and an ilspycmd IL dump confirming
    /// ClientMain/&lt;&gt;c no longer has a &lt;Start&gt;b__ member.
    /// </summary>
    [Fact]
    public void ClientMainStartHasNoThreadLocalRandomLambda()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.DoesNotContain("() => new Random(Environment.TickCount)", addedLines);
        Assert.Contains("rand = new ThreadLocal<Random>(CreateRandom);", addedLines);
        Assert.Contains("private Random CreateRandom()", addedLines);
    }

    [Fact]
    public void WeatherSimulationSoundUpdateSoundsHasNoAllPredicateLambda()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationSound.cs.patch");

        // Only the added (+) lines matter here - a unified diff's context and
        // removed (-) lines legitimately still show vanilla's original
        // .All(s => s.IsReady) text being replaced.
        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.DoesNotContain(".All(", addedLines);
        Assert.DoesNotContain("=> s.IsReady", addedLines);
    }

    /// <summary>
    /// Worker-pool wiring plan's Option B decision gate: TerrainChunkTesselator must
    /// stay a real, assignable field, not become a Cecil-injected property returning
    /// ChunkTesselatorManager.PrimaryTesselator. A property would require transplanting
    /// two collateral vanilla readers (BlockTextureAtlasManager::RuntimeCreateNewAtlas,
    /// ClientSystemStartup::HandleWorldMetaData) and leave a permanently-null public
    /// field on the property-converted metadata for any mod compiled against vanilla
    /// VintagestoryLib. See docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md.
    /// </summary>
    [Fact]
    public void ClientMainTerrainChunkTesselatorStaysAField()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        string addedLines = string.Join('\n', patch
            .Split('\n')
            .Where(line => line.StartsWith('+') && !line.StartsWith("+++")));

        Assert.Contains("TerrainChunkTesselator = terrainChunkTesselatorManager.PrimaryTesselator;", addedLines);
        Assert.DoesNotContain("TerrainChunkTesselator =>", addedLines);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
