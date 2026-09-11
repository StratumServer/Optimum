using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 0: VulkanClientPlatform subclasses ClientPlatformWindows, so the
/// patcher must unseal the class and virtualize the members it overrides, and the donor the
/// renderer compiles against must declare the same shape. A missing entry on either side
/// only shows up at runtime (TypeLoadException, or an override that is silently bypassed).
/// </summary>
public class PlatformSubstitutionCoverageTests
{
    private const string PlatformType = "\"Vintagestory.Client.NoObf.ClientPlatformWindows\"";

    private static readonly (string Name, int ParamCount, string Declaration)[] VirtualizedMembers =
    {
        ("SetupDefaultFrameBuffers", 0, "public virtual List<FrameBufferRef> SetupDefaultFrameBuffers()"),
        ("DisposeFrameBuffers", 1, "public virtual void DisposeFrameBuffers(List<FrameBufferRef> buffers)"),
        ("RenderFullscreenTriangle", 1, "public virtual void RenderFullscreenTriangle(MeshRef modelRef)"),
        ("GetGraphicsCardRenderer", 0, "public virtual string GetGraphicsCardRenderer()"),
    };

    [Fact]
    public void PatcherUnsealsThePlatformClass()
    {
        string unseal = ListBody(Read("Optimum.Patcher/Program.cs"), "var typesToUnseal = new List<string>");

        Assert.Contains(PlatformType + ",", unseal);
    }

    [Fact]
    public void PatcherVirtualizesEveryOverriddenPlatformMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        string virtualize = ListBody(patcher, "var methodsToVirtualize = new List<MethodTarget>");

        foreach (var member in VirtualizedMembers)
            Assert.Contains($"new({PlatformType}, \"{member.Name}\", {member.ParamCount})", virtualize);

        Assert.Contains("typesToUnseal: typesToUnseal", patcher);
        Assert.Contains("methodsToVirtualize: methodsToVirtualize", patcher);
    }

    [Fact]
    public void PatcherVerifiesVirtualDispatchBeforeWritingTheOutput()
    {
        string ilPatcher = Read("Optimum.Patcher/ILPatcher.cs");

        int transplant = ilPatcher.IndexOf("TransplantBody(vanillaMethod, compiledMethod", StringComparison.Ordinal);
        int hooks = ilPatcher.IndexOf("// Phase 3: IL hooks", StringComparison.Ordinal);
        int virtualize = ilPatcher.IndexOf("PlatformSubstitution.VirtualizeMethods(", StringComparison.Ordinal);
        int verify = ilPatcher.IndexOf("PlatformSubstitution.VerifyVirtualDispatch(", StringComparison.Ordinal);
        int write = ilPatcher.IndexOf("AssemblyWriter.Write(vanillaAsm", StringComparison.Ordinal);

        Assert.True(transplant >= 0 && hooks > transplant);
        Assert.True(virtualize > hooks, "flags must be applied after every transplant and hook");
        Assert.True(verify > virtualize && write > verify, "the dispatch verifier must run before the write");
    }

    [Fact]
    public void DonorDeclaresTheSubclassableShape()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs", optional: true)
            ?? PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));

        Assert.Contains("public class ClientPlatformWindows : ClientPlatformAbstract", platform);
        Assert.DoesNotContain("sealed class ClientPlatformWindows", platform);
        foreach (var member in VirtualizedMembers)
            Assert.Contains(member.Declaration, platform);
    }

    private static string ListBody(string source, string declaration)
    {
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing declaration: {declaration}");
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string? Read(string relativePath, bool optional = false)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
        }
        catch (FileNotFoundException) when (optional)
        {
            return null;
        }
    }

    private static string Read(string relativePath) => Read(relativePath, optional: false)!;
}
