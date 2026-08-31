using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class CrashRecoveryCoverageTests
{
    [Fact]
    public void GearRendererGuardsMissingShapeAndRendererInBothPipelines()
    {
        string source = PatchReader.ReadPatch(
            "patches/VSSurvivalMod/Systems/TemporalStability/GearRenderer.cs.patch");
        string runtime = PatchReader.ReadPatch(
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/GearRenderer.cs.patch");

        Assert.Contains("if (shape == null) return;", source);
        Assert.Contains("if (tripodAnim.renderer == null)", source);
        Assert.Contains("optimumGearRendererDisabled || tripodAnim?.renderer == null", source);
        Assert.Contains("if (shape == null)", runtime);
        Assert.Contains("if (tripodAnim.renderer == null)", runtime);
        Assert.Contains("optimumGearRendererDisabled || tripodAnim?.renderer == null", runtime);
        Assert.Contains("DisableOptimumGearRenderer", source);
        Assert.Contains("DisableOptimumGearRenderer", runtime);
    }

    [Fact]
    public void GearRendererMethodsBelongToTheRuntimeTransplantManifest()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("Vintagestory.GameContent.GearRenderer", patcher);
        Assert.Contains("new(\"Vintagestory.GameContent.GearRenderer\", \"Init\", 0)", patcher);
        Assert.Contains("new(\"Vintagestory.GameContent.GearRenderer\", \"LoadShader\", 0)", patcher);
        Assert.Contains("new(\"Vintagestory.GameContent.GearRenderer\", \"OnRenderFrame\", 2)", patcher);
        Assert.Contains("new(\"Vintagestory.GameContent.GearRenderer\", \"updateSuperMechState\", 2)", patcher);
    }

    [Fact]
    public void OitRendererDisablesItsFeatureAfterAResourceFailure()
    {
        string source = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs.patch");
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("program.Disposed", source);
        Assert.Contains("programByName.Disposed", source);
        Assert.Contains("SystemRenderOITLayers.optimumOitDisabled", source);
        Assert.Contains("currentActiveShader.Use()", source);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT\", \"OnRenderFrame\", 2)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SystemRenderOITLayers/AfterOIT\", \"OnRenderFrame\", 2)", patcher);
        Assert.Contains("DisableOptimumOit", patcher);
    }

    [Fact]
    public void HeadControllerFallbackCoversSourceAndRuntimePipelines()
    {
        string headController = PatchReader.ReadPatch(
            "patches/VintagestoryApi/Common/Model/Animation/EntityHeadController.cs.patch");
        string entityPlayer = PatchReader.ReadPatch(
            "patches/VintagestoryApi/Common/Entity/EntityPlayer.cs.patch");
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("animationManager?.Animator?.GetPosebyName(name) ?? new ElementPose()", headController);
        Assert.Contains("AnimManager?.Animator != null", entityPlayer);
        Assert.Contains("OtherAnimManager?.Animator != null", entityPlayer);
        Assert.Contains("PatchHeadControllerPoseFallback", apiPatcher);
        Assert.Contains("OpCodes.Ldfld", apiPatcher);
        Assert.Contains("OpCodes.Newobj", apiPatcher);
    }

    [Fact]
    public void SharedAnimatorComparerRemainsOutsideTheRuntimePatch()
    {
        string apiPatcher = Read("Optimum.Patcher/api-patcher.cs");

        Assert.Contains("internal static int PatchAnimatorAnimCodeComparer", apiPatcher);
        Assert.DoesNotContain("PatchAnimatorAnimCodeComparer(vanilla.MainModule)", apiPatcher);
    }

    [Fact]
    public void BootstrapNormalizesPatchAndRuntimeInputs()
    {
        string attributes = Read(".gitattributes");
        string bootstrap = Read("scripts/bootstrap.sh");
        string bootstrapPowerShell = Read("scripts/bootstrap.ps1");
        string runtimeScript = Read("scripts/prepare-runtime-donors.sh");

        Assert.Contains("*.patch text eol=lf -whitespace", attributes);
        Assert.Contains("-name '*.patch'", bootstrap);
        Assert.Contains("normalize_lf \"$patches_dir\"", bootstrap);
        Assert.Contains("'*.patch'", bootstrapPowerShell);
        Assert.Contains("Convert-ToLf $patchesDir", bootstrapPowerShell);
        Assert.Contains("patches/runtime", runtimeScript);
        Assert.Contains("s/\\r/\\n/g", runtimeScript);
    }

    [Fact]
    public void RuntimeFailureAbortsInsteadOfLaunchingVanilla()
    {
        string program = Read("Optimum.Launcher/Program.cs");
        string loader = Read("Optimum.Launcher/AssemblyLoader.cs");
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("--validate-only", program);
        Assert.Contains("ValidatePatchedRuntime", program);
        Assert.Contains("AcquireLaunchLock", program);
        Assert.Contains("FileShare.None", program);
        Assert.Contains("game.lock", program);
        Assert.Contains("PrepareMethod", program);
        Assert.Contains("TryInvalidate", program);
        Assert.Contains("Launch aborted", program);
        Assert.Contains("TryRestoreVanillaMods", program);
        Assert.DoesNotContain("LaunchVanillaFallback", program);
        Assert.Contains("RequiredPatchedAssemblies", loader);
        Assert.Contains("Required patched assembly is missing", loader);
        Assert.Contains("if (total < 0)", patcher);
        Assert.Contains("failed output validation", patcher);
        Assert.Contains("requireAllTargets: true", patcher);
        Assert.Contains("Optional: true", patcher);
        Assert.Contains("TargetDeclaringType", Read("Optimum.Patcher/Program.cs"));
        Assert.Contains("targetParameterTypes", Read("Optimum.Patcher/ILHook.cs"));
        Assert.Contains("TargetReturnType", Read("Optimum.Patcher/ILPatcher.cs"));
        Assert.Contains("TargetCallingConvention", Read("Optimum.Patcher/ILPatcher.cs"));
        Assert.Contains("IlStackVerifier.VerifyModule", Read("Optimum.Patcher/ILPatcher.cs"));
    }

    [Fact]
    public void WindowsInstallerValidatesTheStagedRuntimeBeforeCopyingIt()
    {
        string installer = Read("scripts/install-windows.ps1");

        Assert.Contains("Assert-ILSpyVersion", installer);
        Assert.Contains("Invoke-RuntimePreflight", installer);
        Assert.Contains("--validate-only", installer);
        Assert.Contains("The package was not installed", installer);
        Assert.Contains("package-complete", installer);
        Assert.Contains("Install-StagedPackage", installer);
        Assert.Contains("rollback", installer);
    }

    [Fact]
    public void WindowsPackageShipsAStandaloneDelayedUninstaller()
    {
        string package = Read("scripts/package.ps1");
        string installer = Read("scripts/install-windows.ps1");
        string uninstaller = Read("scripts/uninstall.ps1");

        Assert.Contains("scripts/uninstall.ps1", package);
        Assert.Contains("standalone-install", package);
        Assert.Contains("-File \"", installer);
        Assert.Contains("-WindowStyle Hidden", installer);
        Assert.Contains("rmdir /s /q", uninstaller);
        Assert.Contains("Start-Process", uninstaller);
        Assert.Contains("Vintage Story was not modified", uninstaller);
        Assert.Contains("$batchLog", uninstaller);
        Assert.Contains("$failedPaths", uninstaller);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}

public sealed class HeadControllerPoseFallbackTests
{
    [Fact]
    public void CecilPatchReplacesUnsafePoseLookupWithAnInertFallback()
    {
        var fixture = CreateFixture();

        int patched = ApiPatcher.PatchHeadControllerPoseFallback(fixture.Module);

        Assert.Equal(1, patched);
        Assert.Contains(fixture.GetPose.Body.Instructions, instruction => instruction.OpCode == OpCodes.Brfalse);
        Assert.Contains(fixture.GetPose.Body.Instructions, instruction =>
            instruction.OpCode == OpCodes.Newobj &&
            instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == fixture.ElementPose.FullName);
        Assert.Contains(fixture.GetPose.Body.Instructions, instruction =>
            instruction.Operand is MethodReference method && method.Name == "GetPosebyName");
        Assert.DoesNotContain(fixture.GetPose.Body.Instructions, instruction =>
            instruction.Operand is MethodReference method && method.DeclaringType.FullName == "System.InvalidOperationException");
        Assert.Empty(IlStackVerifier.VerifyMethod(fixture.GetPose));

        fixture.GetPose.Body.Instructions.Clear();
        fixture.GetPose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        Assert.Contains("stack underflow", string.Join("\n", IlStackVerifier.VerifyMethod(fixture.GetPose)));
    }

    private static Fixture CreateFixture()
    {
        var module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
        var elementPose = new TypeDefinition(
            "Vintagestory.API.Common",
            "ElementPose",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(elementPose);

        var elementPoseCtor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        elementPose.Methods.Add(elementPoseCtor);
        var elementPoseCtorIl = elementPoseCtor.Body.GetILProcessor();
        elementPoseCtorIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        elementPoseCtorIl.Append(Instruction.Create(
            OpCodes.Call,
            module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
        elementPoseCtorIl.Append(Instruction.Create(OpCodes.Ret));

        var animator = new TypeDefinition(
            "Vintagestory.API.Common",
            "IAnimator",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
            module.TypeSystem.Object);
        module.Types.Add(animator);
        var getPoseByName = new MethodDefinition(
            "GetPosebyName",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
            MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            elementPose);
        getPoseByName.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        getPoseByName.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(StringComparison))));
        animator.Methods.Add(getPoseByName);

        var animationManager = new TypeDefinition(
            "Vintagestory.API.Common",
            "IAnimationManager",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
            module.TypeSystem.Object);
        module.Types.Add(animationManager);
        var animatorGetter = new MethodDefinition(
            "get_Animator",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            animator);
        animationManager.Methods.Add(animatorGetter);
        animationManager.Properties.Add(new PropertyDefinition("Animator", PropertyAttributes.None, animator)
        {
            GetMethod = animatorGetter,
        });

        var controller = new TypeDefinition(
            "Vintagestory.API.Common",
            "EntityHeadController",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(controller);
        var animationManagerField = new FieldDefinition(
            "animationManager",
            FieldAttributes.Private,
            animationManager);
        controller.Fields.Add(animationManagerField);

        var getPose = new MethodDefinition(
            "GetPose",
            MethodAttributes.Family | MethodAttributes.HideBySig,
            elementPose);
        getPose.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        controller.Methods.Add(getPose);
        var getPoseIl = getPose.Body.GetILProcessor();
        getPoseIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        getPoseIl.Append(Instruction.Create(OpCodes.Ldfld, animationManagerField));
        getPoseIl.Append(Instruction.Create(OpCodes.Callvirt, animatorGetter));
        getPoseIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        getPoseIl.Append(Instruction.Create(
            OpCodes.Ldc_I4,
            (int)StringComparison.InvariantCultureIgnoreCase));
        getPoseIl.Append(Instruction.Create(OpCodes.Callvirt, getPoseByName));
        getPoseIl.Append(Instruction.Create(OpCodes.Ret));

        return new Fixture(module, elementPose, getPose);
    }

    private sealed record Fixture(ModuleDefinition Module, TypeDefinition ElementPose, MethodDefinition GetPose);
}
