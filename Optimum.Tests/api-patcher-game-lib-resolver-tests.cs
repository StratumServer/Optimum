using System;
using System.IO;
using Mono.Cecil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The Optimum CLI patches VintagestoryAPI.dll from the pristine copy in
/// &lt;game&gt;/.optimum/vanilla, not from the game root. The game's own Lib folder
/// (cairo-sharp.dll, for example) is then two levels above the input, so the API
/// patcher's resolver must search it explicitly or Cecil throws
/// AssemblyResolutionException when a patch needs a type from that assembly.
/// </summary>
public sealed class ApiPatcherGameLibResolverTests : IDisposable
{
    // A name no test dependency shares: the resolver also searches the current
    // directory, where the real cairo-sharp.dll sits next to the test assembly.
    private const string GameLibAssembly = "optimum-test-game-lib";

    private readonly string _gameDir = Directory.CreateTempSubdirectory("optimum-api-resolver-").FullName;

    public void Dispose() => Directory.Delete(_gameDir, recursive: true);

    [Fact]
    public void ResolverFindsGameLibAssemblyWhenTheInputIsTheVanillaBackup()
    {
        WriteAssembly(Path.Combine(_gameDir, "Lib", GameLibAssembly + ".dll"), GameLibAssembly);
        string backupApi = WriteAssembly(Path.Combine(_gameDir, ".optimum", "vanilla", "VintagestoryAPI.dll"), "VintagestoryAPI");
        string contracts = WriteAssembly(Path.Combine(_gameDir, "overlay", "Optimum.Api.Contracts.dll"), "Optimum.Api.Contracts");

        var resolver = ApiPatcher.CreateResolver(backupApi, contracts);

        AssemblyDefinition resolved = resolver.Resolve(new AssemblyNameReference(GameLibAssembly, new Version(1, 0, 0, 0)));
        Assert.Equal(GameLibAssembly, resolved.Name.Name);
    }

    [Fact]
    public void ResolverStillFindsGameLibAssemblyWhenTheInputIsInTheGameRoot()
    {
        WriteAssembly(Path.Combine(_gameDir, "Lib", GameLibAssembly + ".dll"), GameLibAssembly);
        string api = WriteAssembly(Path.Combine(_gameDir, "VintagestoryAPI.dll"), "VintagestoryAPI");
        string contracts = WriteAssembly(Path.Combine(_gameDir, "overlay", "Optimum.Api.Contracts.dll"), "Optimum.Api.Contracts");

        var resolver = ApiPatcher.CreateResolver(api, contracts);

        AssemblyDefinition resolved = resolver.Resolve(new AssemblyNameReference(GameLibAssembly, new Version(1, 0, 0, 0)));
        Assert.Equal(GameLibAssembly, resolved.Name.Name);
    }

    private static string WriteAssembly(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        assembly.Write(path);
        return path;
    }
}
