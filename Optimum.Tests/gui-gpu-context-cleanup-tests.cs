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
        var method = Assert.Single(type.Methods, m => m.Name == methodName);
        var release = Assert.Single(method.Body.Instructions, i =>
            i.Operand is MethodReference m && m.DeclaringType.FullName == "SkiaSharp.GRContext" && m.Name == "AbandonContext");
        Assert.Contains(method.Body.ExceptionHandlers, h => h.HandlerType == ExceptionHandlerType.Finally &&
            h.HandlerStart.Offset <= release.Offset && (h.HandlerEnd == null || release.Offset < h.HandlerEnd.Offset));
    }

    [Theory]
    [InlineData("TryCreateCandidateTexture")]
    [InlineData("RunProbe")]
    public void FramebufferCleanupPrecedesCallerStateRestore(string methodName)
    {
        using var module = ModuleDefinition.ReadModule(typeof(Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend).Assembly.Location);
        var type = module.GetType("Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend");
        var method = Assert.Single(type.Methods, m => m.Name == methodName);
        var deleteFramebuffer = Assert.Single(method.Body.Instructions, i =>
            i.Operand is MethodReference m && m.DeclaringType.FullName == "OpenTK.Graphics.OpenGL.GL" && m.Name == "DeleteFramebuffers");
        var restoreState = Assert.Single(method.Body.Instructions, i =>
            i.Operand is MethodReference m && m.Name == "Restore" && m.DeclaringType.Name == "GlStateSnapshot");

        Assert.True(deleteFramebuffer.Offset < restoreState.Offset,
            "Temporary framebuffer cleanup must happen before restoring the caller's framebuffer bindings.");
    }
}
#endif
