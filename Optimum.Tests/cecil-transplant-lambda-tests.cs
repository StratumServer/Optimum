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
/// "Game Version: v1.22.5 (Stable)" missing its "+ Optimum" suffix.
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

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
