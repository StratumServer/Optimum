using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace Optimum.Launcher;

/// <summary>
/// Checks the API member that compiled runtime donors require before the game loads them.
/// Cecil reads metadata without loading the game assembly into the launcher's process.
/// </summary>
public static class ApiContractValidator
{
    private const string ClientApiType = "Vintagestory.API.Client.ICoreClientAPI";
    private const string TesselationThreadMethod = "IsTesselationThread";

    public static bool HasTesselationThreadContract(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return false;

        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(
                assemblyPath,
                new ReaderParameters { ReadSymbols = false });
            var clientApi = assembly.MainModule.GetType(ClientApiType);
            return clientApi?.Methods.Any(IsTesselationThreadMethod) == true;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or
                                   AssemblyResolutionException or
                                   System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool HasGuiCertificationContract(string assemblyPath, string? contractsAssemblyPath = null)
    {
        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var config = assembly.MainModule.GetType("Vintagestory.API.Config.OptimumConfig");
            if (config == null)
            {
                bool forwarded = assembly.MainModule.ExportedTypes.Any(type =>
                    type.FullName == "Vintagestory.API.Config.OptimumConfig" &&
                    type.Scope is AssemblyNameReference reference && reference.Name == "Optimum.Api.Contracts");
                if (!forwarded) return false;
                contractsAssemblyPath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!, "Optimum.Api.Contracts.dll");
                if (!File.Exists(contractsAssemblyPath)) return false;
                using var contracts = AssemblyDefinition.ReadAssembly(contractsAssemblyPath);
                return HasGuiCertificationMethods(contracts.MainModule.GetType("Vintagestory.API.Config.OptimumConfig"));
            }
            return HasGuiCertificationMethods(config);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or AssemblyResolutionException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasGuiCertificationMethods(TypeDefinition? config) =>
            config?.Methods.Any(method => method.Name == "SetGuiGpuCertificationPassed" &&
                method.IsStatic && method.IsAssembly && method.ReturnType.FullName == "System.Void" &&
                method.Parameters.Count == 8 && method.Parameters[0].ParameterType.FullName == "System.Boolean" &&
                method.Parameters.Skip(1).Take(3).All(parameter => parameter.ParameterType.FullName == "System.String") &&
                method.Parameters.Skip(4).All(parameter => parameter.ParameterType.FullName == "System.Int32")) == true &&
                config.Methods.Any(method => method.Name == "GuiGpuDeviceMatches" && method.IsStatic && method.IsAssembly &&
                    method.ReturnType.FullName == "System.Boolean" && method.Parameters.Count == 7 &&
                    method.Parameters.Take(3).All(parameter => parameter.ParameterType.FullName == "System.String") &&
                    method.Parameters.Skip(3).All(parameter => parameter.ParameterType.FullName == "System.Int32")) &&
                config.Methods.Any(method => method.Name == "RevokeGuiGpuCertification" && method.IsStatic && method.IsAssembly &&
                    method.ReturnType.FullName == "System.Void" && method.Parameters.Count == 0) &&
                config.Methods.Any(method => method.Name == "get_GuiGpuRecordingEligible" &&
                    method.IsStatic && method.ReturnType.FullName == "System.Boolean" && method.Parameters.Count == 0);

    public static void EnsureGuiCertificationContract(string assemblyPath, string? contractsAssemblyPath = null)
    {
        if (!HasGuiCertificationContract(assemblyPath, contractsAssemblyPath))
        {
            throw new InvalidDataException(
                "VintagestoryAPI.dll lacks the Optimum GUI certification gate: " + assemblyPath);
        }
    }

    public static void EnsureTesselationThreadContract(string assemblyPath)
    {
        if (!HasTesselationThreadContract(assemblyPath))
        {
            throw new InvalidDataException(
                $"VintagestoryAPI.dll lacks {ClientApiType}.{TesselationThreadMethod}(int): " +
                $"{assemblyPath}");
        }
    }

    private static bool IsTesselationThreadMethod(MethodDefinition method)
    {
        return method.Name == TesselationThreadMethod &&
            !method.IsStatic &&
            method.ReturnType.FullName == "System.Boolean" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == "System.Int32";
    }
}
