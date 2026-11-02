using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cairo.ParityHarness;

internal static class NativeImportUsageManifest
{
    private sealed class ImportUsage(MethodDefinition method)
    {
        public MethodDefinition Method { get; } = method;
        public List<object> CallSites { get; } = [];
    }

    public static void Write(string outputPath, string cairoAssemblyPath, IEnumerable<string> assemblyDirectories)
    {
        string cairoPath = System.IO.Path.GetFullPath(cairoAssemblyPath);
        using AssemblyDefinition cairoAssembly = AssemblyDefinition.ReadAssembly(cairoPath);
        var imports = cairoAssembly.MainModule.Types
            .SelectMany(GetTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.IsPInvokeImpl)
            .Select(method => new ImportUsage(method))
            .ToDictionary(usage => usage.Method.FullName, StringComparer.Ordinal);

        var assemblyFiles = new SortedSet<string>(StringComparer.Ordinal) { cairoPath };
        foreach (string directory in assemblyDirectories.Select(System.IO.Path.GetFullPath))
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Assembly directory does not exist: {directory}");
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
                assemblyFiles.Add(System.IO.Path.GetFullPath(path));
        }

        var scannedAssemblies = new List<object>();
        int skippedFiles = 0;
        foreach (string assemblyPath in assemblyFiles)
        {
            try
            {
                using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                    assemblyPath,
                    new ReaderParameters { InMemory = true, ReadSymbols = false });
                foreach (TypeDefinition type in assembly.MainModule.Types.SelectMany(GetTypes))
                foreach (MethodDefinition caller in type.Methods.Where(method => method.HasBody))
                foreach (Instruction instruction in caller.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference reference ||
                        !imports.TryGetValue(reference.FullName, out ImportUsage? usage))
                        continue;

                    usage.CallSites.Add(new
                    {
                        assembly = System.IO.Path.GetFileName(assemblyPath),
                        declaringType = caller.DeclaringType.FullName,
                        method = caller.FullName,
                        opcode = instruction.OpCode.Code.ToString(),
                        offset = instruction.Offset
                    });
                }

                scannedAssemblies.Add(new
                {
                    name = System.IO.Path.GetFileName(assemblyPath),
                    identity = assembly.Name.FullName,
                    sha256 = Sha256(assemblyPath)
                });
            }
            catch (BadImageFormatException)
            {
                skippedFiles++;
            }
        }

        var rows = imports.Values
            .OrderBy(usage => usage.Method.PInvokeInfo?.Module.Name, StringComparer.Ordinal)
            .ThenBy(usage => usage.Method.PInvokeInfo?.EntryPoint, StringComparer.Ordinal)
            .ThenBy(usage => usage.Method.FullName, StringComparer.Ordinal)
            .Select(usage => new
            {
                declaringType = usage.Method.DeclaringType.FullName,
                method = usage.Method.FullName,
                library = usage.Method.PInvokeInfo?.Module.Name,
                entryPoint = usage.Method.PInvokeInfo?.EntryPoint,
                directIlCallSiteCount = usage.CallSites.Count,
                callSites = usage.CallSites
            })
            .ToArray();

        string? outputDirectory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            analysis = "direct-IL-callsite-scan",
            limitations = new[]
            {
                "Does not detect reflection, dynamically generated IL, native callbacks, or external mod calls.",
                "An import without a direct call site is only a removal candidate, not proof of global unreachability."
            },
            targetAssembly = new
            {
                name = System.IO.Path.GetFileName(cairoPath),
                identity = cairoAssembly.Name.FullName,
                sha256 = Sha256(cairoPath)
            },
            scannedAssemblyCount = scannedAssemblies.Count,
            skippedNonManagedFileCount = skippedFiles,
            scannedAssemblies,
            importMethodCount = rows.Length,
            directlyReferencedMethodCount = rows.Count(row => row.directIlCallSiteCount > 0),
            noDirectIlReferenceMethodCount = rows.Count(row => row.directIlCallSiteCount == 0),
            imports = rows
        }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Wrote native import callsite inventory: {rows.Length} declarations, " +
            $"{rows.Count(row => row.directIlCallSiteCount > 0)} referenced, " +
            $"{rows.Count(row => row.directIlCallSiteCount == 0)} without direct IL references, " +
            $"{scannedAssemblies.Count} assemblies scanned.");
    }

    private static IEnumerable<TypeDefinition> GetTypes(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(GetTypes));

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
