using System.Linq;
using Cairo;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Config;
using Xunit;

[Collection("GUI metrics")]
public sealed class ApiPatcherFontMeasurementTests
{
    static ApiPatcherFontMeasurementTests() { _ = CairoAPI.Version; CairoBundledFonts.Register(); }

    [Fact]
    public void FontMetricsRewriteVanillaBodiesToCandidateSkiaHelpersAndKeepInstrumentation()
    {
        using var contracts = AssemblyDefinition.ReadAssembly(typeof(OptimumGuiMetrics).Assembly.Location);
        using var target = ModuleDefinition.CreateModule("VintagestoryAPI-Font-Fixture", ModuleKind.Dll);
        using var cairo = AssemblyDefinition.ReadAssembly(typeof(OptimumSkiaTextMetrics).Assembly.Location);
        var config = new TypeDefinition("Vintagestory.API.Client", "FontConfig", TypeAttributes.Public, target.TypeSystem.Object);
        target.Types.Add(config);
        config.Fields.Add(new FieldDefinition("Fontname", FieldAttributes.Public, target.TypeSystem.String));
        config.Fields.Add(new FieldDefinition("FontWeight", FieldAttributes.Public, target.ImportReference(typeof(FontWeight))));
        config.Fields.Add(new FieldDefinition("UnscaledFontsize", FieldAttributes.Public, target.TypeSystem.Double));
        var font = new TypeDefinition("Vintagestory.API.Client", "CairoFont", TypeAttributes.Public, config);
        font.Fields.Add(new FieldDefinition("Slant", FieldAttributes.Public, target.ImportReference(typeof(FontSlant))));
        var measuringSurface = new FieldDefinition("surface", FieldAttributes.Private | FieldAttributes.Static, target.ImportReference(typeof(ImageSurface)));
        var measuringContext = new FieldDefinition("FontMeasuringContext", FieldAttributes.Public | FieldAttributes.Static, target.ImportReference(typeof(Context)));
        font.Fields.Add(measuringSurface);
        font.Fields.Add(measuringContext);
        var guiElement = new TypeDefinition("Vintagestory.API.Client", "GuiElement", TypeAttributes.Public, target.TypeSystem.Object);
        target.Types.Add(guiElement);
        var scaled = new MethodDefinition("scaled", MethodAttributes.Public | MethodAttributes.Static, target.TypeSystem.Double);
        scaled.Parameters.Add(new ParameterDefinition(target.TypeSystem.Double));
        guiElement.Methods.Add(scaled);
        scaled.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        scaled.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        target.Types.Add(font);
        var configConstructor = AddConstructor(config, target.ImportReference(typeof(object).GetConstructor(System.Type.EmptyTypes)!));
        AddConstructor(font, configConstructor);
        AddNativeMeasuringContextInitializer(measuringSurface, measuringContext);
        var options = new FieldDefinition("CairoFontOptions", FieldAttributes.Private, target.ImportReference(typeof(FontOptions)));
        font.Fields.Add(options);
        var dispose = new MethodDefinition("Dispose", MethodAttributes.Public, target.TypeSystem.Void);
        font.Methods.Add(dispose);
        dispose.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        dispose.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, options));
        dispose.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, target.ImportReference(typeof(FontOptions).GetMethod("Dispose")!)));
        dispose.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        AddMeasurement("GetFontExtents", typeof(Context).GetProperty(nameof(Context.FontExtents))!.GetMethod!);
        AddMeasurement("GetTextExtents", typeof(Context).GetMethod(nameof(Context.TextExtents), new[] { typeof(string) })!);

        var measurements = font.Methods.Where(method => method.Name.StartsWith("Get")).ToArray();
        string[] signatures = font.Methods.Select(method => method.FullName).ToArray();
        using var missingHelper = ModuleDefinition.CreateModule("CairoWithoutSkiaMetrics", ModuleKind.Dll);
        Assert.Throws<System.InvalidOperationException>(() =>
            ApiPatcher.PatchFontMeasurementMetrics(target, contracts.MainModule, missingHelper));
        Assert.All(measurements, method => Assert.Contains(method.Body.Instructions,
            instruction => instruction.Operand is MethodReference called && called.DeclaringType.FullName == "Cairo.Context"));
        Assert.Equal(2, ApiPatcher.PatchFontMeasurementMetrics(target, contracts.MainModule, cairo.MainModule));
        Assert.Equal(signatures, font.Methods.Select(method => method.FullName).ToArray());
        var initializer = font.Methods.Single(method => method.Name == ".cctor");
        Assert.Equal(new[] { OpCodes.Ret }, initializer.Body.Instructions.Select(instruction => instruction.OpCode));

        foreach (MethodDefinition method in measurements)
        {
            MethodReference[] calls = method.Body.Instructions
                .Where(instruction => instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                .Select(instruction => (MethodReference)instruction.Operand).ToArray();
            Assert.DoesNotContain(calls, called => called.DeclaringType.FullName == "Cairo.Context");
            Assert.Contains(calls, called => called.DeclaringType.FullName == "Cairo.OptimumSkiaTextMetrics" &&
                called.Name == method.Name && called.DeclaringType.Scope.Name == "cairo-sharp");
            Assert.Contains(calls, called => called.DeclaringType.FullName == guiElement.FullName && called.Name == "scaled");
            Assert.Equal(new[] { "Fontname", "FontWeight", "Slant", "UnscaledFontsize" },
                method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldfld)
                    .Select(instruction => ((FieldReference)instruction.Operand).Name));
            Assert.Contains(calls, called => called.DeclaringType.FullName == typeof(OptimumGuiMetrics).FullName &&
                called.Name == (method.Name == "GetTextExtents" ? "RecordTextExtentMeasurement" : "RecordFontExtentMeasurement"));
        }
        Assert.Empty(IlStackVerifier.VerifyModule(target));
        Assert.Throws<System.InvalidOperationException>(() =>
            ApiPatcher.PatchFontMeasurementMetrics(target, contracts.MainModule, cairo.MainModule));
        using var stream = new System.IO.MemoryStream();
        target.Write(stream);
        stream.Position = 0;
        using var written = ModuleDefinition.ReadModule(stream);
        Assert.Empty(IlStackVerifier.VerifyModule(written));

        // Execute the serialized replacement, not the recompiled API project's
        // CairoFont. This catches argument order, accessibility and binding errors.
        var runtimeAssembly = System.Reflection.Assembly.Load(stream.ToArray());
        var runtimeFont = runtimeAssembly.GetType(font.FullName)!;
        Assert.Null(runtimeFont.GetField("FontMeasuringContext")!.GetValue(null));
        object instance = System.Activator.CreateInstance(runtimeFont)!;
        runtimeFont.GetField("Fontname")!.SetValue(instance, "sans-serif");
        runtimeFont.GetField("FontWeight")!.SetValue(instance, FontWeight.Bold);
        runtimeFont.GetField("Slant")!.SetValue(instance, FontSlant.Italic);
        runtimeFont.GetField("UnscaledFontsize")!.SetValue(instance, 19.5);
        const string text = "Inventory \U0001f600";
        var actualText = (TextExtents)runtimeFont.GetMethod("GetTextExtents")!.Invoke(instance, new object[] { text })!;
        var expectedText = OptimumSkiaTextMetrics.GetTextExtents("sans-serif", FontWeight.Bold, FontSlant.Italic, 19.5, text);
        Assert.Equal(expectedText.XAdvance, actualText.XAdvance);
        Assert.Equal(expectedText.Width, actualText.Width);
        var actualFont = (FontExtents)runtimeFont.GetMethod("GetFontExtents")!.Invoke(instance, null)!;
        var expectedFont = OptimumSkiaTextMetrics.GetFontExtents("sans-serif", FontWeight.Bold, FontSlant.Italic, 19.5);
        Assert.Equal(expectedFont.Ascent, actualFont.Ascent);
        Assert.Equal(expectedFont.Height, actualFont.Height);
        runtimeFont.GetMethod("Dispose")!.Invoke(instance, null);
        // Exercise the original non-null disposal path too, with the platform
        // native library resolver initialized as it is in the game.
        _ = CairoAPI.Version;
        using var populatedOptions = new FontOptions();
        runtimeFont.GetField("CairoFontOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(instance, populatedOptions);
        runtimeFont.GetMethod("Dispose")!.Invoke(instance, null);

        void AddNativeMeasuringContextInitializer(FieldDefinition surfaceField, FieldDefinition contextField)
        {
            var initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, target.TypeSystem.Void);
            font.Methods.Add(initializer);
            ILProcessor processor = initializer.Body.GetILProcessor();
            processor.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            processor.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            processor.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            processor.Append(Instruction.Create(OpCodes.Newobj, target.ImportReference(
                typeof(ImageSurface).GetConstructor(new[] { typeof(Format), typeof(int), typeof(int) })!)));
            processor.Append(Instruction.Create(OpCodes.Stsfld, surfaceField));
            processor.Append(Instruction.Create(OpCodes.Ldsfld, surfaceField));
            processor.Append(Instruction.Create(OpCodes.Newobj, target.ImportReference(
                typeof(Context).GetConstructor(new[] { typeof(Surface) })!)));
            processor.Append(Instruction.Create(OpCodes.Stsfld, contextField));
            processor.Append(Instruction.Create(OpCodes.Ret));
        }

        MethodDefinition AddConstructor(TypeDefinition type, MethodReference baseConstructor)
        {
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                target.TypeSystem.Void);
            type.Methods.Add(constructor);
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseConstructor));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return constructor;
        }

        void AddMeasurement(string name, System.Reflection.MethodInfo cairoMethod)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public, target.ImportReference(cairoMethod.ReturnType));
            if (cairoMethod.GetParameters().Length != 0)
                method.Parameters.Add(new ParameterDefinition("text", ParameterAttributes.None, target.TypeSystem.String));
            font.Methods.Add(method);
            ILProcessor processor = method.Body.GetILProcessor();
            // A null context is enough for an IL fixture; no native call runs.
            processor.Append(Instruction.Create(OpCodes.Ldnull));
            if (method.Parameters.Count != 0) processor.Append(Instruction.Create(OpCodes.Ldarg_1));
            processor.Append(Instruction.Create(OpCodes.Callvirt, target.ImportReference(cairoMethod)));
            processor.Append(Instruction.Create(OpCodes.Ret));
        }
    }
}
