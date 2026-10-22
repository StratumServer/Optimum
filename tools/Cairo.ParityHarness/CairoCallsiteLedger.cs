using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace Cairo.ParityHarness;

/// <summary>
/// Static reachability/callsite ledger for the Cairo wrapper (issue #130).
///
/// Scans the managed assemblies of an exact runtime stage for every first-party
/// callsite that reaches the wrapper (<c>cairo-sharp</c>), classifies each by origin,
/// computes the transitive closure first-party callsite -> wrapper member -> P/Invoke
/// import, and checks that every member exercised by a runtime trace
/// (<c>cairo-api-usage.json</c>) is statically reachable. Output is deterministic:
/// no timestamps, absolute build paths or enumeration-order dependence.
/// </summary>
internal static class CairoCallsiteLedger
{
    private const string WrapperAssemblyName = "cairo-sharp";
    private const string WrapperRelativePath = "Lib/cairo-sharp.dll";
    private const string CairoImportLibrary = "libcairo-2";
    private const int ExpectedCairoImportCount = 201;
    private static readonly StringComparer Ord = StringComparer.Ordinal;

    private static readonly string[] NonGuiTypeNames =
    [
        "TextTextureUtil", "IconUtil", "BitmapExternal", "SurfaceDrawImage" /* declared in BitmapExternal.cs */, "BlockEntitySignPostRenderer", "WgenCommands"
    ];

    private static readonly string[] ComposeEntryPointNames = ["ComposeElements", "ComposeTextElements"];

    private static readonly string[] GuiBaseTypeNames =
    [
        "GuiElement", "GuiDialog", "HudElement", "GuiComposer", "GuiComposerHelpers", "GuiElementTextBase"
    ];

    private static readonly Dictionary<string, string> SourceDirectoryToAssembly = new(Ord)
    {
        ["VintagestoryApi"] = "VintagestoryAPI",
        ["VintagestoryLib"] = "VintagestoryLib",
        ["VSEssentials"] = "VSEssentials",
        ["VSSurvivalMod"] = "VSSurvivalMod",
        ["VSCreativeMod"] = "VSCreativeMod",
        ["Vintagestory"] = "Vintagestory",
        ["Cairo"] = WrapperAssemblyName
    };

    private static readonly Regex DeclarationRegex = new(
        @"^[ \t]*(?:namespace[ \t]+(?<ns>[A-Za-z_][\w.]*)|(?:\[[^\]\r\n]*\][ \t]*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|unsafe|readonly|ref|new|file)[ \t]+)*(?:class|struct|interface|enum|record(?:[ \t]+(?:struct|class))?)[ \t]+(?<type>[A-Za-z_]\w*))",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex PatcherTypeLiteralRegex = new(
        @"""(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)(?:::\w+)?""", RegexOptions.CultureInvariant);

    private sealed class Options
    {
        public string Stage = "";
        public string Output = "";
        public List<string> Traces = [];
        public bool AllTraces;
        public string? Repo;
        public string? Vanilla;
    }

    private sealed class StageAssembly
    {
        public required string RelativePath { get; init; }
        public required string Group { get; init; }
        public required string FullPath { get; init; }
        public required string Sha256 { get; init; }
        public required AssemblyDefinition Definition { get; init; }
        public string Name => Definition.Name.Name;
        public string Role { get; set; } = "caller";
        public bool ReferencesWrapper { get; set; }
    }

    private sealed class LedgerRow
    {
        public required StageAssembly Assembly { get; init; }
        public required TypeDefinition Type { get; init; }
        public required MethodDefinition Method { get; init; }
        public int Offset { get; init; }
        public required string Kind { get; init; }
        public required string Opcode { get; init; }
        public required string Member { get; init; }
        public required string MemberKind { get; init; }
        public required string ReferenceName { get; init; }
        public bool Resolved { get; init; }
        public MethodDefinition? TargetMethod { get; init; }
        public FieldDefinition? TargetField { get; init; }
        public string Origin { get; set; } = "";
        public List<string> Ownership { get; set; } = [];
        public string Category { get; set; } = "";
        public string VanillaComparison { get; set; } = "";
        public bool IsInstruction => Offset >= 0;
        public string TypeName => Type.FullName;
        public string MethodName => Method.FullName;
    }

    private sealed class Closure
    {
        public Dictionary<MethodDefinition, MethodDefinition?> Parent { get; } = new();
        public Dictionary<MethodDefinition, string> Via { get; } = new();
        public List<MethodDefinition> Order { get; } = [];
    }

    public static int Run(string[] args)
    {
        Options options = ParseOptions(args);
        string stage = System.IO.Path.GetFullPath(options.Stage);
        if (!Directory.Exists(stage)) throw new DirectoryNotFoundException($"Stage directory does not exist: {stage}");
        string repo = ResolveRepoRoot(options.Repo);
        string vanillaDirectory = System.IO.Path.GetFullPath(options.Vanilla ?? System.IO.Path.Combine(repo, ".vanilla", "linux-x64", "vintagestory"));

        List<StageAssembly> assemblies = LoadStageAssemblies(stage, out List<object> excluded, out List<string> exclusionRules);
        StageAssembly wrapperEntry = assemblies.SingleOrDefault(a => a.RelativePath == WrapperRelativePath)
            ?? throw new InvalidOperationException($"Stage does not contain {WrapperRelativePath}.");
        if (!string.Equals(wrapperEntry.Name, WrapperAssemblyName, StringComparison.Ordinal))
            throw new InvalidOperationException($"{WrapperRelativePath} has identity {wrapperEntry.Name}, expected {WrapperAssemblyName}.");
        wrapperEntry.Role = "wrapper";
        foreach (StageAssembly a in assemblies.Where(a => a != wrapperEntry && string.Equals(a.Name, WrapperAssemblyName, StringComparison.OrdinalIgnoreCase)))
            a.Role = "wrapper-copy-not-scanned";
        foreach (StageAssembly a in assemblies.Where(a => a.Role == "caller" && string.Equals(a.Name, "CairoSharp", StringComparison.OrdinalIgnoreCase)))
            a.Role = "third-party-cairosharp-not-scanned";
        foreach (StageAssembly a in assemblies)
            a.ReferencesWrapper = a.Definition.MainModule.AssemblyReferences.Any(r => string.Equals(r.Name, WrapperAssemblyName, StringComparison.OrdinalIgnoreCase));

        WrapperModel wrapper = new(wrapperEntry.Definition);
        List<StageAssembly> callers = assemblies.Where(a => a.Role == "caller").ToList();
        Dictionary<string, TypeDefinition> typeIndex = BuildTypeIndex(callers);

        // --- callsite scan ---
        List<LedgerRow> rows = [];
        Dictionary<string, Dictionary<string, List<string>>> stageCairoCalls = new(Ord);
        List<object> reflection = [];
        int typeOperandCount = 0, unreadableBodies = 0;
        foreach (StageAssembly assembly in callers.Where(a => a.ReferencesWrapper))
            ScanAssembly(assembly, wrapper, rows, stageCairoCalls, ref typeOperandCount, ref unreadableBodies);
        foreach (StageAssembly assembly in callers.Where(a => a.Group != "stage-lib"))
            ScanReflection(assembly, reflection);

        // --- origin classification ---
        Ownership ownership = Ownership.Load(repo);
        Dictionary<string, VanillaBaseline> vanilla = LoadVanillaBaselines(vanillaDirectory, callers);
        Classify(rows, ownership, vanilla, stageCairoCalls, typeIndex);
        ScanHarmonyTargets(callers, rows, typeIndex, reflection);

        rows.Sort(CompareRows);
        // Primary view: assemblies the game loads. Donor assemblies are intermediate build products whose bodies are
        // transplanted into the loaded assemblies, so they are listed but kept out of the denominators and seeds.
        List<LedgerRow> loadedRows = rows.Where(r => r.Assembly.Group != "runtime-donor").ToList();
        List<LedgerRow> donorRows = rows.Where(r => r.Assembly.Group == "runtime-donor").ToList();
        List<LedgerRow> instructionRows = loadedRows.Where(r => r.IsInstruction).ToList();

        // --- closure ---
        List<MethodDefinition> seeds = SelectSeeds(loadedRows, wrapper);
        Closure strict = ComputeClosure(wrapper, seeds, implicitLifecycle: false);
        Closure conservative = ComputeClosure(wrapper, seeds, implicitLifecycle: true);
        Closure withDonorCopies = ComputeClosure(wrapper, SelectSeeds(rows, wrapper), implicitLifecycle: true);
        HashSet<MethodDefinition> directlyCalled = loadedRows.Where(r => r.TargetMethod != null && r.IsInstruction).Select(r => r.TargetMethod!).ToHashSet();

        var importRows = wrapper.PInvokes
            .OrderBy(m => m.PInvokeInfo?.Module.Name, Ord).ThenBy(m => m.PInvokeInfo?.EntryPoint, Ord).ThenBy(m => m.FullName, Ord)
            .Select(m => new
            {
                library = m.PInvokeInfo?.Module.Name ?? "",
                entryPoint = m.PInvokeInfo?.EntryPoint ?? m.Name,
                declaringType = m.DeclaringType.FullName,
                method = m.FullName,
                reachableStrict = strict.Parent.ContainsKey(m),
                reachable = conservative.Parent.ContainsKey(m),
                witnessPath = conservative.Parent.ContainsKey(m) ? WitnessPath(conservative, m) : new List<string>()
            }).ToList();
        int cairoTotal = importRows.Count(i => i.library == CairoImportLibrary);
        int cairoReachable = importRows.Count(i => i.library == CairoImportLibrary && i.reachable);
        int cairoReachableStrict = importRows.Count(i => i.library == CairoImportLibrary && i.reachableStrict);
        string headline = $"{cairoReachable}/{cairoTotal} imports libcairo-2 alcançáveis a partir de código first-party fora do próprio wrapper";

        // --- trace validation ---
        List<string> traceFiles = SelectTraces(options, stage);
        var traceReports = traceFiles.Select(t => ValidateTrace(t, stage, wrapper, strict, conservative, directlyCalled)).ToList();
        int traceFailures = traceReports.Sum(r => r.Failures);

        // --- output ---
        var libraryRows = importRows.GroupBy(i => i.library, Ord).OrderBy(g => g.Key, Ord)
            .Select(g => new { library = g.Key, total = g.Count(), reachable = g.Count(i => i.reachable), reachableStrict = g.Count(i => i.reachableStrict) }).ToList();
        object CountBy(IEnumerable<LedgerRow> source, Func<LedgerRow, string> key) => source.GroupBy(key, Ord).OrderBy(g => g.Key, Ord)
            .Select(g => new
            {
                key = g.Key,
                callsites = g.Select(CallsiteKey).Distinct(Ord).Count(),
                callerMethods = g.Select(r => r.Assembly.RelativePath + "|" + r.MethodName).Distinct(Ord).Count(),
                distinctMembers = g.Select(r => r.Member).Distinct(Ord).Count()
            }).ToList();

        var summary = new
        {
            headline,
            libcairo2ImportsReachable = cairoReachable,
            libcairo2ImportsReachableExplicitOnly = cairoReachableStrict,
            libcairo2ImportsReachableIfDonorAssemblyCopiesCounted = wrapper.PInvokes.Count(m => m.PInvokeInfo?.Module.Name == CairoImportLibrary && withDonorCopies.Parent.ContainsKey(m)),
            libcairo2ImportsTotal = cairoTotal,
            libcairo2ImportCountMatchesExpected = cairoTotal == ExpectedCairoImportCount,
            allImportsTotal = importRows.Count,
            allImportsReachable = importRows.Count(i => i.reachable),
            uniqueCallsiteDenominator = instructionRows.Select(CallsiteKey).Distinct(Ord).Count(),
            uniqueCallsiteDefinition = "over assemblies the game loads (stage root, Lib, Mods; donor assembly copies excluded and counted separately): distinct (assembly relative path, caller method full name, IL offset) over call/callvirt/newobj/ldftn/ldvirtftn/ldfld/ldflda/ldsfld/ldsflda/stfld/stsfld instructions whose operand is a cairo-sharp member",
            instructionRowCount = instructionRows.Count,
            distinctCallerMethods = instructionRows.Select(r => r.Assembly.RelativePath + "|" + r.MethodName).Distinct(Ord).Count(),
            distinctCallerMemberPairsAcrossAssemblies = instructionRows.Select(r => r.MethodName + "=>" + r.Member).Distinct(Ord).Count(),
            structuralEntryCount = loadedRows.Count(r => !r.IsInstruction),
            donorAssemblyCopyCallsites = donorRows.Where(r => r.IsInstruction).Select(CallsiteKey).Distinct(Ord).Count(),
            unresolvedWrapperReferenceRows = rows.Count(r => !r.Resolved),
            typeReferenceOperandCountNotInLedger = typeOperandCount,
            unreadableMethodBodies = unreadableBodies,
            distinctWrapperMembersReachedByCallsites = instructionRows.Where(r => r.Resolved).Select(r => r.Member).Distinct(Ord).Count(),
            wrapperMethodsInConservativeClosure = conservative.Order.Count,
            wrapperMethodsInExplicitClosure = strict.Order.Count,
            byOrigin = CountBy(instructionRows, r => r.Origin),
            byCategory = CountBy(instructionRows, r => r.Category),
            byOriginAndCategory = CountBy(instructionRows, r => r.Origin + " / " + r.Category),
            byOpcode = CountBy(instructionRows, r => r.Opcode),
            byAssembly = CountBy(instructionRows, r => r.Assembly.RelativePath),
            reflectionCandidateCount = reflection.Count,
            traceFailures
        };

        var document = new
        {
            schemaVersion = 1,
            analysis = "static-IL-callsite-ledger-and-reachability-closure",
            limitations = new[]
            {
                "Static IL scan only: no reflection, emitted IL, Harmony runtime patches, native callbacks, scripting or third-party mod calls are resolved. Reflection candidates are listed, never counted as callsites or as reachability seeds. In particular ModCompilationContext adds cairo-sharp.dll to the compile references of runtime-compiled mods, so mods can reach any public wrapper member (see reflectionCandidates).",
                "Reachability is an over-approximation of what first-party IL can reach (class-hierarchy virtual dispatch, no data-flow or dead-branch pruning). 'reachableStrict' omits implicit finalizer/Dispose edges; 'reachable' includes them for every instantiated wrapper type.",
                "Origin 'vanilla' means the method's multiset of wrapper references equals the same method in the vanilla assembly; it does not prove the rest of the IL is unmodified. Ownership lists come from the working tree (patches/, .baseline/, sources/, cecil-owned.list, Optimum.Patcher string literals) and can drift from the binaries that were staged.",
                "A stage is the set of assemblies selected by the rules in 'exclusionRules'; assemblies loaded from elsewhere at run time (user mods, per-instance caches) are outside the ledger."
            },
            stage = new
            {
                scannedAssemblyCount = assemblies.Count(a => a.Role == "caller"),
                scannedAssembliesReferencingWrapper = callers.Count(a => a.ReferencesWrapper),
                exclusionRules,
                excluded,
                assemblies = assemblies.Select(a => new
                {
                    path = a.RelativePath, group = a.Group, role = a.Role, identity = a.Definition.Name.FullName,
                    sha256 = a.Sha256, referencesWrapper = a.ReferencesWrapper
                }).ToList()
            },
            wrapper = new
            {
                path = wrapperEntry.RelativePath,
                identity = wrapperEntry.Definition.Name.FullName,
                sha256 = wrapperEntry.Sha256,
                typeCount = wrapper.Types.Count,
                methodCount = wrapper.Methods.Count,
                dynamicNativeCallIndicators = wrapper.DynamicNativeCallIndicators
            },
            vanillaBaseline = new
            {
                directory = vanilla.Count == 0 ? "unavailable" : "repo:.vanilla/linux-x64/vintagestory",
                assemblies = vanilla.OrderBy(kv => kv.Key, Ord).Select(kv => new { path = kv.Key, sha256 = kv.Value.Sha256, identicalToStage = kv.Value.IdenticalToStage }).ToList()
            },
            ownershipSources = ownership.Describe(),
            summary,
            reachability = new { libraries = libraryRows, imports = importRows },
            callerMethods = rows.GroupBy(r => (r.Assembly.RelativePath, r.TypeName, r.MethodName, r.IsInstruction)).Select(g =>
            {
                LedgerRow first = g.First();
                return new
                {
                    assembly = first.Assembly.RelativePath,
                    group = first.Assembly.Group,
                    type = first.TypeName,
                    method = first.MethodName,
                    origin = first.Origin,
                    ownership = first.Ownership,
                    category = first.Category,
                    vanillaComparison = g.Select(r => r.VanillaComparison).Distinct(Ord).OrderBy(x => x, Ord).ToList(),
                    callsites = g.Select(r => new
                    {
                        offset = r.Offset,
                        opcode = r.Opcode,
                        kind = r.Kind,
                        member = r.Member,
                        memberKind = r.MemberKind,
                        resolved = r.Resolved
                    }).ToList()
                };
            }).ToList(),
            guiFixtureManifestCrossCheck = CrossCheckGuiManifest(repo, loadedRows),
            reflectionCandidates = reflection,
            traceValidation = traceReports.Select(t => t.Json).ToList()
        };

        string? outputDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.Output));
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(options.Output, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n", new UTF8Encoding(false));

        Console.WriteLine(headline + $" (explicit-only edges: {cairoReachableStrict}/{cairoTotal}).");
        Console.WriteLine($"Unique callsites: {summary.uniqueCallsiteDenominator} ({instructionRows.Count} rows, {summary.distinctCallerMethods} caller methods, {summary.structuralEntryCount} structural entries, {summary.unresolvedWrapperReferenceRows} unresolved).");
        foreach (var origin in instructionRows.GroupBy(r => r.Origin, Ord).OrderBy(g => g.Key, Ord))
            Console.WriteLine($"  origin {origin.Key}: {origin.Select(CallsiteKey).Distinct(Ord).Count()} callsites");
        foreach (var report in traceReports)
            Console.WriteLine($"Trace {report.Path}: {report.Called} called members, {report.Matched} matched, {report.Reached} reachable, {report.Failures} failures.");
        Console.WriteLine($"Wrote {options.Output}");
        return traceFailures == 0 ? 0 : 3;
    }

    // ------------------------------------------------------------------ options and discovery

    private static Options ParseOptions(string[] args)
    {
        Options options = new();
        List<string> positional = [];
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trace": options.Traces.Add(System.IO.Path.GetFullPath(RequireValue(args, ref i))); break;
                case "--all-traces": options.AllTraces = true; break;
                case "--repo": options.Repo = RequireValue(args, ref i); break;
                case "--vanilla": options.Vanilla = RequireValue(args, ref i); break;
                default: positional.Add(args[i]); break;
            }
        }
        if (positional.Count != 2)
            throw new ArgumentException("Usage: --cairo-callsite-ledger <stageDir> <outJson> [--trace <cairo-api-usage.json>]... [--all-traces] [--repo <optimumRoot>] [--vanilla <vanillaDir>]");
        options.Stage = positional[0];
        options.Output = System.IO.Path.GetFullPath(positional[1]);
        return options;
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"{args[index]} requires a value.");
        return args[++index];
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        static bool IsRepo(string directory) => File.Exists(System.IO.Path.Combine(directory, "patches", "cecil-owned.list"));
        if (explicitRoot != null)
        {
            string full = System.IO.Path.GetFullPath(explicitRoot);
            if (!IsRepo(full)) throw new DirectoryNotFoundException($"--repo does not look like an Optimum checkout (missing patches/cecil-owned.list): {full}");
            return full;
        }
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
                if (IsRepo(dir.FullName)) return dir.FullName;
        throw new DirectoryNotFoundException("Could not locate the Optimum checkout; pass --repo <optimumRoot>.");
    }

    private static List<StageAssembly> LoadStageAssemblies(string stage, out List<object> excluded, out List<string> exclusionRules)
    {
        exclusionRules =
        [
            "included: stage root *.dll (non-recursive), Lib/**, Mods/**, .optimum/donors/*.dll",
            "excluded by name: *before-subpixel*, *.vanilla.dll, *.original.dll (stale or vanilla patcher inputs)",
            "excluded by location: every other directory (evidence, artifacts*, pharos-*, data*, runtimes, .optimum/vanilla, tmp*, ...)"
        ];
        SortedDictionary<string, string> included = new(Ord);
        List<(string Path, string Reason)> skipped = [];
        void Consider(string full, string group)
        {
            string rel = RelativePath(stage, full);
            string name = System.IO.Path.GetFileName(full);
            if (name.Contains("before-subpixel", StringComparison.OrdinalIgnoreCase)) { skipped.Add((rel, "stale-before-subpixel")); return; }
            if (name.EndsWith(".vanilla.dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".original.dll", StringComparison.OrdinalIgnoreCase)) { skipped.Add((rel, "vanilla-patcher-input")); return; }
            included[rel] = group;
        }
        foreach (string f in Directory.EnumerateFiles(stage, "*.dll", SearchOption.TopDirectoryOnly)) Consider(f, "stage-root");
        string lib = System.IO.Path.Combine(stage, "Lib");
        if (Directory.Exists(lib)) foreach (string f in Directory.EnumerateFiles(lib, "*.dll", SearchOption.AllDirectories)) Consider(f, "stage-lib");
        string mods = System.IO.Path.Combine(stage, "Mods");
        if (Directory.Exists(mods)) foreach (string f in Directory.EnumerateFiles(mods, "*.dll", SearchOption.AllDirectories)) Consider(f, "stage-mods");
        string donors = System.IO.Path.Combine(stage, ".optimum", "donors");
        if (Directory.Exists(donors)) foreach (string f in Directory.EnumerateFiles(donors, "*.dll", SearchOption.TopDirectoryOnly)) Consider(f, "runtime-donor");

        HashSet<string> includedPaths = new(included.Keys, Ord);
        SortedDictionary<string, int> excludedByTopLevel = new(Ord);
        foreach (string f in Directory.EnumerateFiles(stage, "*.dll", SearchOption.AllDirectories))
        {
            string rel = RelativePath(stage, f);
            if (includedPaths.Contains(rel) || skipped.Any(s => s.Path == rel)) continue;
            int slash = rel.IndexOf('/');
            string top = slash < 0 ? "(stage root)" : rel[..slash];
            excludedByTopLevel[top] = excludedByTopLevel.GetValueOrDefault(top) + 1;
        }
        excluded =
        [
            .. skipped.OrderBy(s => s.Path, Ord).Select(s => (object)new { path = s.Path, reason = s.Reason }),
            .. excludedByTopLevel.Select(kv => (object)new { topLevelDirectory = kv.Key, dllCount = kv.Value, reason = "outside-runtime-stage-rules" })
        ];

        List<StageAssembly> result = [];
        foreach ((string rel, string group) in included)
        {
            string full = System.IO.Path.Combine(stage, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            try
            {
                AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(full, new ReaderParameters { InMemory = true, ReadSymbols = false });
                result.Add(new StageAssembly { RelativePath = rel, Group = group, FullPath = full, Sha256 = Sha256(full), Definition = definition });
            }
            catch (BadImageFormatException)
            {
                excluded.Add(new { path = rel, reason = "not-managed" });
            }
        }
        return result;
    }

    private static string RelativePath(string root, string full) =>
        System.IO.Path.GetRelativePath(root, full).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    private static Dictionary<string, TypeDefinition> BuildTypeIndex(IEnumerable<StageAssembly> callers)
    {
        Dictionary<string, TypeDefinition> index = new(Ord);
        foreach (StageAssembly assembly in callers.OrderBy(a => a.Group == "runtime-donor" ? 1 : 0).ThenBy(a => a.RelativePath, Ord))
            foreach (TypeDefinition type in AllTypes(assembly.Definition.MainModule))
                index.TryAdd(type.FullName, type);
        return index;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) => module.Types.SelectMany(Flatten);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    // ------------------------------------------------------------------ wrapper model

    private sealed class WrapperModel
    {
        private readonly Dictionary<string, List<MethodDefinition>> byNameParams = new(Ord);
        private readonly Dictionary<MethodDefinition, List<MethodDefinition>> overrideCache = new();

        public WrapperModel(AssemblyDefinition assembly)
        {
            foreach (TypeDefinition type in AllTypes(assembly.MainModule))
            {
                Types.TryAdd(type.FullName, type);
                foreach (FieldDefinition field in type.Fields) Fields.TryAdd(field.FullName, field);
                foreach (MethodDefinition method in type.Methods)
                {
                    Methods.TryAdd(method.FullName, method);
                    if (method.IsPInvokeImpl) PInvokes.Add(method);
                    string key = method.Name + "|" + ParamKey(method);
                    if (!byNameParams.TryGetValue(key, out List<MethodDefinition>? list)) byNameParams[key] = list = [];
                    list.Add(method);
                    if (method.HasBody)
                        foreach (Instruction instruction in method.Body.Instructions)
                            if (instruction.OpCode.Code == Code.Calli ||
                                instruction.Operand is MethodReference { Name: "GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate" or "GetExport" or "GetProcAddress" })
                                DynamicNativeCallIndicators++;
                }
            }
        }

        public Dictionary<string, TypeDefinition> Types { get; } = new(Ord);
        public Dictionary<string, MethodDefinition> Methods { get; } = new(Ord);
        public Dictionary<string, FieldDefinition> Fields { get; } = new(Ord);
        public List<MethodDefinition> PInvokes { get; } = [];
        public int DynamicNativeCallIndicators { get; private set; }

        public static string ParamKey(IMethodSignature method) =>
            string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName)) + "#" + (method is MethodReference { HasGenericParameters: true } r ? r.GenericParameters.Count : 0);

        public TypeDefinition? BaseOf(TypeDefinition type) =>
            type.BaseType != null && Types.TryGetValue(type.BaseType.GetElementType().FullName, out TypeDefinition? b) ? b : null;

        public bool DerivesFrom(TypeDefinition derived, TypeDefinition baseType)
        {
            int guard = 0;
            for (TypeDefinition? t = derived; t != null && guard++ < 64; t = BaseOf(t))
                if (ReferenceEquals(t, baseType)) return true;
            return false;
        }

        public bool Implements(TypeDefinition type, TypeDefinition @interface)
        {
            int guard = 0;
            for (TypeDefinition? t = type; t != null && guard++ < 64; t = BaseOf(t))
                if (InterfaceClosure(t).Contains(@interface)) return true;
            return false;
        }

        private HashSet<TypeDefinition> InterfaceClosure(TypeDefinition type)
        {
            HashSet<TypeDefinition> result = [];
            Stack<TypeDefinition> pending = new([type]);
            while (pending.Count > 0)
                foreach (InterfaceImplementation i in pending.Pop().Interfaces)
                    if (Types.TryGetValue(i.InterfaceType.GetElementType().FullName, out TypeDefinition? def) && result.Add(def))
                        pending.Push(def);
            return result;
        }

        public MethodDefinition? FindVirtual(TypeDefinition startType, string name, string paramKey)
        {
            int guard = 0;
            for (TypeDefinition? t = startType; t != null && guard++ < 64; t = BaseOf(t))
                foreach (MethodDefinition m in t.Methods)
                    if (m.IsVirtual && m.Name == name && ParamKey(m) == paramKey) return m;
            return null;
        }

        public MethodDefinition? ResolveMethod(MethodReference reference)
        {
            MethodReference element = reference.GetElementMethod();
            if (Methods.TryGetValue(element.FullName, out MethodDefinition? exact)) return exact;
            if (!Types.TryGetValue(element.DeclaringType.GetElementType().FullName, out TypeDefinition? type)) return null;
            string key = ParamKey(element);
            int guard = 0;
            for (TypeDefinition? t = type; t != null && guard++ < 64; t = BaseOf(t))
            {
                foreach (MethodDefinition m in t.Methods)
                    if (m.Name == element.Name && ParamKey(m) == key) return m;
                foreach (TypeDefinition i in InterfaceClosure(t).OrderBy(i => i.FullName, Ord))
                    foreach (MethodDefinition m in i.Methods)
                        if (m.Name == element.Name && ParamKey(m) == key) return m;
            }
            return null;
        }

        public FieldDefinition? ResolveField(FieldReference reference)
        {
            if (Fields.TryGetValue(reference.FullName, out FieldDefinition? exact)) return exact;
            if (!Types.TryGetValue(reference.DeclaringType.GetElementType().FullName, out TypeDefinition? type)) return null;
            int guard = 0;
            for (TypeDefinition? t = type; t != null && guard++ < 64; t = BaseOf(t))
                foreach (FieldDefinition f in t.Fields)
                    if (f.Name == reference.Name) return f;
            return null;
        }

        public MethodDefinition? StaticConstructor(TypeDefinition type) => type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);

        public IReadOnlyList<MethodDefinition> Overrides(MethodDefinition target)
        {
            if (!target.IsVirtual && !target.DeclaringType.IsInterface) return [];
            if (overrideCache.TryGetValue(target, out List<MethodDefinition>? cached)) return cached;
            List<MethodDefinition> result = [];
            if (byNameParams.TryGetValue(target.Name + "|" + ParamKey(target), out List<MethodDefinition>? candidates))
                foreach (MethodDefinition c in candidates)
                    if (!ReferenceEquals(c, target) && c.IsVirtual &&
                        (DerivesFrom(c.DeclaringType, target.DeclaringType) || (target.DeclaringType.IsInterface && Implements(c.DeclaringType, target.DeclaringType))))
                        result.Add(c);
            foreach (MethodDefinition m in Methods.Values)
                if (m.HasOverrides && m.Overrides.Any(o => o.GetElementMethod().FullName == target.FullName) && !result.Contains(m))
                    result.Add(m);
            result.Sort((a, b) => Ord.Compare(a.FullName, b.FullName));
            overrideCache[target] = result;
            return result;
        }

        public IEnumerable<MethodDefinition> LifecycleMethods(TypeDefinition constructed)
        {
            int guard = 0;
            for (TypeDefinition? t = constructed; t != null && guard++ < 64; t = BaseOf(t))
                foreach (MethodDefinition m in t.Methods.Where(m => !m.IsStatic && (m.Name == "Finalize" || m.Name == "Dispose")).OrderBy(m => m.FullName, Ord))
                    yield return m;
        }
    }

    private static bool IsWrapperScope(TypeReference? type)
    {
        if (type == null) return false;
        TypeReference element = type.GetElementType();
        return element.Scope is AssemblyNameReference reference && string.Equals(reference.Name, WrapperAssemblyName, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ scanning

    private static void ScanAssembly(StageAssembly assembly, WrapperModel wrapper, List<LedgerRow> rows,
        Dictionary<string, Dictionary<string, List<string>>> stageCairoCalls, ref int typeOperandCount, ref int unreadableBodies)
    {
        Dictionary<string, List<string>> calls = [];
        stageCairoCalls[assembly.RelativePath] = calls;
        foreach (TypeDefinition type in AllTypes(assembly.Definition.MainModule))
        {
            if (IsWrapperScope(type.BaseType))
                rows.Add(StructuralRow(assembly, type, null, "inherits-wrapper-type", type.BaseType!.GetElementType().FullName, wrapper.Types.GetValueOrDefault(type.BaseType.GetElementType().FullName)));
            foreach (InterfaceImplementation i in type.Interfaces.Where(i => IsWrapperScope(i.InterfaceType)))
                rows.Add(StructuralRow(assembly, type, null, "implements-wrapper-interface", i.InterfaceType.GetElementType().FullName, null));

            foreach (MethodDefinition method in type.Methods)
            {
                if (method.IsVirtual && !method.IsNewSlot && type.BaseType != null && IsWrapperScope(type.BaseType) &&
                    wrapper.Types.TryGetValue(type.BaseType.GetElementType().FullName, out TypeDefinition? wrapperBase) &&
                    wrapper.FindVirtual(wrapperBase, method.Name, WrapperModel.ParamKey(method)) is { } overridden)
                    rows.Add(StructuralRow(assembly, type, method, "overrides-wrapper-member", overridden.FullName, null, overridden));

                if (ComposeEntryPointNames.Contains(method.Name, Ord) && method.Parameters.Any(p => IsWrapperScope(p.ParameterType)))
                    rows.Add(StructuralRow(assembly, type, method, (method.IsVirtual ? "override-" : "nonvirtual-") + method.Name,
                        $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.ParameterType.FullName))})", null));

                if (!method.HasBody) continue;
                Collection<Instruction> instructions;
                try { instructions = method.Body.Instructions; }
                catch (Exception) { unreadableBodies++; continue; }
                foreach (Instruction instruction in instructions)
                {
                    LedgerRow? row = null;
                    switch (instruction.Operand)
                    {
                        case MethodReference mr when IsInstructionKind(instruction.OpCode.Code) && IsWrapperScope(mr.DeclaringType):
                        {
                            MethodDefinition? target = wrapper.ResolveMethod(mr);
                            row = new LedgerRow
                            {
                                Assembly = assembly, Type = type, Method = method, Offset = instruction.Offset,
                                Kind = instruction.OpCode.Code == Code.Newobj ? "construct" : instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn ? "delegate" : "call",
                                Opcode = instruction.OpCode.Name, Member = target?.FullName ?? mr.FullName, MemberKind = "method",
                                ReferenceName = mr.GetElementMethod().FullName, Resolved = target != null, TargetMethod = target
                            };
                            break;
                        }
                        case FieldReference fr when IsWrapperScope(fr.DeclaringType):
                        {
                            FieldDefinition? target = wrapper.ResolveField(fr);
                            row = new LedgerRow
                            {
                                Assembly = assembly, Type = type, Method = method, Offset = instruction.Offset, Kind = "field",
                                Opcode = instruction.OpCode.Name, Member = target?.FullName ?? fr.FullName, MemberKind = "field",
                                ReferenceName = fr.FullName, Resolved = target != null, TargetField = target
                            };
                            break;
                        }
                        case TypeReference tr when IsWrapperScope(tr):
                            typeOperandCount++;
                            break;
                    }
                    if (row == null) continue;
                    rows.Add(row);
                    if (!calls.TryGetValue(method.FullName, out List<string>? list)) calls[method.FullName] = list = [];
                    list.Add(row.ReferenceName);
                }
            }
        }
        foreach (List<string> list in calls.Values) list.Sort(Ord);
    }

    private static LedgerRow StructuralRow(StageAssembly assembly, TypeDefinition type, MethodDefinition? method, string kind, string member,
        TypeDefinition? targetType, MethodDefinition? targetMethod = null) =>
        new()
        {
            Assembly = assembly, Type = type, Method = method ?? type.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic) ?? new MethodDefinition("<type>", MethodAttributes.Public, type.Module.TypeSystem.Void) { DeclaringType = type },
            Offset = -1, Kind = kind, Opcode = "structural", Member = member, MemberKind = targetType != null ? "type" : targetMethod != null ? "method" : "signature",
            ReferenceName = member, Resolved = targetType != null || targetMethod != null || kind.StartsWith("override-Compose", StringComparison.Ordinal) || kind.StartsWith("nonvirtual-Compose", StringComparison.Ordinal),
            TargetMethod = targetMethod
        };

    private static bool IsInstructionKind(Code code) =>
        code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn;

    private static void ScanReflection(StageAssembly assembly, List<object> reflection)
    {
        if (assembly.Role != "caller") return;
        foreach (TypeDefinition type in AllTypes(assembly.Definition.MainModule))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                Collection<Instruction> instructions;
                try { instructions = method.Body.Instructions; }
                catch (Exception) { continue; }
                foreach (Instruction instruction in instructions)
                {
                    if (instruction.OpCode.Code == Code.Ldstr && instruction.Operand is string text && text.Contains("Cairo", StringComparison.OrdinalIgnoreCase))
                        reflection.Add(new
                        {
                            assembly = assembly.RelativePath, type = type.FullName, method = method.FullName, offset = instruction.Offset,
                            kind = "ldstr-contains-cairo", detail = text.Length > 200 ? text[..200] : text, origin = "reflection-candidate",
                            note = ReflectionNote(assembly, type, text)
                        });
                    else if (instruction.OpCode.Code == Code.Ldtoken && instruction.Operand is TypeReference tr && IsWrapperScope(tr))
                        reflection.Add(new
                        {
                            assembly = assembly.RelativePath, type = type.FullName, method = method.FullName, offset = instruction.Offset,
                            kind = "ldtoken-wrapper-type", detail = tr.FullName, origin = "reflection-candidate",
                            note = "typeof/ldtoken of a wrapper type; members it reaches through reflection are not resolved."
                        });
                }
            }
    }

    private static string ReflectionNote(StageAssembly assembly, TypeDefinition type, string text)
    {
        if (assembly.Name == "Optimum.Patcher") return "patch-time-only: Cecil patcher metadata, not executed by the running game.";
        if (type.FullName.Contains("ModCompilationContext", StringComparison.Ordinal) && text.Contains("cairo-sharp", StringComparison.OrdinalIgnoreCase))
            return "mod-compilation-reference: runtime-compiled third-party mods reference cairo-sharp.dll and can call any public wrapper member; not statically enumerable.";
        if (text.Contains("CAIRO_DEBUG_DISPOSE", StringComparison.Ordinal)) return "environment-variable-name: toggles wrapper allocation tracing; not a member reference.";
        return "string-only: no member binding resolved.";
    }

    private static void ScanHarmonyTargets(IEnumerable<StageAssembly> callers, List<LedgerRow> rows,
        Dictionary<string, TypeDefinition> typeIndex, List<object> reflection)
    {
        HashSet<string> typesWithRows = rows.Where(r => r.IsInstruction).Select(r => OuterType(r.Type).FullName).ToHashSet(Ord);
        HashSet<string> methodsWithRows = rows.Where(r => r.IsInstruction).Select(r => OuterType(r.Type).FullName + "::" + r.Method.Name).ToHashSet(Ord);
        foreach (StageAssembly assembly in callers.Where(a => a.Group != "stage-lib" && a.Role == "caller"))
            foreach (TypeDefinition type in AllTypes(assembly.Definition.MainModule))
            {
                foreach ((ICustomAttributeProvider provider, string owner) in new[] { ((ICustomAttributeProvider)type, type.FullName) }
                    .Concat(type.Methods.Select(m => ((ICustomAttributeProvider)m, m.FullName))))
                    foreach (CustomAttribute attribute in provider.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                    {
                        string? targetType = attribute.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Select(t => t.FullName).FirstOrDefault();
                        string? targetMethod = attribute.ConstructorArguments.Select(a => a.Value).OfType<string>().FirstOrDefault();
                        bool reaches = targetType != null &&
                            (targetType.StartsWith("Cairo.", StringComparison.Ordinal) || typesWithRows.Contains(targetType) &&
                             (targetMethod == null || methodsWithRows.Contains(targetType + "::" + targetMethod)));
                        if (reaches)
                            reflection.Add(new
                            {
                                assembly = assembly.RelativePath, type = type.FullName, method = owner, offset = -1,
                                kind = "harmony-patch-target-reaches-wrapper", detail = targetType + "::" + (targetMethod ?? "*"), origin = "reflection-candidate",
                                note = "Harmony patch target whose type/method contains wrapper callsites."
                            });
                    }
            }
        reflection.Sort((a, b) => Ord.Compare(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b)));
    }

    private static TypeDefinition OuterType(TypeDefinition type)
    {
        TypeDefinition t = type;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t;
    }

    private static string BaseName(string typeName)
    {
        int tick = typeName.IndexOf('`');
        string stripped = tick >= 0 ? typeName[..tick] : typeName;
        int dot = stripped.LastIndexOf('.');
        return dot >= 0 ? stripped[(dot + 1)..] : stripped;
    }

    private static string StripArity(string fullName)
    {
        int tick = fullName.IndexOf('`');
        return tick >= 0 ? fullName[..tick] : fullName;
    }

    private static string CallsiteKey(LedgerRow row) => row.Assembly.RelativePath + "|" + row.MethodName + "|" + row.Offset;

    private static int CompareRows(LedgerRow a, LedgerRow b)
    {
        int c = Ord.Compare(a.Assembly.RelativePath, b.Assembly.RelativePath);
        if (c != 0) return c;
        c = Ord.Compare(a.TypeName, b.TypeName);
        if (c != 0) return c;
        c = Ord.Compare(a.MethodName, b.MethodName);
        if (c != 0) return c;
        c = a.Offset.CompareTo(b.Offset);
        if (c != 0) return c;
        c = Ord.Compare(a.Kind, b.Kind);
        return c != 0 ? c : Ord.Compare(a.Member, b.Member);
    }

    // ------------------------------------------------------------------ origin classification

    private sealed class VanillaBaseline
    {
        public required string Sha256 { get; init; }
        public required bool IdenticalToStage { get; init; }
        public Dictionary<string, List<string>>? Calls { get; init; }
        public HashSet<string>? Methods { get; init; }
        public HashSet<string>? Types { get; init; }
    }

    private static Dictionary<string, VanillaBaseline> LoadVanillaBaselines(string vanillaDirectory, IEnumerable<StageAssembly> callers)
    {
        Dictionary<string, VanillaBaseline> result = new(Ord);
        if (!Directory.Exists(vanillaDirectory)) return result;
        foreach (StageAssembly assembly in callers.Where(a => a.Group is "stage-root" or "stage-mods" && a.ReferencesWrapper))
        {
            string path = System.IO.Path.Combine(vanillaDirectory, assembly.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            string hash = Sha256(path);
            bool identical = string.Equals(hash, assembly.Sha256, StringComparison.Ordinal);
            Dictionary<string, List<string>>? calls = null;
            HashSet<string>? methods = null, types = null;
            if (!identical)
            {
                calls = [];
                methods = new(Ord);
                types = new(Ord);
                using AssemblyDefinition vanilla = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true, ReadSymbols = false });
                foreach (TypeDefinition type in AllTypes(vanilla.MainModule))
                {
                    types.Add(type.FullName);
                    foreach (MethodDefinition method in type.Methods) methods.Add(method.FullName);
                    foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            string? name = instruction.Operand switch
                            {
                                MethodReference mr when IsInstructionKind(instruction.OpCode.Code) && IsWrapperScope(mr.DeclaringType) => mr.GetElementMethod().FullName,
                                FieldReference fr when IsWrapperScope(fr.DeclaringType) => fr.FullName,
                                _ => null
                            };
                            if (name == null) continue;
                            if (!calls.TryGetValue(method.FullName, out List<string>? list)) calls[method.FullName] = list = [];
                            list.Add(name);
                        }
                }
                foreach (List<string> list in calls.Values) list.Sort(Ord);
            }
            result[assembly.RelativePath] = new VanillaBaseline { Sha256 = hash, IdenticalToStage = identical, Calls = calls, Methods = methods, Types = types };
        }
        return result;
    }

    private sealed class Ownership
    {
        private readonly Dictionary<string, SortedSet<string>> byMechanism = new(Ord)
        {
            ["runtime-donor"] = new(Ord), ["cecil-owned"] = new(Ord), ["source-patch"] = new(Ord), ["source-added"] = new(Ord)
        };
        private string cecilListSha = "", patcherSourceSha = "", patcherText = "";
        private int patcherLiteralCount, runtimePatchFiles, sourcePatchFiles, addedSourceFiles, cecilListEntries;

        public static Ownership Load(string repo)
        {
            Ownership o = new();
            string patches = System.IO.Path.Combine(repo, "patches");
            string baseline = System.IO.Path.Combine(repo, ".baseline");
            string sources = System.IO.Path.Combine(repo, "sources");

            // Source patches and runtime donor patches.
            foreach (string patch in Directory.EnumerateFiles(patches, "*.patch", SearchOption.AllDirectories).OrderBy(p => p, Ord))
            {
                string rel = RelativePath(patches, patch);
                if (rel.EndsWith(".patch", StringComparison.Ordinal)) rel = rel[..^".patch".Length];
                string[] parts = rel.Split('/');
                bool runtime = parts[0] == "runtime";
                string dir = runtime ? parts[1] : parts[0];
                string assembly = runtime ? dir : SourceDirectoryToAssembly.GetValueOrDefault(dir, dir);
                if (assembly == WrapperAssemblyName) continue;
                string inner = string.Join('/', parts.Skip(runtime ? 2 : 1));
                List<string> names = [];
                string patchText = PatchedText(File.ReadAllText(patch));
                if (runtime)
                {
                    string ns = string.Join('.', parts.Skip(2).Take(parts.Length - 3));
                    names.AddRange(ExtractTypes(patchText, ns));
                    names.Add((ns.Length > 0 ? ns + "." : "") + System.IO.Path.GetFileNameWithoutExtension(inner));
                    o.runtimePatchFiles++;
                }
                else
                {
                    string baseFile = System.IO.Path.Combine(baseline, dir, inner.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    string baseText = File.Exists(baseFile) ? File.ReadAllText(baseFile) : "";
                    List<string> baseTypes = ExtractTypes(baseText, null);
                    names.AddRange(baseTypes);
                    string? ns = baseTypes.Count > 0 && baseTypes[0].Contains('.') ? baseTypes[0][..baseTypes[0].LastIndexOf('.')] : null;
                    names.AddRange(ExtractTypes(patchText, ns));
                    o.sourcePatchFiles++;
                }
                foreach (string name in names.Distinct(Ord))
                    o.byMechanism[runtime ? "runtime-donor" : "source-patch"].Add(assembly + "|" + name);
                if (!runtime && assembly == "VintagestoryLib")
                    foreach (string name in names.Distinct(Ord)) o.byMechanism["source-patch"].Add("*|" + name);
            }

            // Optimum-added source files (not in the vanilla baseline).
            if (Directory.Exists(sources))
                foreach (string dir in Directory.EnumerateDirectories(sources).OrderBy(d => d, Ord))
                {
                    string dirName = System.IO.Path.GetFileName(dir);
                    if (!SourceDirectoryToAssembly.TryGetValue(dirName, out string? assembly) || assembly == WrapperAssemblyName) continue;
                    foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")).OrderBy(f => f, Ord))
                    {
                        string inner = RelativePath(dir, file);
                        if (File.Exists(System.IO.Path.Combine(baseline, dirName, inner.Replace('/', System.IO.Path.DirectorySeparatorChar)))) continue;
                        o.addedSourceFiles++;
                        foreach (string name in ExtractTypes(File.ReadAllText(file), null))
                            o.byMechanism["source-added"].Add(assembly + "|" + name);
                    }
                }

            // cecil-owned.list: patch path -> baseline file -> declared types.
            string listPath = System.IO.Path.Combine(patches, "cecil-owned.list");
            o.cecilListSha = Sha256(listPath);
            foreach (string raw in File.ReadAllLines(listPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || !line.StartsWith("patches/", StringComparison.Ordinal) || !line.EndsWith(".patch", StringComparison.Ordinal)) continue;
                o.cecilListEntries++;
                string rel = line["patches/".Length..^".patch".Length];
                string[] parts = rel.Split('/');
                string baseFile = System.IO.Path.Combine(baseline, parts[0], string.Join(System.IO.Path.DirectorySeparatorChar, parts.Skip(1)));
                IEnumerable<string> names = File.Exists(baseFile) ? ExtractTypes(File.ReadAllText(baseFile), null) : [];
                string ns = parts.Length > 2 ? parts[1] : "";
                string fileStem = System.IO.Path.GetFileNameWithoutExtension(parts[^1]);
                foreach (string name in names.Append((ns.Length > 0 ? ns + "." : "") + fileStem).Distinct(Ord))
                    o.byMechanism["cecil-owned"].Add("*|" + name);
            }

            // Optimum.Patcher type lists: any dotted string literal; matched against real type names later.
            StringBuilder patcherSource = new();
            foreach (string file in Directory.EnumerateFiles(System.IO.Path.Combine(repo, "Optimum.Patcher"), "*.cs", SearchOption.TopDirectoryOnly).OrderBy(f => f, Ord))
            {
                string text = File.ReadAllText(file);
                patcherSource.Append(text);
                foreach (Match m in PatcherTypeLiteralRegex.Matches(text))
                {
                    o.patcherLiteralCount++;
                    o.byMechanism["cecil-owned"].Add("*|" + m.Groups["name"].Value);
                }
            }
            o.patcherText = patcherSource.ToString();
            o.patcherSourceSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patcherSource.ToString()))).ToLowerInvariant();
            return o;
        }

        public bool PatcherMentions(string identifier) => patcherText.Contains("\"" + identifier + "\"", StringComparison.Ordinal);

        public List<string> For(string assemblyName, string outerTypeFullName)
        {
            string name = StripArity(outerTypeFullName);
            List<string> result = [];
            foreach (string mechanism in new[] { "runtime-donor", "cecil-owned", "source-patch", "source-added" })
                if (byMechanism[mechanism].Contains(assemblyName + "|" + name) || byMechanism[mechanism].Contains("*|" + name))
                    result.Add(mechanism);
            return result;
        }

        public object Describe() => new
        {
            cecilOwnedListSha256 = cecilListSha,
            cecilOwnedListEntries = cecilListEntries,
            patcherSourceSha256 = patcherSourceSha,
            patcherQualifiedStringLiterals = patcherLiteralCount,
            runtimeDonorPatchFiles = runtimePatchFiles,
            sourcePatchFiles,
            addedSourceFiles,
            ownedTypeNames = byMechanism.OrderBy(kv => kv.Key, Ord).Select(kv => new { mechanism = kv.Key, names = kv.Value.Count }).ToList(),
            note = "Ownership comes from the working tree (patches/*.patch, .baseline, sources/, patches/cecil-owned.list, Optimum.Patcher/*.cs string literals); patch files may be regenerated or uncommitted."
        };
    }

    private static string PatchedText(string patch)
    {
        StringBuilder builder = new();
        foreach (string line in patch.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal) || line[0] == '-' || line[0] == '\\')
                continue;
            builder.Append(line.AsSpan(1)).Append('\n');
        }
        return builder.ToString();
    }

    private static List<string> ExtractTypes(string text, string? fallbackNamespace)
    {
        List<string> result = [];
        string? ns = fallbackNamespace;
        foreach (Match m in DeclarationRegex.Matches(text))
        {
            if (m.Groups["ns"].Success) { ns = m.Groups["ns"].Value; continue; }
            if (m.Groups["type"].Success) result.Add((string.IsNullOrEmpty(ns) ? "" : ns + ".") + m.Groups["type"].Value);
        }
        return result;
    }

    private static void Classify(List<LedgerRow> rows, Ownership ownership, Dictionary<string, VanillaBaseline> vanilla,
        Dictionary<string, Dictionary<string, List<string>>> stageCalls, Dictionary<string, TypeDefinition> typeIndex)
    {
        Dictionary<string, string> categoryCache = new(Ord);
        foreach (LedgerRow row in rows)
        {
            TypeDefinition outer = OuterType(row.Type);
            row.Ownership = ownership.For(row.Assembly.Name, outer.FullName);

            string comparison = "no-vanilla-counterpart";
            bool matches = false;
            if (vanilla.TryGetValue(row.Assembly.RelativePath, out VanillaBaseline? baseline))
            {
                if (baseline.IdenticalToStage) { comparison = "assembly-identical-to-vanilla"; matches = true; }
                else if (row.IsInstruction && baseline.Calls != null)
                {
                    bool hasStage = stageCalls[row.Assembly.RelativePath].TryGetValue(row.MethodName, out List<string>? mine);
                    bool hasVanilla = baseline.Calls.TryGetValue(row.MethodName, out List<string>? theirs);
                    matches = hasStage && hasVanilla && mine!.SequenceEqual(theirs!, Ord);
                    comparison = matches ? "method-wrapper-references-identical-to-vanilla" : hasVanilla ? "method-wrapper-references-differ-from-vanilla" : "method-absent-or-without-wrapper-references-in-vanilla";
                }
                else
                {
                    bool present = row.Kind.StartsWith("inherits", StringComparison.Ordinal) || row.Kind.StartsWith("implements", StringComparison.Ordinal)
                        ? baseline.Types?.Contains(row.TypeName) == true
                        : baseline.Methods?.Contains(row.MethodName) == true;
                    matches = present;
                    comparison = present ? "structural-entry-present-in-vanilla" : "structural-entry-absent-from-vanilla";
                }
            }
            row.VanillaComparison = comparison;

            if (row.Assembly.Group == "runtime-donor") row.Origin = "donor-assembly-copy";
            else if (row.Assembly.Name.StartsWith("Optimum", StringComparison.Ordinal)) row.Origin = "optimum-own";
            else if (matches) row.Origin = "vanilla";
            else if (row.Ownership.Contains("runtime-donor")) row.Origin = "runtime-donor";
            else if (row.Ownership.Contains("cecil-owned")) row.Origin = "cecil-owned";
            else if (row.Ownership.Contains("source-patch") || row.Ownership.Contains("source-added")) row.Origin = "source-patch";
            else if (row.TargetMethod != null && ownership.PatcherMentions(row.TargetMethod.Name))
            {
                row.Origin = "cecil-owned";
                row.Ownership = [.. row.Ownership, "patcher-source-names-wrapper-member"];
            }
            else row.Origin = baseline == null ? "unclassified-no-vanilla-baseline" : "modified-unattributed";

            string key = outer.FullName;
            if (!categoryCache.TryGetValue(key, out string? category)) categoryCache[key] = category = Categorize(outer, typeIndex);
            row.Category = category == "unclassified" && row.Method.Name.StartsWith("Draw", StringComparison.Ordinal) && row.Method.Name.EndsWith("_svg", StringComparison.Ordinal)
                ? "icon-svg-draw" : category;
        }
    }

    private static string Categorize(TypeDefinition outer, Dictionary<string, TypeDefinition> typeIndex)
    {
        List<string> chain = [];
        HashSet<string> seen = new(Ord);
        TypeReference? current = outer;
        while (current != null && chain.Count < 64 && seen.Add(current.FullName))
        {
            chain.Add(BaseName(current.GetElementType().FullName));
            current = current is TypeDefinition def ? def.BaseType : typeIndex.GetValueOrDefault(current.GetElementType().FullName)?.BaseType;
        }
        string self = BaseName(outer.FullName);
        bool guiChain = chain.Any(n => GuiBaseTypeNames.Contains(n, Ord));
        if (NonGuiTypeNames.Contains(self, Ord)) return "non-gui";
        if (!guiChain && chain.Any(n => n.Contains("MapLayer", StringComparison.Ordinal) || n.Contains("MapComponent", StringComparison.Ordinal))) return "non-gui";
        if (guiChain) return "gui";
        string full = outer.FullName;
        if (full.Contains("Gui", StringComparison.Ordinal) || full.Contains("Hud", StringComparison.Ordinal) || full.Contains("RichText", StringComparison.Ordinal) ||
            full.Contains("TextDraw", StringComparison.Ordinal) || full.Contains("Dialog", StringComparison.Ordinal) || full.Contains("Cairo", StringComparison.Ordinal))
            return "gui-support-by-name";
        return "unclassified";
    }

    private static object CrossCheckGuiManifest(string repo, List<LedgerRow> loadedRows)
    {
        string path = System.IO.Path.Combine(repo, "tools", "Cairo.ParityHarness", "gui-fixture-manifest.csv");
        if (!File.Exists(path)) return new { available = false };
        HashSet<string> present = loadedRows.Where(r => r.Kind.StartsWith("override-Compose", StringComparison.Ordinal))
            .Select(r => r.Assembly.Name + "|" + BaseName(OuterType(r.Type).FullName) + "|" + r.Method.Name).ToHashSet(Ord);
        List<string> missing = [];
        int entries = 0;
        foreach (string line in File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0))
        {
            List<string> fields = SplitCsv(line);
            if (fields.Count < 4) continue;
            Match method = Regex.Match(fields[3], @"\b(Compose\w*)\(");
            if (!method.Success) continue;
            entries++;
            string assembly = SourceDirectoryToAssembly.GetValueOrDefault(fields[0], fields[0]);
            string key = assembly + "|" + fields[2] + "|" + method.Groups[1].Value;
            if (!present.Contains(key)) missing.Add(key);
        }
        missing.Sort(Ord);
        return new { available = true, manifestEntries = entries, presentInStageIl = entries - missing.Count, missingFromStageIl = missing };
    }

    private static List<string> SplitCsv(string line)
    {
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; } else if (c == '"') quoted = false; else field.Append(c); }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }

    // ------------------------------------------------------------------ closure

    private static List<MethodDefinition> SelectSeeds(List<LedgerRow> rows, WrapperModel wrapper)
    {
        HashSet<MethodDefinition> seeds = [];
        foreach (LedgerRow row in rows)
        {
            if (row.TargetMethod != null) seeds.Add(row.TargetMethod);
            TypeDefinition? declaring = row.TargetMethod?.DeclaringType ?? row.TargetField?.DeclaringType;
            if (declaring != null && wrapper.StaticConstructor(declaring) is { } cctor) seeds.Add(cctor);
        }
        return seeds.OrderBy(m => m.FullName, Ord).ToList();
    }

    private static Closure ComputeClosure(WrapperModel wrapper, List<MethodDefinition> seeds, bool implicitLifecycle)
    {
        Closure closure = new();
        Queue<MethodDefinition> queue = new();
        void Add(MethodDefinition method, MethodDefinition? parent, string via)
        {
            if (closure.Parent.ContainsKey(method)) return;
            closure.Parent[method] = parent;
            closure.Via[method] = via;
            closure.Order.Add(method);
            queue.Enqueue(method);
        }
        foreach (MethodDefinition seed in seeds) Add(seed, null, "first-party-callsite");
        while (queue.Count > 0)
        {
            MethodDefinition method = queue.Dequeue();
            if (wrapper.StaticConstructor(method.DeclaringType) is { } cctor) Add(cctor, method, "static-constructor");
            foreach (MethodDefinition over in wrapper.Overrides(method)) Add(over, method, "virtual-dispatch");
            if (implicitLifecycle && method.IsConstructor && !method.IsStatic)
                foreach (MethodDefinition life in wrapper.LifecycleMethods(method.DeclaringType)) Add(life, method, "implicit-lifecycle");
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference mr when wrapper.ResolveMethod(mr) is { } target:
                        Add(target, method, instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn ? "delegate" : "call");
                        break;
                    case FieldReference fr when wrapper.ResolveField(fr) is { } field && wrapper.StaticConstructor(field.DeclaringType) is { } fieldCctor:
                        Add(fieldCctor, method, "static-constructor");
                        break;
                }
            }
        }
        return closure;
    }

    private static List<string> WitnessPath(Closure closure, MethodDefinition method)
    {
        List<string> path = [];
        for (MethodDefinition? m = method; m != null && path.Count < 64; m = closure.Parent[m])
            path.Add(m.FullName + (closure.Via[m] is "first-party-callsite" ? "" : $" [{closure.Via[m]}]"));
        path.Reverse();
        return path;
    }

    // ------------------------------------------------------------------ trace validation

    private sealed class TraceReport
    {
        public required string Path { get; init; }
        public int Called, Matched, Reached, Failures;
        public required object Json { get; set; }
    }

    private static List<string> SelectTraces(Options options, string stage)
    {
        List<string> traces = [.. options.Traces];
        string evidence = System.IO.Path.Combine(stage, "evidence");
        List<string> all = Directory.Exists(evidence)
            ? Directory.EnumerateFiles(evidence, "cairo-api-usage.json", SearchOption.AllDirectories).ToList()
            : [];
        if (options.AllTraces) traces.AddRange(all);
        else if (traces.Count == 0 && all.Count > 0)
            traces.Add(all.OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => p, Ord).First());
        return traces.Select(System.IO.Path.GetFullPath).Distinct(Ord).OrderBy(p => p, Ord).ToList();
    }

    private static TraceReport ValidateTrace(string tracePath, string stage, WrapperModel wrapper, Closure strict, Closure conservative, HashSet<MethodDefinition> directlyCalled)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(tracePath));
        JsonElement root = document.RootElement;
        Dictionary<string, Dictionary<string, List<MethodDefinition>>> signatureCache = new(Ord);
        int called = 0, matched = 0, reached = 0;
        List<object> unmatched = [], unreached = [];
        Dictionary<string, int> tiers = new(Ord) { ["direct-first-party-callsite"] = 0, ["transitive-via-wrapper"] = 0, ["implicit-lifecycle-only"] = 0 };
        foreach (JsonElement member in root.GetProperty("members").EnumerateArray())
        {
            if (member.GetProperty("calls").GetInt64() <= 0) continue;
            called++;
            string declaring = member.GetProperty("declaringType").GetString() ?? "";
            string signature = member.GetProperty("signature").GetString() ?? "";
            List<MethodDefinition> targets = MatchTraceMember(wrapper, signatureCache, declaring, signature);
            if (targets.Count == 0) { unmatched.Add(new { declaringType = declaring, signature }); continue; }
            matched++;
            if (targets.Any(t => directlyCalled.Contains(t))) { tiers["direct-first-party-callsite"]++; reached++; }
            else if (targets.Any(t => strict.Parent.ContainsKey(t))) { tiers["transitive-via-wrapper"]++; reached++; }
            else if (targets.Any(t => conservative.Parent.ContainsKey(t))) { tiers["implicit-lifecycle-only"]++; reached++; }
            else unreached.Add(new { declaringType = declaring, signature, matchedTo = targets.Select(t => t.FullName).ToList() });
        }
        int failures = unmatched.Count + unreached.Count;
        string sha = Sha256(tracePath);
        return new TraceReport
        {
            Path = RelativePath(stage, tracePath).StartsWith("..", StringComparison.Ordinal) ? tracePath : RelativePath(stage, tracePath),
            Called = called, Matched = matched, Reached = reached, Failures = failures,
            Json = new
            {
                path = RelativePath(stage, tracePath).StartsWith("..", StringComparison.Ordinal) ? tracePath : RelativePath(stage, tracePath),
                sha256 = sha,
                scenarioId = root.TryGetProperty("scenarioId", out JsonElement s) ? s.GetString() : null,
                renderMode = root.TryGetProperty("renderMode", out JsonElement r) ? r.GetString() : null,
                cairoAssemblySha256 = root.TryGetProperty("cairoAssemblySha256", out JsonElement c) ? c.GetString() : null,
                calledMembers = called,
                matchedToWrapperMembers = matched,
                reachableMembers = reached,
                reachabilityTiers = tiers.OrderBy(kv => kv.Key, Ord).Select(kv => new { tier = kv.Key, members = kv.Value }).ToList(),
                unmatchedMembers = unmatched,
                unreachableMembers = unreached,
                allCalledMembersReachable = failures == 0
            }
        };
    }

    private static List<MethodDefinition> MatchTraceMember(WrapperModel wrapper, Dictionary<string, Dictionary<string, List<MethodDefinition>>> cache, string declaringType, string signature)
    {
        if (!wrapper.Types.TryGetValue(declaringType.Replace('+', '/'), out TypeDefinition? type)) return [];
        Match accessorList = Regex.Match(signature, @"\[(?<names>[A-Za-z_][\w,]*)\]$");
        if (accessorList.Success && (signature.Contains(" [", StringComparison.Ordinal)))
        {
            string[] names = accessorList.Groups["names"].Value.Split(',');
            return type.Methods.Where(m => names.Contains(m.Name, Ord)).OrderBy(m => m.FullName, Ord).ToList();
        }
        if (signature.StartsWith("event ", StringComparison.Ordinal))
        {
            string name = signature[(signature.LastIndexOf(' ') + 1)..];
            return type.Methods.Where(m => m.Name == "add_" + name || m.Name == "remove_" + name).ToList();
        }
        if (!cache.TryGetValue(type.FullName, out Dictionary<string, List<MethodDefinition>>? signatures))
        {
            cache[type.FullName] = signatures = new(Ord);
            foreach (MethodDefinition m in type.Methods)
            {
                string key = TraceSignature(m);
                if (!signatures.TryGetValue(key, out List<MethodDefinition>? list)) signatures[key] = list = [];
                list.Add(m);
            }
        }
        return signatures.GetValueOrDefault(signature) ?? [];
    }

    // Mirrors Program.MemberSignature (reflection spelling) using Cecil metadata.
    private static string TraceSignature(MethodDefinition method)
    {
        static string TypeName(TypeReference t) => t.FullName.Replace('/', '+');
        string parameters = string.Join(", ", method.Parameters.Select(p =>
        {
            bool byRef = p.ParameterType is ByReferenceType;
            TypeReference core = p.ParameterType is ByReferenceType br ? br.ElementType : p.ParameterType;
            return (p.IsOut ? "out " : byRef ? "ref " : "") + TypeName(core) + (p.IsOptional ? " = optional" : "");
        }));
        if (method.IsConstructor) return $"{(method.IsStatic ? "static " : "")}{TypeName(method.DeclaringType)}({parameters})";
        string arity = method.HasGenericParameters ? $"`{method.GenericParameters.Count}" : "";
        return $"{TypeName(method.ReturnType)} {method.Name}{arity}({parameters})";
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
