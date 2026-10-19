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
                setter.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                setter.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                setter.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                for (int i = 0; i < 4; i++) setter.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
                setter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                config.Methods.Add(setter);
                var matcher = new MethodDefinition("GuiGpuDeviceMatches", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Boolean);
                matcher.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                matcher.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                matcher.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                for (int i = 0; i < 4; i++) matcher.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
                matcher.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                matcher.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                config.Methods.Add(matcher);
                var revoke = new MethodDefinition("RevokeGuiGpuCertification", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Void);
                revoke.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                config.Methods.Add(revoke);
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

    [Fact]
    public void CachedApiResolvesCertificationContractThroughOptimumApiContractsTypeForward()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimum-api-forward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string apiPath = Path.Combine(directory, "VintagestoryAPI.dll");
        string contractsPath = Path.Combine(directory, "Optimum.Api.Contracts.dll");
        try
        {
            using var api = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
            var contractsName = new AssemblyNameReference("Optimum.Api.Contracts", new Version(1, 0, 0, 0));
            api.AssemblyReferences.Add(contractsName);
            api.ExportedTypes.Add(new ExportedType("Vintagestory.API.Config", "OptimumConfig", api, contractsName));
            api.Write(apiPath);

            using var contracts = ModuleDefinition.CreateModule("Optimum.Api.Contracts", ModuleKind.Dll);
            var config = new TypeDefinition("Vintagestory.API.Config", "OptimumConfig", TypeAttributes.Public, contracts.TypeSystem.Object);
            contracts.Types.Add(config);
            var setter = new MethodDefinition("SetGuiGpuCertificationPassed", MethodAttributes.Assembly | MethodAttributes.Static, contracts.TypeSystem.Void);
            setter.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.Boolean));
            setter.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            setter.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            setter.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            for (int i = 0; i < 4; i++) setter.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.Int32));
            config.Methods.Add(setter);
            var matcher = new MethodDefinition("GuiGpuDeviceMatches", MethodAttributes.Assembly | MethodAttributes.Static, contracts.TypeSystem.Boolean);
            matcher.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            matcher.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            matcher.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.String));
            for (int i = 0; i < 4; i++) matcher.Parameters.Add(new ParameterDefinition(contracts.TypeSystem.Int32));
            config.Methods.Add(matcher);
            config.Methods.Add(new MethodDefinition("RevokeGuiGpuCertification", MethodAttributes.Assembly | MethodAttributes.Static, contracts.TypeSystem.Void));
            config.Methods.Add(new MethodDefinition("get_GuiGpuRecordingEligible", MethodAttributes.Public | MethodAttributes.Static, contracts.TypeSystem.Boolean));
            contracts.Write(contractsPath);

            Assert.True(ApiContractValidator.HasGuiCertificationContract(apiPath, contractsPath));
            Assert.False(ApiContractValidator.HasGuiCertificationContract(apiPath, Path.Combine(directory, "missing.dll")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
