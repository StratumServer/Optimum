using System;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Xunit;

namespace Optimum.Tests;

public class WildcardReplaceTests
{
    [Fact]
    public void WildCardReplace_NullSafety_ReturnsNull()
    {
        var loc = new AssetLocation("game:rock-granite");
        var search = new AssetLocation("game:*-granite");
        var replace = new AssetLocation("game:*-andesite");

        Assert.Null(WildcardUtil.WildCardReplace(null!, search, replace));
        Assert.Null(WildcardUtil.WildCardReplace(loc, null!, replace));
        Assert.Null(WildcardUtil.WildCardReplace(loc, search, null!));

        Assert.Null(OptimumApiBridge.WildcardUtilWildCardReplace(null, search, replace));
        Assert.Null(OptimumApiBridge.WildcardUtilWildCardReplace(loc, null, replace));
        Assert.Null(OptimumApiBridge.WildcardUtilWildCardReplace(loc, search, null));
    }

    [Fact]
    public void WildCardReplace_SearchEqualsCode_ReturnsSearch()
    {
        var loc = new AssetLocation("game:rock-granite");
        var replace = new AssetLocation("game:*-andesite");

        var result = WildcardUtil.WildCardReplace(loc, loc, replace);
        Assert.Same(loc, result);

        var bridgeResult = OptimumApiBridge.WildcardUtilWildCardReplace(loc, loc, replace);
        Assert.Same(loc, bridgeResult);
    }

    [Fact]
    public void WildCardReplace_DomainMismatch_ReturnsNull()
    {
        var loc = new AssetLocation("game:rock-granite");
        var search = new AssetLocation("survival:*-granite");
        var replace = new AssetLocation("game:*-andesite");

        Assert.Null(WildcardUtil.WildCardReplace(loc, search, replace));
        Assert.Null(OptimumApiBridge.WildcardUtilWildCardReplace(loc, search, replace));
    }

    [Theory]
    [InlineData("game:trapdoor-up-north", "game:*-up-*", "game:*-down-*", "game:trapdoor-down-north")]
    [InlineData("game:trapdoor-up-north", "game:*-up-north", "game:*-down-south", "game:trapdoor-down-south")]
    [InlineData("game:trapdoor-up-north", "game:trapdoor-*", "game:door-*", "game:door-up-north")]
    [InlineData("game:rock-granite", "game:*-granite", "game:*-andesite", "game:rock-andesite")]
    [InlineData("game:rock-granite", "*:*-granite", "game:stone-*", "game:stone-rock")]
    public void WildCardReplace_Substitutions_ProduceExpectedResult(
        string codeStr, string searchStr, string replaceStr, string expectedStr)
    {
        var code = new AssetLocation(codeStr);
        var search = new AssetLocation(searchStr);
        var replace = new AssetLocation(replaceStr);
        var expected = new AssetLocation(expectedStr);

        var result = WildcardUtil.WildCardReplace(code, search, replace);
        Assert.NotNull(result);
        Assert.Equal(expected, result);

        var bridgeResult = OptimumApiBridge.WildcardUtilWildCardReplace(code, search, replace);
        Assert.NotNull(bridgeResult);
        Assert.Equal(expected, bridgeResult);
    }

    [Theory]
    [InlineData("game:trapdoor-up-north", "game:*-down-*", "game:*-south-*")]
    [InlineData("game:rock-granite", "game:*-andesite", "game:*-chert")]
    [InlineData("game:block-wood-oak", "game:item-*", "game:item-*")]
    public void WildCardReplace_NoMatch_ReturnsNull(string codeStr, string searchStr, string replaceStr)
    {
        var code = new AssetLocation(codeStr);
        var search = new AssetLocation(searchStr);
        var replace = new AssetLocation(replaceStr);

        Assert.Null(WildcardUtil.WildCardReplace(code, search, replace));
        Assert.Null(OptimumApiBridge.WildcardUtilWildCardReplace(code, search, replace));
    }

    [Fact]
    public void WildCardReplace_MultipleWildcards_ReplacesInSequence()
    {
        var code = new AssetLocation("game:block-oak-leaves-fall");
        var search = new AssetLocation("game:block-*-*-*");
        var replace = new AssetLocation("game:plant-*-*-*");

        var result = WildcardUtil.WildCardReplace(code, search, replace);
        Assert.NotNull(result);
        Assert.Equal(new AssetLocation("game:plant-oak-leaves-fall"), result);

        var bridgeResult = OptimumApiBridge.WildcardUtilWildCardReplace(code, search, replace);
        Assert.NotNull(bridgeResult);
        Assert.Equal(new AssetLocation("game:plant-oak-leaves-fall"), bridgeResult);
    }

    [Fact]
    public void WildcardUtil_ApiPatcher_RewritesWildCardReplaceToCallBridge()
    {
        using var module = ModuleDefinition.CreateModule("WildcardReplacePatchTest", ModuleKind.Dll);
        var wildcardUtil = new TypeDefinition(
            "Vintagestory.API.Util",
            "WildcardUtil",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(wildcardUtil);

        var assetLocationType = module.ImportReference(typeof(AssetLocation));
        var stringArrayType = module.ImportReference(typeof(string[]));

        var matchTwoArg = new MethodDefinition(
            "Match",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Boolean);
        matchTwoArg.Parameters.Add(new ParameterDefinition("needle", ParameterAttributes.None, assetLocationType));
        matchTwoArg.Parameters.Add(new ParameterDefinition("haystack", ParameterAttributes.None, assetLocationType));
        matchTwoArg.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldc_I4_0));
        matchTwoArg.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        wildcardUtil.Methods.Add(matchTwoArg);

        var matchThreeArg = new MethodDefinition(
            "Match",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Boolean);
        matchThreeArg.Parameters.Add(new ParameterDefinition("wildCard", ParameterAttributes.None, assetLocationType));
        matchThreeArg.Parameters.Add(new ParameterDefinition("inCode", ParameterAttributes.None, assetLocationType));
        matchThreeArg.Parameters.Add(new ParameterDefinition("allowedVariants", ParameterAttributes.None, stringArrayType));
        matchThreeArg.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldc_I4_0));
        matchThreeArg.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        wildcardUtil.Methods.Add(matchThreeArg);

        var wildCardReplace = new MethodDefinition(
            "WildCardReplace",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            assetLocationType);
        wildCardReplace.Parameters.Add(new ParameterDefinition("code", ParameterAttributes.None, assetLocationType));
        wildCardReplace.Parameters.Add(new ParameterDefinition("search", ParameterAttributes.None, assetLocationType));
        wildCardReplace.Parameters.Add(new ParameterDefinition("replace", ParameterAttributes.None, assetLocationType));
        wildCardReplace.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        wildCardReplace.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        wildcardUtil.Methods.Add(wildCardReplace);

        var matchTwoArgBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.WildcardUtilMatch), new[] { typeof(AssetLocation), typeof(AssetLocation) })!);
        var matchThreeArgBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.WildcardUtilMatch), new[] { typeof(AssetLocation), typeof(AssetLocation), typeof(string[]) })!);
        var wildCardReplaceBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.WildcardUtilWildCardReplace), new[] { typeof(AssetLocation), typeof(AssetLocation), typeof(AssetLocation) })!);

        int patched = Optimum.Patcher.ApiPatcher.PatchWildcardUtil(module, matchTwoArgBridge, matchThreeArgBridge, wildCardReplaceBridge);

        Assert.Equal(3, patched);
        Assert.Empty(wildCardReplace.Body.Variables);
        Assert.Collection(
            wildCardReplace.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_2, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(wildCardReplaceBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));
    }
}
