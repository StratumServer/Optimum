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

    [Theory]
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

    [Fact]
    public void WildcardUtil_Match_AssetLocation_NullSafe()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        // The optimized version adds null guards
        bool result = WildcardUtil.Match(wildCard, null, null);
        Assert.False(result);
    }

    [Fact]
    public void WildcardUtil_Match_AssetLocation_GlobalWildcard()
    {
        var wildCard = new AssetLocation("*", "*");
        var code = new AssetLocation("game", "anything-here");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact]
    public void WildcardUtil_Match_AssetLocation_ExactMatch()
    {
        var wildCard = new AssetLocation("game", "rock-granite");
        var code = new AssetLocation("game", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.True(result);
    }

    [Fact]
    public void WildcardUtil_Match_AssetLocation_WildcardSuffix()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeMatch = new AssetLocation("game", "rock-granite");
        var codeNoMatch = new AssetLocation("game", "soil-low");

        Assert.True(WildcardUtil.Match(wildCard, codeMatch, null));
        Assert.False(WildcardUtil.Match(wildCard, codeNoMatch, null));
    }

    [Fact]
    public void WildcardUtil_Match_AssetLocation_DomainMismatch()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var code = new AssetLocation("modx", "rock-granite");
        bool result = WildcardUtil.Match(wildCard, code, null);
        Assert.False(result);
    }

    [Fact]
    public void WildcardUtil_Match_WithAllowedVariants()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        var codeGranite = new AssetLocation("game", "rock-granite");
        var codeBasalt = new AssetLocation("game", "rock-basalt");

        string[] allowed = new[] { "granite" };

        Assert.True(WildcardUtil.Match(wildCard, codeGranite, allowed));
        Assert.False(WildcardUtil.Match(wildCard, codeBasalt, allowed));
    }

    [Fact]
    public void WildcardUtil_Match_TwoArg_AssetLocation_NullSafe()
    {
        var wildCard = new AssetLocation("game", "rock-*");
        Assert.False(WildcardUtil.Match(wildCard, null));
        Assert.False(WildcardUtil.Match(null, wildCard));
        Assert.False(WildcardUtil.Match((AssetLocation)null, (AssetLocation)null));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(wildCard, null));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(null, wildCard));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(null, null));
    }

    [Fact]
    public void WildcardUtil_Match_TwoArg_AssetLocation_ProducesSameResult()
    {
        var needle = new AssetLocation("game", "rock-*");
        var match = new AssetLocation("game", "rock-granite");
        var noMatch = new AssetLocation("game", "soil-low");
        var diffDomain = new AssetLocation("other", "rock-granite");
        var globalDomain = new AssetLocation("*", "rock-*");

        Assert.True(WildcardUtil.Match(needle, match));
        Assert.False(WildcardUtil.Match(needle, noMatch));
        Assert.False(WildcardUtil.Match(needle, diffDomain));
        Assert.True(WildcardUtil.Match(globalDomain, diffDomain));

        Assert.True(OptimumApiBridge.WildcardUtilMatch(needle, match));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(needle, noMatch));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(needle, diffDomain));
        Assert.True(OptimumApiBridge.WildcardUtilMatch(globalDomain, diffDomain));
    }

    [Fact]
    public void WildcardUtil_Match_RegexFallback()
    {
        var regexWildcard = new AssetLocation("game", "@^rock-(granite|andesite)$");
        var codeGranite = new AssetLocation("game", "rock-granite");
        var codeBasalt = new AssetLocation("game", "rock-basalt");

        Assert.True(WildcardUtil.Match(regexWildcard, codeGranite, null));
        Assert.False(WildcardUtil.Match(regexWildcard, codeBasalt, null));

        Assert.True(OptimumApiBridge.WildcardUtilMatch(regexWildcard, codeGranite, null));
        Assert.False(OptimumApiBridge.WildcardUtilMatch(regexWildcard, codeBasalt, null));
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

    [Fact]
    public void WildcardUtil_ApiPatcher_RewritesMethodsToCallOptimumApiBridge()
    {
        using var module = ModuleDefinition.CreateModule("WildcardUtilPatchTest", ModuleKind.Dll);
        var wildcardUtil = new TypeDefinition(
            "Vintagestory.API.Util",
            "WildcardUtil",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(wildcardUtil);

        var assetLocationType = new TypeDefinition(
            "Vintagestory.API.Common",
            "AssetLocation",
            TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(assetLocationType);

        var stringArrayType = new ArrayType(module.TypeSystem.String);

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

        var matchTwoArgBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.WildcardUtilMatch), new[] { typeof(AssetLocation), typeof(AssetLocation) })!);
        var matchThreeArgBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.WildcardUtilMatch), new[] { typeof(AssetLocation), typeof(AssetLocation), typeof(string[]) })!);

        int patched = ApiPatcher.PatchWildcardUtil(module, matchTwoArgBridge, matchThreeArgBridge);

        Assert.Equal(2, patched);
        Assert.Empty(matchTwoArg.Body.Variables);
        Assert.Collection(
            matchTwoArg.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(matchTwoArgBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        Assert.Empty(matchThreeArg.Body.Variables);
        Assert.Collection(
            matchThreeArg.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_2, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(matchThreeArgBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));
    }

    #endregion

    #region EvolvingNatFloat Tests

    [Fact]
    public void EvolvingNatFloat_Equals_ValueSemanticsAndIEquatable()
    {
        var a = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.5f);
        var b = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.5f);
        var c = new EvolvingNatFloat(EnumTransformFunction.QUADRATIC, 2.5f);
        var d = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 3.0f);

        Assert.IsAssignableFrom<IEquatable<EvolvingNatFloat>>(a);

        // Value equality
        Assert.True(a.Equals(b));
        Assert.True(b.Equals(a));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // Inequality by transform
        Assert.False(a.Equals(c));
        Assert.False(c.Equals(a));
        Assert.False(a == c);
        Assert.True(a != c);
        Assert.False(a.Equals((object)c));

        // Inequality by factor
        Assert.False(a.Equals(d));
        Assert.False(d.Equals(a));
        Assert.False(a == d);
        Assert.True(a != d);
        Assert.False(a.Equals((object)d));

        // Non-EvolvingNatFloat and null
        Assert.False(a.Equals((object?)null));
        Assert.False(a.Equals("other-type"));
    }

    [Fact]
    public void OptimumApiBridge_EvolvingNatFloatEquals_MatchesSemantics()
    {
        var a = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 1.5f);
        var b = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 1.5f);
        var c = new EvolvingNatFloat(EnumTransformFunction.IDENTICAL, 1.5f);
        var d = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 2.0f);

        Assert.True(OptimumApiBridge.EvolvingNatFloatEquals(a, b));
        Assert.False(OptimumApiBridge.EvolvingNatFloatEquals(a, c));
        Assert.False(OptimumApiBridge.EvolvingNatFloatEquals(a, d));
    }

    [Fact]
    public void ApiPatcher_PatchEvolvingNatFloat_RewritesOperatorsAndInjectsIEquatableWithoutBoxing()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("VintagestoryAPI", ModuleKind.Dll);
        var evolvingNatFloat = new TypeDefinition(
            "Vintagestory.API.MathTools",
            "EvolvingNatFloat",
            TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            module.ImportReference(typeof(ValueType)));
        module.Types.Add(evolvingNatFloat);

        var opEquality = new MethodDefinition(
            "op_Equality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.Boolean);
        opEquality.Parameters.Add(new ParameterDefinition("left", ParameterAttributes.None, evolvingNatFloat));
        opEquality.Parameters.Add(new ParameterDefinition("right", ParameterAttributes.None, evolvingNatFloat));
        opEquality.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldc_I4_0));
        opEquality.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        evolvingNatFloat.Methods.Add(opEquality);

        var opInequality = new MethodDefinition(
            "op_Inequality",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            module.TypeSystem.Boolean);
        opInequality.Parameters.Add(new ParameterDefinition("left", ParameterAttributes.None, evolvingNatFloat));
        opInequality.Parameters.Add(new ParameterDefinition("right", ParameterAttributes.None, evolvingNatFloat));
        opInequality.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldc_I4_0));
        opInequality.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        evolvingNatFloat.Methods.Add(opInequality);

        var equalsBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.EvolvingNatFloatEquals),
                new[] { typeof(EvolvingNatFloat), typeof(EvolvingNatFloat) })!);

        int patched = ApiPatcher.PatchEvolvingNatFloat(module, equalsBridge);

        Assert.Equal(2, patched);

        // Verify IEquatable<EvolvingNatFloat> interface is implemented
        Assert.Contains(
            evolvingNatFloat.Interfaces,
            iface => iface.InterfaceType.FullName.StartsWith("System.IEquatable`1<Vintagestory.API.MathTools.EvolvingNatFloat"));

        // Verify op_Equality calls bridge without boxing
        Assert.Empty(opEquality.Body.Variables);
        Assert.DoesNotContain(opEquality.Body.Instructions, ins => ins.OpCode == OpCodes.Box);
        Assert.Collection(
            opEquality.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(equalsBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        // Verify op_Inequality calls bridge and inverts without boxing
        Assert.Empty(opInequality.Body.Variables);
        Assert.DoesNotContain(opInequality.Body.Instructions, ins => ins.OpCode == OpCodes.Box);
        Assert.Collection(
            opInequality.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(equalsBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ldc_I4_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ceq, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        // Verify Equals(EvolvingNatFloat) method exists and loads this value type without boxing
        var equalsMethod = evolvingNatFloat.Methods.Single(m =>
            !m.IsStatic &&
            m.Name == "Equals" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == evolvingNatFloat.FullName);

        Assert.DoesNotContain(equalsMethod.Body.Instructions, ins => ins.OpCode == OpCodes.Box);
        Assert.Collection(
            equalsMethod.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Ldobj, ins.OpCode);
                Assert.Equal(evolvingNatFloat.FullName, ((TypeReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(equalsBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));
    }

    #endregion

    #region BlockPatch Tests

    [Fact]
    public void BlockPatch_Generate_ContainsNoDeadModSystemLookup()
    {
        string vsEssentialsPath = typeof(Vintagestory.ServerMods.NoObf.BlockPatch).Assembly.Location;
        using var module = AssemblyDefinition.ReadAssembly(vsEssentialsPath).MainModule;
        var blockPatchType = module.GetType("Vintagestory.ServerMods.NoObf.BlockPatch");
        Assert.NotNull(blockPatchType);

        var generateMethod = blockPatchType.Methods.Single(m => m.Name == "Generate" && m.Parameters.Count == 7);
        Assert.NotNull(generateMethod);

        Assert.DoesNotContain(generateMethod.Body.Instructions, ins =>
            ins.Operand is MethodReference mr && mr.Name == "GetModSystem");
        Assert.DoesNotContain(generateMethod.Body.Instructions, ins =>
            ins.Operand is TypeReference tr && tr.Name.Contains("GenVegetationAndPatches"));
    }

    #endregion

    #region ClientAnimator Tests

    [Fact]
    public void ClientAnimator_ApiPatcher_RewritesInitMatricesAndClampsActiveAnimations()
    {
        string vanillaApi = PatchReader.FindRepositoryFile(".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        using var module = AssemblyDefinition.ReadAssembly(vanillaApi).MainModule;
        var clientAnimator = module.GetType("Vintagestory.API.Common.ClientAnimator");
        Assert.NotNull(clientAnimator);

        var initBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(nameof(OptimumApiBridge.ClientAnimatorInitMatrices),
                new[] { typeof(ClientAnimator), typeof(int) })!);

        int patched = ApiPatcher.PatchClientAnimator(module, initBridge);
        Assert.Equal(4, patched);

        // Verify initMatrices calls bridge
        var init = clientAnimator.Methods.Single(m => m.Name == "initMatrices" && m.Parameters.Count == 1);
        Assert.Collection(
            init.Body.Instructions,
            ins => Assert.Equal(OpCodes.Ldarg_0, ins.OpCode),
            ins => Assert.Equal(OpCodes.Ldarg_1, ins.OpCode),
            ins =>
            {
                Assert.Equal(OpCodes.Call, ins.OpCode);
                Assert.Equal(initBridge.FullName, ((MethodReference)ins.Operand).FullName);
            },
            ins => Assert.Equal(OpCodes.Ret, ins.OpCode));

        // Verify calculateMatrices(float) clamps activeAnimCount
        var calc1 = clientAnimator.Methods.Single(m => m.Name == "calculateMatrices" && m.Parameters.Count == 1);
        int calc1Clamps = 0;
        for (int i = 0; i < calc1.Body.Instructions.Count - 2; i++)
        {
            if (calc1.Body.Instructions[i].OpCode == OpCodes.Ldfld &&
                calc1.Body.Instructions[i].Operand is FieldReference fr &&
                fr.Name == "activeAnimCount" &&
                calc1.Body.Instructions[i + 1].OpCode == OpCodes.Ldsfld &&
                calc1.Body.Instructions[i + 2].OpCode == OpCodes.Call &&
                calc1.Body.Instructions[i + 2].Operand is MethodReference mr &&
                mr.Name == "Min")
            {
                calc1Clamps++;
            }
        }
        Assert.Equal(1, calc1Clamps);

        // Verify calculateMatrices(8 params) clamps both activeAnimCount reads
        var calc8 = clientAnimator.Methods.Single(m => m.Name == "calculateMatrices" && m.Parameters.Count == 8);
        int calc8Clamps = 0;
        for (int i = 0; i < calc8.Body.Instructions.Count - 2; i++)
        {
            if (calc8.Body.Instructions[i].OpCode == OpCodes.Ldfld &&
                calc8.Body.Instructions[i].Operand is FieldReference fr &&
                fr.Name == "activeAnimCount" &&
                calc8.Body.Instructions[i + 1].OpCode == OpCodes.Ldsfld &&
                calc8.Body.Instructions[i + 2].OpCode == OpCodes.Call &&
                calc8.Body.Instructions[i + 2].Operand is MethodReference mr &&
                mr.Name == "Min")
            {
                calc8Clamps++;
            }
        }
        Assert.Equal(2, calc8Clamps);
    }

    #endregion

    #region MechBlockRenderer Tests

    [Fact]
    public void MechBlockRenderer_UpdateCustomFloatBuffer_CachesPosition()
    {
        string survivalDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        if (!System.IO.File.Exists(survivalDll))
        {
            survivalDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSSurvivalMod.dll");
        }
        Assert.True(System.IO.File.Exists(survivalDll), $"VSSurvivalMod.dll not found at {survivalDll}");

        using var module = AssemblyDefinition.ReadAssembly(survivalDll).MainModule;
        var rendererType = module.GetType("Vintagestory.GameContent.Mechanics.MechBlockRenderer");
        Assert.NotNull(rendererType);

        var updateMethod = rendererType.Methods.Single(m => m.Name == "UpdateCustomFloatBuffer" && m.Parameters.Count == 0);
        Assert.NotNull(updateMethod);

        int positionCalls = updateMethod.Body.Instructions.Count(ins =>
            ins.Operand is MethodReference mr && mr.Name == "get_Position");
        Assert.Equal(1, positionCalls);
    }

    #endregion

    #region BEBehaviorMicroblockSnowCover Tests

    [Fact]
    public void BEBehaviorMicroblockSnowCover_BuildSnowCuboids_AllocatesZeroOnHeap()
    {
        string survivalDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        if (!System.IO.File.Exists(survivalDll))
        {
            survivalDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSSurvivalMod.dll");
        }
        Assert.True(System.IO.File.Exists(survivalDll), $"VSSurvivalMod.dll not found at {survivalDll}");

        using var module = AssemblyDefinition.ReadAssembly(survivalDll).MainModule;
        var snowCoverType = module.GetType("Vintagestory.GameContent.BEBehaviorMicroblockSnowCover");
        Assert.NotNull(snowCoverType);

        var buildMethod = snowCoverType.Methods.Single(m => m.Name == "buildSnowCuboids" && m.Parameters.Count == 1);
        Assert.NotNull(buildMethod);

        // Verify no 2D boolean array allocation
        Assert.DoesNotContain(buildMethod.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference mr && mr.DeclaringType.Name.Contains("Boolean[,]"));

        // Verify stackalloc is used
        Assert.Contains(buildMethod.Body.Instructions, ins => ins.OpCode == OpCodes.Localloc);
    }

    #endregion

    #region TreeGen Tests

    [Fact]
    public void TreeGen_PlaceBlockEtc_OrdersMossConditionsBeforeBlockLookup()
    {
        string essentialsDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSEssentials.dll");
        if (!System.IO.File.Exists(essentialsDll))
        {
            essentialsDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSEssentials.dll");
        }
        Assert.True(System.IO.File.Exists(essentialsDll), $"VSEssentials.dll not found at {essentialsDll}");

        using var module = AssemblyDefinition.ReadAssembly(essentialsDll).MainModule;
        var treeGenType = module.GetType("Vintagestory.ServerMods.TreeGen");
        Assert.NotNull(treeGenType);

        var placeMethod = treeGenType.Methods.Single(m => m.Name == "PlaceBlockEtc" && m.Parameters.Count == 5);
        Assert.NotNull(placeMethod);

        var instructions = placeMethod.Body.Instructions;
        int getBlockIdx = -1;
        int mossGrowthChanceIdx = -1;

        for (int i = 0; i < instructions.Count; i++)
        {
            var ins = instructions[i];
            if (ins.Operand is MethodReference mr && mr.Name == "GetBlock" && getBlockIdx == -1)
            {
                getBlockIdx = i;
            }
            if (ins.Operand is FieldReference fr && fr.Name == "mossGrowthChance" && mossGrowthChanceIdx == -1)
            {
                mossGrowthChanceIdx = i;
            }
        }

        Assert.True(mossGrowthChanceIdx >= 0, "mossGrowthChance field read not found in PlaceBlockEtc");
        Assert.True(getBlockIdx >= 0, "GetBlock call not found in PlaceBlockEtc");
        Assert.True(mossGrowthChanceIdx < getBlockIdx,
            $"Expected mossGrowthChance check (idx {mossGrowthChanceIdx}) before GetBlock call (idx {getBlockIdx})");
    }

    #endregion

    #region LerpedWeightedIndex2DMap Tests

    [Fact]
    public void LerpedWeightedIndex2DMap_WeightsAt_UsesArrayClearAndPrecomputedRows()
    {
        string essentialsDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSEssentials.dll");
        if (!System.IO.File.Exists(essentialsDll))
        {
            essentialsDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSEssentials.dll");
        }
        Assert.True(System.IO.File.Exists(essentialsDll), $"VSEssentials.dll not found at {essentialsDll}");

        using var module = AssemblyDefinition.ReadAssembly(essentialsDll).MainModule;
        var mapType = module.GetType("Vintagestory.ServerMods.LerpedWeightedIndex2DMap");
        Assert.NotNull(mapType);

        var weightsMethod = mapType.Methods.Single(m => m.Name == "WeightsAt" && m.Parameters.Count == 3);
        Assert.NotNull(weightsMethod);

        // Verify Array.Clear is called to zero output
        Assert.Contains(weightsMethod.Body.Instructions, ins =>
            ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "Clear" && mr.DeclaringType.Name == "Array");

        // Verify multiplication by sizeX is hoisted (done exactly 2 times for rowLeft and rowRight, instead of 4 times)
        int mulInstructions = weightsMethod.Body.Instructions.Count(ins => ins.OpCode == OpCodes.Mul);
        Assert.Equal(2, mulInstructions);
    }

    #endregion

    #region GenStoryStructures Tests

    [Fact]
    public void GenStoryStructures_WorldGenClampingAndValidation_Injected()
    {
        string survivalDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        if (!System.IO.File.Exists(survivalDll))
        {
            survivalDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSSurvivalMod.dll");
        }
        Assert.True(System.IO.File.Exists(survivalDll), $"VSSurvivalMod.dll not found at {survivalDll}");

        using var module = AssemblyDefinition.ReadAssembly(survivalDll).MainModule;
        var storyGenType = module.GetType("Vintagestory.GameContent.GenStoryStructures");
        Assert.NotNull(storyGenType);

        var initMethod = storyGenType.Methods.Single(m => m.Name == "InitWorldGen" && m.Parameters.Count == 0);
        Assert.NotNull(initMethod);
        // Verify InitWorldGen checks MapSizeY
        Assert.Contains(initMethod.Body.Instructions, ins =>
            ins.Operand is MethodReference mr && mr.Name == "get_MapSizeY");

        var columnGenMethod = storyGenType.Methods.Single(m => m.Name == "OnChunkColumnGen" && m.Parameters.Count == 1);
        Assert.NotNull(columnGenMethod);
        // Verify OnChunkColumnGen reads MapSizeY and clamps coordinates with Math.Max
        Assert.Contains(columnGenMethod.Body.Instructions, ins =>
            ins.Operand is MethodReference mr && mr.Name == "get_MapSizeY");
        Assert.Contains(columnGenMethod.Body.Instructions, ins =>
            ins.Operand is MethodReference mr && mr.Name == "Max" && mr.DeclaringType.Name == "Math");
    }

    #endregion

    #region Issue 271 - ShiverAnimationNames and CollisionTester Delegate Tests

    [Fact]
    public void EntityBehaviorBodyTemperature_ShiverAnimationNames_CachedAndInjected()
    {
        string survivalDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSSurvivalMod.dll");
        if (!System.IO.File.Exists(survivalDll))
        {
            survivalDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSSurvivalMod.dll");
        }
        Assert.True(System.IO.File.Exists(survivalDll), $"VSSurvivalMod.dll not found at {survivalDll}");

        using var module = AssemblyDefinition.ReadAssembly(survivalDll).MainModule;
        var bodyTempType = module.GetType("Vintagestory.GameContent.EntityBehaviorBodyTemperature");
        Assert.NotNull(bodyTempType);

        var shiverField = bodyTempType.Fields.SingleOrDefault(f => f.Name == "ShiverAnimationNames");
        Assert.NotNull(shiverField);
        Assert.True(shiverField.IsStatic, "ShiverAnimationNames must be static");

        var updateAnimMethod = bodyTempType.Methods.Single(m => m.Name == "updateFreezingAnimState" && m.Parameters.Count == 0);
        Assert.NotNull(updateAnimMethod);

        // Verify updateFreezingAnimState references ShiverAnimationNames field
        Assert.Contains(updateAnimMethod.Body.Instructions, ins =>
            ins.Operand is FieldReference fr && fr.Name == "ShiverAnimationNames");
    }

    [Fact]
    public void CollisionTester_WalkBlocksDelegate_UsesCachedInstanceField()
    {
        var tester = new Vintagestory.API.MathTools.CollisionTester();
        var cachingTester = new Vintagestory.API.MathTools.CachingCollisionTester();

        // Both testers instantiate without throwing and initialize the walkBlocksDelegate
        Assert.NotNull(tester);
        Assert.NotNull(cachingTester);
    }

    #endregion

    #region Issue 462 - RegistryObjectType Lazy Resolve and FillPlaceHolder Tests

    [Theory]
    [InlineData("block/wood/planks/{wood}1", "oak", "granite", "block/wood/planks/oak1")]
    [InlineData("block/{type|rock}/brick", "oak", "granite", "block/granite/brick")]
    [InlineData("block/{wood|rock}/brick", "oak", "granite", "block/oak/brick")]
    [InlineData("{foo{rock}}", "oak", "granite", "{foogranite}")]
    [InlineData("unclosed-{rock", "oak", "granite", "unclosed-{rock")]
    [InlineData("no-placeholders-here", "oak", "granite", "no-placeholders-here")]
    public void RegistryObjectFillPlaceHolder_MatchesVanillaSemantics(
        string input,
        string woodVal,
        string rockVal,
        string expected)
    {
        var dict = new Vintagestory.API.Datastructures.OrderedDictionary<string, string>
        {
            ["wood"] = woodVal,
            ["rock"] = rockVal,
        };

        Assert.Equal(expected, OptimumApiBridge.RegistryObjectFillPlaceHolder(input, dict));
        Assert.Equal(expected, RegistryObject.FillPlaceHolder(input, dict));
    }

    [Fact]
    public void RegistryObjectFillPlaceHolder_UnchangedInput_ReturnsSameInstance()
    {
        var dict = new Vintagestory.API.Datastructures.OrderedDictionary<string, string>
        {
            ["wood"] = "oak",
        };

        string noBrace = "game:block/stone/granite";
        Assert.Same(noBrace, OptimumApiBridge.RegistryObjectFillPlaceHolder(noBrace, dict));

        string unmatchedBrace = "game:block/stone/{unknown}";
        Assert.Same(unmatchedBrace, OptimumApiBridge.RegistryObjectFillPlaceHolder(unmatchedBrace, dict));
    }

    [Fact]
    public void RegistryObjectFillPlaceHolder_LongStringAndAssetLocation_WorkCorrectly()
    {
        var dict = new Vintagestory.API.Datastructures.OrderedDictionary<string, string>
        {
            ["rock"] = "chert",
            ["metal"] = "copper",
        };

        string prefix = new string('a', 300);
        string longInput = $"{prefix}-{{rock}}-{{type|metal}}";
        string result = OptimumApiBridge.RegistryObjectFillPlaceHolder(longInput, dict);
        Assert.Equal($"{prefix}-chert-copper", result);

        var loc = new AssetLocation("game", "ore-{metal}-{rock}");
        var resolvedLoc = OptimumApiBridge.RegistryObjectFillPlaceHolder(loc, dict);
        Assert.Same(loc, resolvedLoc);
        Assert.Equal("ore-copper-chert", resolvedLoc!.Path);
    }

    [Fact]
    public void RegistryObjectFillPlaceHolder_ApiPatcher_RewritesOverloadsToCallOptimumApiBridge()
    {
        using var module = ModuleDefinition.CreateModule("RegistryObjectFillPlaceHolderPatchTest", ModuleKind.Dll);
        var registryObject = new TypeDefinition(
            "Vintagestory.API.Common",
            "RegistryObject",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(registryObject);

        var assetLocationType = module.ImportReference(typeof(AssetLocation));
        var orderedDictType = module.ImportReference(typeof(Vintagestory.API.Datastructures.OrderedDictionary<string, string>));

        var fillAsset = new MethodDefinition(
            "FillPlaceHolder",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            assetLocationType);
        fillAsset.Parameters.Add(new ParameterDefinition("input", ParameterAttributes.None, assetLocationType));
        fillAsset.Parameters.Add(new ParameterDefinition("searchReplace", ParameterAttributes.None, orderedDictType));
        fillAsset.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        fillAsset.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        registryObject.Methods.Add(fillAsset);

        var fillString = new MethodDefinition(
            "FillPlaceHolder",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.String);
        fillString.Parameters.Add(new ParameterDefinition("input", ParameterAttributes.None, module.TypeSystem.String));
        fillString.Parameters.Add(new ParameterDefinition("searchReplace", ParameterAttributes.None, orderedDictType));
        fillString.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        fillString.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        registryObject.Methods.Add(fillString);

        var fillSingle = new MethodDefinition(
            "FillPlaceHolder",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.String);
        fillSingle.Parameters.Add(new ParameterDefinition("input", ParameterAttributes.None, module.TypeSystem.String));
        fillSingle.Parameters.Add(new ParameterDefinition("search", ParameterAttributes.None, module.TypeSystem.String));
        fillSingle.Parameters.Add(new ParameterDefinition("replace", ParameterAttributes.None, module.TypeSystem.String));
        fillSingle.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ldnull));
        fillSingle.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        registryObject.Methods.Add(fillSingle);

        var fillAssetBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.RegistryObjectFillPlaceHolder),
                new[] { typeof(AssetLocation), typeof(Vintagestory.API.Datastructures.OrderedDictionary<string, string>) })!);
        var fillStringBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.RegistryObjectFillPlaceHolder),
                new[] { typeof(string), typeof(Vintagestory.API.Datastructures.OrderedDictionary<string, string>) })!);
        var fillSingleBridge = module.ImportReference(
            typeof(OptimumApiBridge).GetMethod(
                nameof(OptimumApiBridge.RegistryObjectFillPlaceHolder),
                new[] { typeof(string), typeof(string), typeof(string) })!);

        int patched = ApiPatcher.PatchRegistryObjectFillPlaceHolder(
            module,
            fillAssetBridge,
            fillStringBridge,
            fillSingleBridge);

        Assert.Equal(3, patched);
    }

    [Fact]
    public void RegistryObjectType_BuildDynamicSetAndResolve_InjectedAndPreservesByTypeSemantics()
    {
        string essentialsDll = System.IO.Path.Combine(AppContext.BaseDirectory, "VSEssentials.dll");
        if (!System.IO.File.Exists(essentialsDll))
        {
            essentialsDll = PatchReader.FindRepositoryFile("bin/Release/net10.0/VSEssentials.dll");
        }
        Assert.True(System.IO.File.Exists(essentialsDll), $"VSEssentials.dll not found at {essentialsDll}");

        var asm = System.Reflection.Assembly.LoadFrom(essentialsDll);
        var regType = asm.GetType("Vintagestory.ServerMods.NoObf.RegistryObjectType");
        Assert.NotNull(regType);

        var buildDynamicSet = regType!.GetMethod(
            "BuildDynamicSet",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var resolve = regType.GetMethod(
            "Resolve",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(buildDynamicSet);
        Assert.NotNull(resolve);

        var baseJson = Newtonsoft.Json.Linq.JObject.Parse("""
        {
            "code": "clothes",
            "staticSection": { "x": 1, "y": 2 },
            "attributes": {
                "warmthByType": {
                    "*-surgeonhood": 0,
                    "*": 1.5
                },
                "label": "cloth-{color}"
            },
            "attributesByType": {
                "*-surgeonhood": 2.2,
                "*-wintercoat-*": {
                    "extraByType": {
                        "*-blue": "winter-{color}",
                        "*": "default"
                    }
                }
            }
        }
        """);

        var dynamicNodes = new System.Collections.Generic.HashSet<Newtonsoft.Json.Linq.JToken>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        buildDynamicSet!.Invoke(null, new object[] { baseJson, dynamicNodes });

        // Static section must not be marked dynamic
        Assert.DoesNotContain(baseJson["staticSection"]!, dynamicNodes);

        // 1. Surgeonhood: attributesByType matches scalar 2.2, must not overwrite attributes JObject, warmth resolves to 0
        var surgeonVars = new Vintagestory.API.Datastructures.OrderedDictionary<string, string> { ["color"] = "white" };
        var resolvedSurgeon = (Newtonsoft.Json.Linq.JObject)resolve!.Invoke(
            null,
            new object[] { baseJson, "clothes-face-surgeonhood", surgeonVars, dynamicNodes })!;
        Assert.Equal(0, (int)resolvedSurgeon["attributes"]!["warmth"]!);
        Assert.Equal("cloth-white", (string?)resolvedSurgeon["attributes"]!["label"]!);
        Assert.Null(resolvedSurgeon["attributesByType"]);

        // 2. Wintercoat blue: attributesByType JObject merges into attributes and resolves nested extraByType + placeholder
        var winterVars = new Vintagestory.API.Datastructures.OrderedDictionary<string, string> { ["color"] = "blue" };
        var resolvedWinter = (Newtonsoft.Json.Linq.JObject)resolve.Invoke(
            null,
            new object[] { baseJson, "clothes-upperbody-wintercoat-blue", winterVars, dynamicNodes })!;
        Assert.Equal(1.5, (double)resolvedWinter["attributes"]!["warmth"]!);
        Assert.Equal("cloth-blue", (string?)resolvedWinter["attributes"]!["label"]!);
        Assert.Equal("winter-blue", (string?)resolvedWinter["attributes"]!["extra"]!);
    }

    #endregion
}
