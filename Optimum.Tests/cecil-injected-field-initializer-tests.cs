using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Cecil field injection (Optimum.Patcher/mod-patcher.cs's Members manifests,
/// api-patcher.cs's equivalents) adds a field to an already-compiled type
/// without re-running its constructor, so a C# field initializer on an
/// injected field is silently never executed - the field is null at runtime
/// no matter what the source says. This has caused four real crashes so far:
/// EventManager.singleDelayedCallbackBlockKeys (fixed with a lazy ??= at each
/// use site, see EventManager.cs), MechanicalPowerMod.optimumTickNetworks
/// (same fix; NRE'd every server tick and tripped the DieAboveErrorCount
/// safety shutdown on a real singleplayer load),
/// WeatherSystemClient.optimumWindSpeed/optimumSurfaceWindSpeed (readonly
/// Vec3d fields; NRE'd on the first render frame after joining, every single
/// time - fixed by dropping the fields entirely and using local variables
/// instead, matching the VSEssentials/Systems/Weather/WeatherSystemClient.cs
/// fork source, which never needed injected reference-type fields for this),
/// and AStar.optimumNodePool (readonly PathNode[]; flooded server-main.log
/// with "Exception thrown during pathfinding" on every single pathfind call
/// from every entity, silently caught by the vanilla try/catch around
/// pathfinding so it never crashed outright - just made every mob pathless.
/// Fixed with a lazy ??= at each OptimumRentNode overload, matching the
/// MechanicalPowerMod pattern; a persistent pool can't become a local like
/// the weather fix, it has to survive across FindPathOrEscapePath calls).
/// WeatherSimulationSound.lastSetWindVolumeLeafy/Leafless/lastSetRainVolumeLeafy/Leafless
/// never actually shipped broken - the runtime patch for it didn't exist at
/// all until this class already had four known instances of the pattern, so
/// it was written Cecil-safe from the start (no initializer, relies on the
/// CLR's guaranteed float zero-default instead of the -1f sentinel the
/// source-tree fork uses). Kept as a guard so nobody "fixes" it back to -1f
/// by copying the source-tree version verbatim.
/// TreeGen.vineScratchPos/positionStack (readonly BlockPos / readonly
/// BlockPos[32]) is the sixth, and predates this session's other fixes - it
/// was already shipping broken. NRE'd in growBranch every time worldgen grew
/// a tree branch past depth 0, caught per-chunk by
/// ServerSystemSupplyChunks's pass error handler ("[Worldgen] An error was
/// thrown in pass Vegetation...") so it never crashed the server outright,
/// just silently truncated tree/vine generation. Most visible during world
/// shutdown, when many queued-but-incomplete chunks get force-processed at
/// once ("Incomplete chunks stored and wiped"), flooding server-worldgen.log
/// right as the player quits. Fixed the same way as AStar's node pool: lazy
/// ??=/null-check at each use site (positionStack is indexed by recursion
/// depth as a per-depth scratch-object pool, so it stays a field; vineScratchPos
/// is fully written and consumed within one PlaceBlockEtc call, so reuse
/// across calls is safe).
/// ClientMain.tesselationWorkers (readonly OptimumTesselationWorkerRegistry) is
/// the seventh, and is the same class of bug hitting the *other* patcher
/// entry point: Optimum.Patcher/Program.cs's membersToInject, consumed by
/// MemberInjector.InjectStaticMembers via ILPatcher.PatchWithInjection - the
/// main VintagestoryLib donor transplant, not the runtime mod-patcher.cs path
/// the six instances above went through, but the same InjectStaticMembers
/// function underneath. Shipped broken from 2026-08-10's first
/// IsTesselationThread fix (`51da961`) until this test: every real call
/// (VSSurvivalMod's MealMeshCache.GetOrCreateMealInContainerMeshRef, which
/// calls `capi.IsTesselationThread(...)` on every meal-container mesh build -
/// VSSurvivalMod ships as a normally-compiled DLL, not Cecil-transplanted, so
/// this call site was live immediately) NRE'd on `tesselationWorkers.Contains`.
/// Fixed the same way as MechanicalPowerMod/AStar: lazy `??=` at the one
/// writer (RegisterTesselationThread), null-safe reads at the other two
/// accessors (IsTesselationThread, GetTesselationWorkerSlot).
/// Guards against an eighth.
/// </summary>
public class CecilInjectedFieldInitializerTests
{
    [Fact]
    public void MechanicalPowerModTickNetworksIsLazilyInitialized()
    {
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechanicalPowerMod.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"optimumTickNetworks\s*=\s*new List<MechanicalNetwork>\(\);"),
            patch);
        Assert.Contains("optimumTickNetworks ??= new List<MechanicalNetwork>();", patch);
    }

    [Fact]
    public void WeatherSystemClientDoesNotInjectUninitializedReferenceFields()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSystemClient.cs.patch");

        // optimumWindSpeed/optimumSurfaceWindSpeed used to be readonly Vec3d
        // fields with a `= new Vec3d()` initializer that Cecil field
        // injection never runs, so they were null on every first use. Assert
        // they don't come back as fields at all - the safe fix here is local
        // variables (Vec3d is only touched inside OnRenderFrame, which is
        // itself transplanted whole, so locals work fine).
        Assert.DoesNotContain("optimumWindSpeed", patch);
        Assert.DoesNotContain("optimumSurfaceWindSpeed", patch);

        // optimumWindFrameCounter is an int (default-zeroed by the CLR, no
        // constructor call needed), so it's safe as an injected field.
        Assert.Contains("private int optimumWindFrameCounter;", patch);
    }

    [Fact]
    public void AStarNodePoolIsLazilyInitialized()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/Essentials/AStar.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly PathNode\[\]\s*optimumNodePool\s*=\s*new PathNode\[4096\];"),
            patch);
        Assert.Contains("optimumNodePool ??= new PathNode[4096];", patch);
    }

    [Fact]
    public void WeatherSimulationSoundVolumeDeadzoneFieldsHaveNoInitializer()
    {
        // The source-tree fork (patches/VSEssentials/Systems/Weather/WeatherSimulationSound.cs.patch)
        // sentinels these at -1f so the very first SetVolume call always
        // fires. That initializer is float literal IL in the constructor,
        // not metadata - safe there because that class is a normally
        // compiled C# type. The runtime patch injects these same fields via
        // Cecil member injection instead, which never runs the constructor,
        // so any non-default initializer would silently vanish. Defaulting
        // to 0f (the CLR's guaranteed zero-init, no initializer needed) is
        // functionally equivalent here: it only skips the very first
        // SetVolume call if the true starting volume happens to land within
        // the 0.01 deadzone of zero, which is inaudible anyway.
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/GameContent/WeatherSimulationSound.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"lastSet(Wind|Rain)Volume(Leafy|Leafless)\s*=\s*-1f;"),
            patch);
        Assert.Contains("private float lastSetWindVolumeLeafy;", patch);
        Assert.Contains("private float lastSetWindVolumeLeafless;", patch);
        Assert.Contains("private float lastSetRainVolumeLeafy;", patch);
        Assert.Contains("private float lastSetRainVolumeLeafless;", patch);
    }

    [Fact]
    public void TreeGenScratchFieldsHaveNoInitializer()
    {
        string patch = Read("patches/runtime/VSEssentials/Vintagestory/ServerMods/TreeGen.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly BlockPos\s*vineScratchPos\s*=\s*new BlockPos\(0\);"),
            patch);
        Assert.DoesNotMatch(
            new Regex(@"readonly BlockPos\[\]\s*positionStack\s*=\s*new BlockPos\[32\];"),
            patch);
        Assert.Contains("positionStack ??= new BlockPos[32];", patch);
        Assert.Contains("vineScratchPos ??= new BlockPos(0);", patch);
    }

    [Fact]
    public void ClientMainTesselationWorkersIsLazilyInitialized()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");

        Assert.DoesNotMatch(
            new Regex(@"readonly OptimumTesselationWorkerRegistry\s*tesselationWorkers\s*=\s*new OptimumTesselationWorkerRegistry\(\);"),
            patch);
        Assert.Contains("(tesselationWorkers ??= new OptimumTesselationWorkerRegistry()).Register(threadId);", patch);

        // IsTesselationThread/GetTesselationWorkerSlot both read the field before
        // it's necessarily been created (any thread can ask before the tesselation
        // worker thread has registered itself) - both must null-check.
        Assert.Contains("workers != null && workers.Contains(threadId);", patch);
        Assert.Contains("workers != null ? workers.GetSlot(threadId) : 0;", patch);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
