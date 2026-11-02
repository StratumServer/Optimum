#if !NO_DONOR
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

public sealed class GuiGpuContextCleanupTests
{
    [Theory]
    [InlineData("TryCreateCandidateTexture")]
    [InlineData("RunProbe")]
    public void PerUploadContextReleasesResourcesInsideFinally(string methodName)
    {
        using var module = ModuleDefinition.ReadModule(typeof(Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend).Assembly.Location);
        var type = module.GetType("Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend");
        var method = Assert.Single(type.Methods.Where(m => m.Name == methodName));
        var release = Assert.Single(method.Body.Instructions.Where(i =>
            i.Operand is MethodReference m && m.DeclaringType.FullName == "SkiaSharp.GRContext" && m.Name == "AbandonContext"));
        Assert.Contains(method.Body.ExceptionHandlers, h => h.HandlerType == ExceptionHandlerType.Finally &&
            h.HandlerStart.Offset <= release.Offset && (h.HandlerEnd == null || release.Offset < h.HandlerEnd.Offset));
    }
}
#endif
