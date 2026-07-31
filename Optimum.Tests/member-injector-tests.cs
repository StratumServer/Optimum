using System;
using System.Collections.Generic;
using Mono.Cecil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

public sealed class MemberInjectorTests
{
    [Fact]
    public void MissingRequiredTypeFailsThePatch()
    {
        using AssemblyDefinition vanilla = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("vanilla", new Version(1, 0)), "vanilla", ModuleKind.Dll);
        using AssemblyDefinition compiled = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("compiled", new Version(1, 0)), "compiled", ModuleKind.Dll);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => MemberInjector.InjectTypes(vanilla, compiled, new List<string> { "Optimum.RequiredType" }));

        Assert.Contains("Optimum.RequiredType", exception.Message);
    }
}
