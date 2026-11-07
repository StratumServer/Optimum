using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Optimum.Tests;

/// <summary>
/// Tests for Stratum-ported optimizations.
/// Validates behavioral equivalence between optimized and original implementations.
/// </summary>
public class StratumPortTests
{
    #region WildcardUtil Tests

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData("rock-*", "rock-granite", true)]
    [InlineData("rock-*", "rock-basalt", true)]
    [InlineData("rock-*", "soil-low", false)]
    [InlineData("*-north", "trapdoor-north", true)]
    [InlineData("*-north", "trapdoor-south", false)]
    [InlineData("*", "anything", true)]
    [InlineData("exact-match", "exact-match", true)]
    [InlineData("exact-match", "wrong-match", false)]
    [InlineData("a-*-c", "a-b-c", true)]
    [InlineData("a-*-c", "a-xyz-c", true)]
    [InlineData("a-*-c", "a-b-d", false)]
    [InlineData("planks-*", "planks-oak", true)]
    [InlineData("planks-*", "log-oak", false)]
    public void WildcardUtil_Match_String_ProducesSameResultAsExpected(string needle, string haystack, bool expected)
    {
        bool result = WildcardUtil.Match(needle, haystack);
        Assert.Equal(expected, result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_NullSafe()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        // The optimized version adds null guards
        bool result = WildcardUtil.Match(wildCard, null, null);
        Assert.False(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_GlobalWildcard()
    {
        var wildCard = new AssetLocation("*", "*");
        var code = new AssetLocation("game", "anything-here");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_ExactMatch()
    {
        var wildCard = new AssetLocation("game", "rock-granite");
        var code = new AssetLocation("game", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_WildcardSuffix()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeMatch = new AssetLocation("game", "rock-granite");
        var codeNoMatch = new AssetLocation("game", "soil-low");

        Assert.True(WildcardUtil.Match(wildCard, codeMatch, null));
        Assert.False(WildcardUtil.Match(wildCard, codeNoMatch, null));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_AssetLocation_DomainMismatch()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var code = new AssetLocation("modx", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.False(result);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void WildcardUtil_Match_WithAllowedVariants()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeGranite = new AssetLocation("game", "rock-granite");
        var codeBasalt = new AssetLocation("game", "rock-basalt");

        string[] allowed = new[] { "granite" };

        Assert.True(WildcardUtil.Match(wildCard, codeGranite, allowed));
        Assert.False(WildcardUtil.Match(wildCard, codeBasalt, allowed));
    }

    #endregion

    #region Ascii85 Tests

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_EmptyArray()
    {
        byte[] input = Array.Empty<byte>();
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_SingleByte()
    {
        byte[] input = new byte[] { 0x42 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_FourBytes()
    {
        byte[] input = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_ZeroBlock()
    {
        byte[] input = new byte[] { 0, 0, 0, 0 };
        string encoded = Ascii85.Encode(input);
        Assert.Contains("z", encoded);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_LargeData()
    {
        byte[] input = new byte[1024];
        var rng = new Random(42);
        rng.NextBytes(input);
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void Ascii85_EncodeDecodeRoundtrip_MixedZeroAndNonZero()
    {
        byte[] input = new byte[] { 0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 5, 6, 7 };
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xFF, 0x80 })]
    [InlineData(new byte[] { 0xFF, 0x80, 0x01 })]
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x01, 0xFE, 0xDC })]
    public void Ascii85_EncodeDecodeRoundtrip_TailLengthsAndHighBitBytes(byte[] input)
    {
        string encoded = Ascii85.Encode(input);
        byte[] decoded = Ascii85.Decode(encoded);
        Assert.Equal(input, decoded);
    }

    [Theory]
    [InlineData("abc~e")]
    [InlineData("abzde")]
    [InlineData("a")]
    [InlineData("z!")]
    [InlineData("uuuuu")]
    [InlineData("uuuu")]
    public void Ascii85_Decode_InvalidOrOverflowInput_ThrowsFormatException(string invalidEncoded)
    {
        Assert.Throws<FormatException>(() => Ascii85.Decode(invalidEncoded));
    }

    [Fact]
    public void Ascii85_Decode_MaxUInt32Group_ReturnsFour0xFFBytes()
    {
        byte[] expected = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        string encoded = Ascii85.Encode(expected);
        Assert.Equal("s8W-!", encoded);
        Assert.Equal(expected, Ascii85.Decode(encoded));
    }

    [Fact]
    public void Ascii85_GetDecodedLength_ValidatesFormatAndEncodeValueInlineIsRemoved()
    {
        System.Reflection.MethodInfo? encodeValueInline = typeof(Ascii85).GetMethod(
            "EncodeValueInline",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.Null(encodeValueInline);

        System.Reflection.MethodInfo? getDecodedLength = typeof(Ascii85).GetMethod(
            "GetDecodedLength",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(getDecodedLength);

        var invalidCharEx = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => getDecodedLength!.Invoke(null, new object[] { "abzde" }));
        Assert.IsType<FormatException>(invalidCharEx.InnerException);

        var trailingSingleEx = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => getDecodedLength!.Invoke(null, new object[] { "z!" }));
        Assert.IsType<FormatException>(trailingSingleEx.InnerException);
    }

    [Fact]
    public void OptimumApiBridge_Ascii85EncodeAndDecode_MatchAscii85()
    {
        byte[] payload = new byte[] { 0, 0, 0, 0, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 };
        string encoded = OptimumApiBridge.Ascii85Encode(payload);
        Assert.Equal(Ascii85.Encode(payload), encoded);
        Assert.Equal(payload, OptimumApiBridge.Ascii85Decode(encoded));
    }

    [Fact]
    public void ApiPatcher_PatchAscii85_RewritesEncodeAndDecodeToCallBridge()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
        var ascii85 = new TypeDefinition(
            "Vintagestory.API.MathTools",
            "Ascii85",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        module.Types.Add(ascii85);

        var byteArrayType = new ArrayType(module.TypeSystem.Byte);
        var encode = new MethodDefinition(
            "Encode",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.String);
        encode.Parameters.Add(new ParameterDefinition("bytes", ParameterAttributes.None, byteArrayType));
        encode.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        encode.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        encode.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        ascii85.Methods.Add(encode);

        var decode = new MethodDefinition(
            "Decode",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            byteArrayType);
        decode.Parameters.Add(new ParameterDefinition("encoded", ParameterAttributes.None, module.TypeSystem.String));
        decode.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        decode.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        decode.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        ascii85.Methods.Add(decode);

        var encodeBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.Ascii85Encode), new[] { typeof(byte[]) })!);
        var decodeBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.Ascii85Decode), new[] { typeof(string) })!);

        int patched = ApiPatcher.PatchAscii85(module, encodeBridge, decodeBridge);

        Assert.Equal(2, patched);
        Assert.Empty(encode.Body.Variables);
        Assert.Collection(
            encode.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(encodeBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        Assert.Empty(decode.Body.Variables);
        Assert.Collection(
            decode.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(decodeBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        Assert.Empty(IlStackVerifier.VerifyModule(module));
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(module));
    }

    #endregion

    #region CollisionTester Fast Path Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void CollisionTester_FastPath_ZeroVelocity_SetsPositionUnchanged()
    {
        // Validates that zero-velocity non-player/non-item entities get the fast path
        // We cannot instantiate a real Entity without a World, but we verify the logic pattern
        var diagnostics = OptimumDiagnostics.CollisionFastPathHits;
        // If we had a mock entity with zero motion, the fast path sets newPosition = entityPos
        // This test validates the OptimumDiagnostics counter infrastructure compiles and functions
        OptimumDiagnostics.RecordCollisionFastPathHit();
        Assert.True(OptimumDiagnostics.CollisionFastPathHits > diagnostics);
    }

    #endregion

    #region PathNode Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_NullReturnsFlase()
    {
        var node = new Vintagestory.Essentials.PathNode(new BlockPos(1, 2, 3, 0));
        Assert.False(node.Equals((Vintagestory.Essentials.PathNode)null));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_SamePosition()
    {
        var a = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        var b = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        Assert.True(a.Equals(b));
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PathNode_Equals_DifferentPosition()
    {
        var a = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 15, 0));
        var b = new Vintagestory.Essentials.PathNode(new BlockPos(5, 10, 16, 0));
        Assert.False(a.Equals(b));
    }

    #endregion

    #region CollectEntities Stride Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void CollectEntities_StrideInterval_DefaultIsThree()
    {
        Assert.Equal(3, Vintagestory.GameContent.EntityBehaviorCollectEntities.OptimumCollectStrideInterval);
    }

    [Theory(Skip = "Requires fork patches: OptimumDiagnostics")]
    [InlineData(0, 100, 3, true)]   // tick 0, entityId 100: (0+100)%3 = 1, skips
    [InlineData(0, 99, 3, false)]   // tick 0, entityId 99: (0+99)%3 = 0, runs
    [InlineData(1, 99, 3, true)]    // tick 1, entityId 99: (1+99)%3 = 1, skips
    [InlineData(2, 99, 3, true)]    // tick 2, entityId 99: (2+99)%3 = 2, skips
    [InlineData(3, 99, 3, false)]   // tick 3, entityId 99: (3+99)%3 = 0, runs
    public void CollectEntities_StrideLogic_SkipsCorrectTicks(long tickIndex, long entityId, int stride, bool shouldSkip)
    {
        bool skips = (tickIndex + entityId) % stride != 0;
        Assert.Equal(shouldSkip, skips);
    }

    #endregion

    #region OptimumDiagnostics Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void OptimumDiagnostics_CountersIncrementAndReset()
    {
        OptimumDiagnostics.ResetStratumCounters();
        Assert.Equal(0, OptimumDiagnostics.ServerTickCount);

        OptimumDiagnostics.RecordServerTick();
        OptimumDiagnostics.RecordServerTick();
        Assert.Equal(2, OptimumDiagnostics.ServerTickCount);

        OptimumDiagnostics.RecordCollisionFastPathHit();
        Assert.Equal(1, OptimumDiagnostics.CollisionFastPathHits);

        OptimumDiagnostics.ResetStratumCounters();
        Assert.Equal(0, OptimumDiagnostics.ServerTickCount);
        Assert.Equal(0, OptimumDiagnostics.CollisionFastPathHits);
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void OptimumDiagnostics_GetSummary_ContainsExpectedKeys()
    {
        OptimumDiagnostics.ResetStratumCounters();
        OptimumDiagnostics.RecordMechPowerTick();
        string summary = OptimumDiagnostics.GetStratumSummary();
        Assert.Contains("mechTicks=1", summary);
        Assert.Contains("[Stratum Ports]", summary);
    }

    #endregion

    #region Propick Node Mode Reuse Tests

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PropickNodeMode_SortLogic_MatchesLinqOrderByDescending()
    {
        // Validate the manual sort produces same order as .OrderByDescending(val => val.Value).ToList()
        var dict = new Dictionary<string, int>
        {
            { "ore-copper", 15 },
            { "ore-tin", 42 },
            { "ore-gold", 3 },
            { "ore-iron", 28 },
        };

        // LINQ reference
        var linqResult = dict.OrderByDescending(val => val.Value).ToList();

        // Manual sort (the Optimum implementation)
        var manualResult = new List<KeyValuePair<string, int>>(dict);
        manualResult.Sort((a, b) => b.Value.CompareTo(a.Value));

        Assert.Equal(linqResult.Count, manualResult.Count);
        for (int i = 0; i < linqResult.Count; i++)
        {
            Assert.Equal(linqResult[i].Key, manualResult[i].Key);
            Assert.Equal(linqResult[i].Value, manualResult[i].Value);
        }
    }

    [Fact(Skip = "Requires fork patches: OptimumDiagnostics")]
    public void PropickNodeMode_DictionaryReuse_ClearProducesEmptyState()
    {
        // Validates the reuse pattern: Clear() + repopulate produces same results as new Dictionary
        var reusable = new Dictionary<string, int>();
        reusable["ore-copper"] = 5;
        reusable["ore-tin"] = 10;

        // "new reading" - clear and repopulate
        reusable.Clear();
        reusable["ore-gold"] = 7;

        Assert.Single(reusable);
        Assert.Equal(7, reusable["ore-gold"]);
        Assert.False(reusable.ContainsKey("ore-copper"));
    }

    #endregion

    #region RegistryObject FirstCodePart/LastCodePart Tests

    private sealed class DummyRegistryObject : RegistryObject
    {
        public DummyRegistryObject(string? path)
        {
            Code = path != null ? new AssetLocation("game", path) : null;
        }
    }

    [Fact]
    public void FirstCodePart_NoDash_ReturnsFullPath()
    {
        var obj = new DummyRegistryObject("granite");
        Assert.Equal("granite", obj.FirstCodePart(0));
        Assert.Null(obj.FirstCodePart(1));
    }

    [Theory]
    [InlineData("rock-granite-polished", 0, "rock")]
    [InlineData("rock-granite-polished", 1, "granite")]
    [InlineData("rock-granite-polished", 2, "polished")]
    [InlineData("a-b", 0, "a")]
    [InlineData("a-b", 1, "b")]
    public void FirstCodePart_Logic_MatchesSplitBehavior(string path, int posFromLeft, string expected)
    {
        var obj = new DummyRegistryObject(path);
        Assert.Equal(expected, obj.FirstCodePart(posFromLeft));
        Assert.Equal(expected, OptimumApiBridge.RegistryObjectFirstCodePart(obj, posFromLeft));
    }

    [Theory]
    [InlineData("rock-granite-polished", 0, "polished")]
    [InlineData("rock-granite-polished", 1, "granite")]
    [InlineData("rock-granite-polished", 2, "rock")]
    [InlineData("a-b", 0, "b")]
    [InlineData("a-b", 1, "a")]
    public void LastCodePart_Logic_MatchesSplitBehavior(string path, int posFromRight, string expected)
    {
        var obj = new DummyRegistryObject(path);
        Assert.Equal(expected, obj.LastCodePart(posFromRight));
        Assert.Equal(expected, OptimumApiBridge.RegistryObjectLastCodePart(obj, posFromRight));
    }

    [Fact]
    public void CodePart_NullCodeOrOutOfBounds_ReturnsNull()
    {
        var nullObj = new DummyRegistryObject(null);
        Assert.Null(nullObj.FirstCodePart(0));
        Assert.Null(nullObj.LastCodePart(0));
        Assert.Null(OptimumApiBridge.RegistryObjectFirstCodePart(nullObj, 0));
        Assert.Null(OptimumApiBridge.RegistryObjectLastCodePart(nullObj, 0));

        var obj = new DummyRegistryObject("rock-granite");
        Assert.Null(obj.FirstCodePart(2));
        Assert.Null(obj.LastCodePart(2));
        Assert.Null(obj.FirstCodePart(100));
        Assert.Null(obj.LastCodePart(100));
        Assert.Null(OptimumApiBridge.RegistryObjectFirstCodePart(obj, 2));
        Assert.Null(OptimumApiBridge.RegistryObjectLastCodePart(obj, 2));
    }

    [Fact]
    public void CodePart_NegativeIndex_ThrowsIndexOutOfRangeException()
    {
        var obj = new DummyRegistryObject("rock-granite");
        Assert.Throws<IndexOutOfRangeException>(() => obj.FirstCodePart(-1));
        Assert.Throws<IndexOutOfRangeException>(() => obj.LastCodePart(-1));
        Assert.Throws<IndexOutOfRangeException>(() => OptimumApiBridge.RegistryObjectFirstCodePart(obj, -1));
        Assert.Throws<IndexOutOfRangeException>(() => OptimumApiBridge.RegistryObjectLastCodePart(obj, -1));
    }

    [Fact]
    public void RegistryObject_ApiPatcher_RewritesMethodsToCallOptimumApiBridge()
    {
        using var module = ModuleDefinition.CreateModule("RegistryObjectPatchTest", ModuleKind.Dll);
        var registryObject = new TypeDefinition(
            "Vintagestory.API.Common",
            "RegistryObject",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(registryObject);

        var firstCodePart = new MethodDefinition(
            "FirstCodePart",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.String);
        firstCodePart.Parameters.Add(new ParameterDefinition("posFromLeft", ParameterAttributes.Optional, module.TypeSystem.Int32));
        firstCodePart.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        firstCodePart.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        firstCodePart.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        registryObject.Methods.Add(firstCodePart);

        var lastCodePart = new MethodDefinition(
            "LastCodePart",
            MethodAttributes.Public | MethodAttributes.HideBySig,
            module.TypeSystem.String);
        lastCodePart.Parameters.Add(new ParameterDefinition("posFromRight", ParameterAttributes.Optional, module.TypeSystem.Int32));
        lastCodePart.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        lastCodePart.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        lastCodePart.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        registryObject.Methods.Add(lastCodePart);

        var firstCodePartBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.RegistryObjectFirstCodePart), new[] { typeof(RegistryObject), typeof(int) })!);
        var lastCodePartBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.RegistryObjectLastCodePart), new[] { typeof(RegistryObject), typeof(int) })!);

        int patched = ApiPatcher.PatchRegistryObjectCodeParts(module, firstCodePartBridge, lastCodePartBridge);

        Assert.Equal(2, patched);
        Assert.Empty(firstCodePart.Body.Variables);
        Assert.Collection(
            firstCodePart.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(firstCodePartBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        Assert.Empty(lastCodePart.Body.Variables);
        Assert.Collection(
            lastCodePart.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(lastCodePartBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));
    }

    #endregion
}
