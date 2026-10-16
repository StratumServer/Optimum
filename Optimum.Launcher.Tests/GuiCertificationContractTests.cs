using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class GuiCertificationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedApiRequiresCertificationSetterAndRecordingEligibility(bool includeGate)
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-api-gate-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            using var module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
            var config = new TypeDefinition("Vintagestory.API.Config", "OptimumConfig", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(config);
            if (includeGate)
            {
                var setter = new MethodDefinition("SetGuiGpuCertificationPassed", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Void);
                setter.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
                setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                config.Methods.Add(setter);
                var getter = new MethodDefinition("get_GuiGpuRecordingEligible", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Boolean);
                getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                config.Methods.Add(getter);
            }
            module.Write(path);
            Assert.Equal(includeGate, ApiContractValidator.HasGuiCertificationContract(path));
        }
        finally { File.Delete(path); }
    }
}
