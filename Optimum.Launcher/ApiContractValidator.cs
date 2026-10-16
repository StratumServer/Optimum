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

    public static bool HasGuiCertificationContract(string assemblyPath)
    {
        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            var config = assembly.MainModule.GetType("Vintagestory.API.Config.OptimumConfig");
            return config?.Methods.Any(method => method.Name == "SetGuiGpuCertificationPassed" &&
                method.IsStatic && method.IsAssembly && method.ReturnType.FullName == "System.Void" &&
                method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.Boolean") == true &&
                config.Methods.Any(method => method.Name == "get_GuiGpuRecordingEligible" &&
                    method.IsStatic && method.ReturnType.FullName == "System.Boolean" && method.Parameters.Count == 0);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or AssemblyResolutionException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void EnsureGuiCertificationContract(string assemblyPath)
    {
        if (!HasGuiCertificationContract(assemblyPath))
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
