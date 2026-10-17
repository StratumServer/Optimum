using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Cairo;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

[Collection("Cairo materialization metrics")]
public sealed class ApiPatcherTextLayoutTests
{
    static ApiPatcherTextLayoutTests()
    {
        _ = CairoAPI.Version;
        CairoRecorderTextTests.BundledFonts.Register();
    }

    [Fact]
    public void FontLayoutPatchRewritesSerializedApiAndPreservesContextOverload()
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(typeof(TextDrawUtil).Assembly.Location)!);
        using var target = AssemblyDefinition.ReadAssembly(typeof(TextDrawUtil).Assembly.Location,
            new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver });

        var util = target.MainModule.GetType("Vintagestory.API.Client.TextDrawUtil")!;
        var contextLineize = FindLineize(util, "Cairo.Context");
        string[] contextBody = contextLineize.Body.Instructions
            .Select(instruction => $"{instruction.OpCode.Code}:{instruction.Operand}").ToArray();

        Assert.Equal(2, ApiPatcher.PatchTextDrawUtilFontOverloads(target.MainModule));

        var fontLineize = FindLineize(util, "Vintagestory.API.Client.CairoFont");
        var quantity = util.Methods.Single(method => method.Name == "GetQuantityTextLines" &&
            method.Parameters.Count == 5 && method.Parameters[0].ParameterType.FullName == "Vintagestory.API.Client.CairoFont");
        var helper = util.Methods.Single(method => method.Name == "LineizeWithSkiaMetrics");

        Assert.Equal(contextBody, contextLineize.Body.Instructions
            .Select(instruction => $"{instruction.OpCode.Code}:{instruction.Operand}").ToArray());
        Assert.DoesNotContain(fontLineize.Body.Instructions, IsNativeContextConstruction);
        Assert.DoesNotContain(quantity.Body.Instructions, IsNativeContextConstruction);
        Assert.DoesNotContain(helper.Body.Instructions, instruction =>
            instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Cairo.Context");
        Assert.Contains(helper.Body.Instructions, instruction =>
            instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Vintagestory.API.Client.CairoFont" &&
            called.Name == "GetTextExtents");
        Assert.Contains(helper.Body.Instructions, instruction =>
            instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Vintagestory.API.Client.CairoFont" &&
            called.Name == "GetFontExtents");
        Assert.Empty(IlStackVerifier.VerifyModule(target.MainModule));
        Assert.Empty(SelfConsistencyVerifier.VerifySelfReferences(target.MainModule));

        using var stream = new System.IO.MemoryStream();
        target.Write(stream);
        stream.Position = 0;
        var loadContext = new AssemblyLoadContext("PatchedTextLayoutFixture", isCollectible: true);
        loadContext.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
        try
        {
            Assembly api = loadContext.LoadFromStream(stream);
            ComparePatchedAndSourceLayout(api, "Inventory items and café\nSecond line", EnumLinebreakBehavior.AfterWord, 44, 0, 0, false);
            ComparePatchedAndSourceLayout(api, "Inventory items and café\nSecond line", EnumLinebreakBehavior.AfterCharacter, 32, 3, 1.5, true);
            ComparePatchedAndSourceLayout(api, "A\r\nB\tC", EnumLinebreakBehavior.None, 80, 0, 0, false);
            ComparePatchedAndSourceLayout(api, string.Empty, EnumLinebreakBehavior.AfterWord, 40, 0, 0, false);
            ComparePatchedAndSourceLayout(api, null, EnumLinebreakBehavior.AfterWord, 40, 0, 0, false);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static MethodDefinition FindLineize(TypeDefinition util, string firstParameter) => util.Methods.Single(method =>
        method.Name == "Lineize" && method.Parameters.Count == (firstParameter == "Cairo.Context" ? 8 : 7) &&
        method.Parameters[0].ParameterType.FullName == firstParameter &&
        method.Parameters[1].ParameterType.MetadataType == MetadataType.String &&
        method.Parameters[3].ParameterType.FullName == "Vintagestory.API.Client.TextFlowPath[]");

    private static bool IsNativeContextConstruction(Instruction instruction) =>
        instruction.OpCode == OpCodes.Newobj && instruction.Operand is MethodReference constructor &&
        (constructor.DeclaringType.FullName == "Cairo.Context" || constructor.DeclaringType.FullName == "Cairo.ImageSurface");

    private static void ComparePatchedAndSourceLayout(Assembly patchedApi, string? text, EnumLinebreakBehavior linebreak,
        double width, double offsetX, double startY, bool keepLinebreak)
    {
        Type patchedFontType = patchedApi.GetType("Vintagestory.API.Client.CairoFont")!;
        Type patchedUtilType = patchedApi.GetType("Vintagestory.API.Client.TextDrawUtil")!;
        Type patchedFlowPathType = patchedApi.GetType("Vintagestory.API.Client.TextFlowPath")!;
        Type patchedBehaviorType = patchedApi.GetType("Vintagestory.API.Client.EnumLinebreakBehavior")!;
        FieldInfo patchedMeasuringContext = patchedFontType.GetField("FontMeasuringContext", BindingFlags.Public | BindingFlags.Static)!;
        patchedMeasuringContext.SetValue(null, null);
        object patchedFont = Activator.CreateInstance(patchedFontType, 16d, "sans-serif")!;
        patchedFontType.GetField("LineHeightMultiplier")!.SetValue(patchedFont, 1.25d);
        object patchedUtil = Activator.CreateInstance(patchedUtilType)!;
        Array patchedPaths = CreateFlowPaths(patchedFlowPathType, width);
        object patchedBreak = Enum.ToObject(patchedBehaviorType, (int)linebreak);
        MethodInfo patchedLineize = FindReflectionMethod(patchedUtilType, "Lineize", 7, patchedFontType);
        object?[] args = { patchedFont, text, patchedBreak, patchedPaths, offsetX, startY, keepLinebreak };
        Array? actual = (Array?)patchedLineize.Invoke(patchedUtil, args);

        Context previous = CairoFont.FontMeasuringContext;
        CairoFont.FontMeasuringContext = null;
        try
        {
            using var sourceFont = new CairoFont(16, "sans-serif").WithLineHeightMultiplier(1.25);
            var sourceUtil = new TextDrawUtil();
            TextLine[] expected = sourceUtil.Lineize(sourceFont, text, linebreak, new[] { new TextFlowPath(width) }, offsetX, startY, keepLinebreak);
            Assert.Equal(expected.Length, actual!.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                object actualLine = actual.GetValue(i)!;
                object actualBounds = actualLine.GetType().GetField("Bounds")!.GetValue(actualLine)!;
                var expectedBounds = expected[i].Bounds;
                Assert.Equal(expected[i].Text, actualLine.GetType().GetField("Text")!.GetValue(actualLine));
                Assert.Equal(expected[i].LeftSpace, (double)actualLine.GetType().GetField("LeftSpace")!.GetValue(actualLine)!, 6);
                Assert.Equal(expected[i].RightSpace, (double)actualLine.GetType().GetField("RightSpace")!.GetValue(actualLine)!, 6);
                Assert.Equal(expected[i].NextOffsetX, (double)actualLine.GetType().GetField("NextOffsetX")!.GetValue(actualLine)!, 6);
                foreach (string field in new[] { "X", "Y", "Width", "Height", "Ascent" })
                    Assert.Equal((double)typeof(LineRectangled).GetField(field)!.GetValue(expectedBounds)!,
                        (double)actualBounds.GetType().GetField(field)!.GetValue(actualBounds)!, 6);
            }

            object patchedCount = FindReflectionMethod(patchedUtilType, "GetQuantityTextLines", 5, patchedFontType)
                .Invoke(patchedUtil, new object?[] { patchedFont, text, patchedBreak, patchedPaths, startY })!;
            Assert.Equal(expected.Length, (int)patchedCount);
        }
        finally
        {
            CairoFont.FontMeasuringContext = previous;
        }
    }

    private static Array CreateFlowPaths(Type pathType, double width)
    {
        Array paths = Array.CreateInstance(pathType, 1);
        paths.SetValue(Activator.CreateInstance(pathType, width), 0);
        return paths;
    }

    private static MethodInfo FindReflectionMethod(Type utilType, string name, int parameterCount, Type fontType) => utilType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Single(method => method.Name == name && method.GetParameters().Length == parameterCount &&
            method.GetParameters()[0].ParameterType == fontType);
}
