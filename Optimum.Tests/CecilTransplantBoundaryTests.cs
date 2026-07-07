#if !NO_DONOR
using System.Reflection;
using Vintagestory.Client.NoObf;
using Xunit;

namespace Optimum.Tests;

// The release DLL is the vanilla assembly with selected method bodies
// cecil-transplanted from this compiled donor (Optimum.Patcher/Program.cs).
// A transplanted body's member references resolve at JIT time against the
// vanilla definitions by name AND signature, so any member a transplanted
// method touches must keep its vanilla type in the donor. Optimum 0.2.1
// shipped ClientWorldMap.chunksLock retyped object -> System.Threading.Lock
// while ChunkCuller.CullInvisibleChunks (transplanted) referenced it: every
// world load then killed the chunkculling thread with MissingFieldException
// "Field not found: chunksLock". These tests pin the vanilla signatures of
// members that transplanted methods reference. The hardened
// SelfConsistencyVerifier fails the patcher on any new mismatch; this pins
// the known one at unit-test speed, before a package run.
public class CecilTransplantBoundaryTests
{
    [Fact]
    public void ChunksLockKeepsItsVanillaObjectType()
    {
        FieldInfo field = typeof(ClientWorldMap).GetField(
            "chunksLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(object), field.FieldType);
    }
}
#endif
