using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Xunit;

/// <summary>
/// Compares the enum, constant and value-struct surface of the Optimum Cairo wrapper with the
/// original game <c>cairo-sharp.dll</c> (unpatched, loaded in an isolated load context so it can
/// coexist with the wrapper under the same assembly name). The stored historical manifest in
/// <c>tools/Cairo.ParityHarness/cairo-api-manifest.json</c> is the row denominator: for every enum
/// and value-struct type the wrapper's reflected rows must match the manifest. Original enum
/// rows must remain present; Cairo.Status may append upstream Cairo status constants that are
/// missing from the game's older binding. Underlying types, constant values, field
/// order, struct layout, field offsets and sizes.
///
/// A missing original assembly is a failed run, not a skip. The original is located through
/// CAIRO_REFERENCE_PATH, VINTAGE_STORY, the repository's .vanilla stages and the default Linux
/// install; any candidate that references SkiaSharp (that is, an Optimum wrapper) is rejected.
/// </summary>
public sealed class CairoAbiMetadataTests
{
	const string ManifestRelativePath = "tools/Cairo.ParityHarness/cairo-api-manifest.json";

	sealed class OriginalLoadContext : AssemblyLoadContext
	{
		public OriginalLoadContext () : base ("cairo-original-abi", isCollectible: false) { }
		protected override Assembly Load (AssemblyName assemblyName) => null; // dependencies come from the default context
	}

	static readonly Lazy<(Assembly Assembly, string Path)> original = new (LoadOriginal);
	static readonly Lazy<JsonDocument> manifest = new (() => JsonDocument.Parse (File.ReadAllText (FindManifestPath ())));
	static Assembly Wrapper => typeof (Cairo.Antialias).Assembly;

	// ---------------------------------------------------------------- discovery

	static string FindRepositoryRoot ()
	{
		for (var dir = new DirectoryInfo (AppContext.BaseDirectory); dir != null; dir = dir.Parent)
			if (File.Exists (System.IO.Path.Combine (dir.FullName, ManifestRelativePath))) return dir.FullName;
		throw new InvalidOperationException ("Cannot locate " + ManifestRelativePath + " above " + AppContext.BaseDirectory);
	}

	static string FindManifestPath () => System.IO.Path.Combine (FindRepositoryRoot (), ManifestRelativePath);

	// The game's own cairo-sharp.dll already references SkiaSharp, so that cannot tell the original from the Optimum
	// wrapper. The wrapper defines the Optimum recording types, which the game binary does not contain.
	static bool IsOptimumWrapper (string path)
	{
		using var stream = File.OpenRead (path);
		using var pe = new PEReader (stream);
		var reader = pe.GetMetadataReader ();
		return reader.TypeDefinitions.Select (h => reader.GetString (reader.GetTypeDefinition (h).Name))
			.Any (n => n is "SurfaceRecorder" or "SurfaceRecordingState" or "CairoNativeGuard");
	}

	static (Assembly, string) LoadOriginal ()
	{
		var candidates = new List<string> ();
		void Add (string dir, params string[] relative)
		{
			if (string.IsNullOrEmpty (dir)) return;
			candidates.Add (System.IO.Path.Combine (new[] { dir }.Concat (relative).ToArray ()));
		}
		var explicitPath = Environment.GetEnvironmentVariable ("CAIRO_REFERENCE_PATH");
		if (!string.IsNullOrEmpty (explicitPath)) candidates.Add (explicitPath);
		Add (Environment.GetEnvironmentVariable ("VINTAGE_STORY"), "Lib", "cairo-sharp.dll");
		var root = FindRepositoryRoot ();
		foreach (var platform in new[] { "linux-x64", "win-x64", "osx-arm64", "osx-x64" })
			Add (root, ".vanilla", platform, "vintagestory", "Lib", "cairo-sharp.dll");
		Add (Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "vintagestory", "Lib", "cairo-sharp.dll");

		var rejected = new List<string> ();
		foreach (var path in candidates.Distinct ()) {
			if (!File.Exists (path)) continue;
			if (IsOptimumWrapper (path)) { rejected.Add (path); continue; }
			var assembly = new OriginalLoadContext ().LoadFromAssemblyPath (System.IO.Path.GetFullPath (path));
			return (assembly, path);
		}
		throw new InvalidOperationException (
			"The original game cairo-sharp.dll was not found. Set CAIRO_REFERENCE_PATH to it or bootstrap .vanilla. Tried: "
			+ string.Join ("; ", candidates.Distinct ()) + (rejected.Count > 0 ? ". Rejected as Optimum wrappers (define Optimum recorder types): " + string.Join ("; ", rejected) : ""));
	}

	// ---------------------------------------------------------------- manifest

	record ManifestType (string Name, string Kind, string BaseType, (string Name, long Value)[] EnumValues);

	static IReadOnlyList<ManifestType> ManifestTypes ()
	{
		var list = new List<ManifestType> ();
		foreach (var t in manifest.Value.RootElement.GetProperty ("types").EnumerateArray ()) {
			var values = t.GetProperty ("enumValues").ValueKind == JsonValueKind.Array
				? t.GetProperty ("enumValues").EnumerateArray ().Select (v => (v.GetProperty ("name").GetString (), v.GetProperty ("value").GetInt64 ())).ToArray ()
				: Array.Empty<(string, long)> ();
			list.Add (new ManifestType (t.GetProperty ("name").GetString (), t.GetProperty ("kind").GetString (), t.GetProperty ("baseType").GetString (), values));
		}
		return list;
	}

	static IReadOnlyList<string> ManifestSignatures (string typeName) =>
		manifest.Value.RootElement.GetProperty ("members").EnumerateArray ()
			.Where (m => m.GetProperty ("declaringType").GetString () == typeName)
			.Select (m => m.GetProperty ("signature").GetString ()).ToArray ();

	public static IEnumerable<object[]> EnumTypeNames () => ManifestTypes ().Where (t => t.Kind == "enum").Select (t => new object[] { t.Name });
	public static IEnumerable<object[]> StructTypeNames () => ManifestTypes ().Where (t => t.Kind == "struct").Select (t => new object[] { t.Name });

	// ---------------------------------------------------------------- signatures (same grammar as Cairo.ParityHarness --api-manifest)

	const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

	static bool IsPublicOrProtected (MemberInfo member) => member switch {
		MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
		FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
		PropertyInfo p => p.GetAccessors (true).Any (a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
		EventInfo e => new[] { e.AddMethod, e.RemoveMethod, e.RaiseMethod }.Where (m => m != null).Any (m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly),
		_ => false
	};

	static string ParameterSignature (ParameterInfo p) =>
		$"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{(p.ParameterType.IsByRef ? p.ParameterType.GetElementType () : p.ParameterType)?.FullName}{(p.IsOptional ? " = optional" : "")}";

	static string MemberSignature (MemberInfo member) => member switch {
		ConstructorInfo c => $"{(c.IsStatic ? "static " : "")}{c.DeclaringType?.FullName}({string.Join (", ", c.GetParameters ().Select (ParameterSignature))})",
		MethodInfo m => $"{m.ReturnType.FullName} {m.Name}{(m.IsGenericMethodDefinition ? "`" + m.GetGenericArguments ().Length : "")}({string.Join (", ", m.GetParameters ().Select (ParameterSignature))})",
		PropertyInfo p => $"{p.PropertyType.FullName} {p.Name}{(p.GetIndexParameters ().Length == 0 ? "" : "[" + string.Join (", ", p.GetIndexParameters ().Select (ParameterSignature)) + "]")} [{string.Join (",", p.GetAccessors (true).Where (IsPublicOrProtected).Select (a => a.Name))}]",
		EventInfo e => $"event {e.EventHandlerType?.FullName} {e.Name}",
		FieldInfo f => $"{(f.IsLiteral ? "const " : "field ")}{f.FieldType.FullName} {f.Name}{(f.IsLiteral ? " = " + f.GetRawConstantValue () : "")}",
		_ => $"{member.MemberType} {member.Name}"
	};

	static string[] PublicRows (Type type) =>
		type.GetMembers (AllDeclared).Where (IsPublicOrProtected).Select (MemberSignature).OrderBy (s => s, StringComparer.Ordinal).ToArray ();

	static Type RequireType (Assembly assembly, string name) =>
		assembly.GetType (name, throwOnError: false) ?? throw new Xunit.Sdk.XunitException ($"{name} is missing from {assembly.GetName ()}");

	// ---------------------------------------------------------------- inventory

	[Fact]
	public void OriginalAssemblyIsTheUnpatchedGameBinary ()
	{
		var (assembly, path) = original.Value;
		Assert.False (IsOptimumWrapper (path), path);
		Assert.NotEqual (Wrapper.Location, assembly.Location);
		Assert.Equal (manifest.Value.RootElement.GetProperty ("assemblyIdentity").GetString (), assembly.GetName ().FullName);
		Assert.Equal (assembly.GetName ().FullName, Wrapper.GetName ().FullName);
		Assert.Equal (typeof (Cairo.Antialias).Assembly, Wrapper);
	}

	[Fact]
	public void EnumAndValueStructTypeInventoryMatchesTheManifestForOriginalAndWrapper ()
	{
		var expected = ManifestTypes ().Where (t => t.Kind is "enum" or "struct")
			.Select (t => $"{t.Kind} {t.Name} : {t.BaseType}").OrderBy (s => s, StringComparer.Ordinal).ToArray ();
		string Describe (Type t) => $"{(t.IsEnum ? "enum" : "struct")} {t.FullName} : {t.BaseType?.FullName}";
		foreach (var assembly in new[] { original.Value.Assembly, Wrapper }) {
			var actual = assembly.GetExportedTypes ().Where (t => t.IsEnum || t.IsValueType)
				.Select (Describe).OrderBy (s => s, StringComparer.Ordinal).ToArray ();
			Assert.Equal (expected, actual);
		}
		Assert.Equal (23, ManifestTypes ().Count (t => t.Kind == "enum"));
		Assert.Equal (11, ManifestTypes ().Count (t => t.Kind == "struct"));
	}

	[Fact]
	public void ManifestEnumAndValueStructRowCountsAreStable ()
	{
		var kinds = ManifestTypes ().ToDictionary (t => t.Name, t => t.Kind);
		var rows = manifest.Value.RootElement.GetProperty ("members").EnumerateArray ()
			.Select (m => kinds[m.GetProperty ("declaringType").GetString ()]).ToArray ();
		Assert.Equal (184, rows.Count (k => k == "enum"));   // 161 const + 23 value__
		Assert.Equal (116, rows.Count (k => k == "struct"));
	}

	// ---------------------------------------------------------------- enums

	static string DescribeEnum (Type type)
	{
		var sb = new StringBuilder ();
		sb.Append ($"visibility={(type.IsPublic ? "public" : type.IsNestedPublic ? "nestedPublic" : "other")} sealed={type.IsSealed} flags={type.IsDefined (typeof (FlagsAttribute), false)}");
		sb.Append ($" underlying={Enum.GetUnderlyingType (type).FullName} attrs={type.Attributes}");
		sb.Append (" interfaces=" + string.Join (",", type.GetInterfaces ().Select (i => i.FullName).OrderBy (s => s, StringComparer.Ordinal)));
		foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy (f => f.MetadataToken))
			sb.Append ($"\n  {(f.IsStatic ? "static" : "instance")} {f.FieldType.FullName} {f.Name} {f.Attributes} = {(f.IsLiteral ? Convert.ToString (f.GetRawConstantValue (), CultureInfo.InvariantCulture) : "-")}");
		return sb.ToString ();
	}

	[Theory]
	[MemberData (nameof (EnumTypeNames))]
	public void EnumMetadataMatchesOriginalAndManifest (string typeName)
	{
		var manifestType = ManifestTypes ().Single (t => t.Name == typeName);
		var originalType = RequireType (original.Value.Assembly, typeName);
		var wrapperType = RequireType (Wrapper, typeName);
		Assert.True (originalType.IsEnum && wrapperType.IsEnum);

		// Layout of the enum: underlying type, flags, attributes, declaration order and numeric values.
		if (typeName == "Cairo.Status")
			Assert.StartsWith (DescribeEnum (originalType), DescribeEnum (wrapperType));
		else
			Assert.Equal (DescribeEnum (originalType), DescribeEnum (wrapperType));

		// Names, order and numeric values against the manifest's enumValues (Enum.GetNames order).
		foreach (var type in new[] { originalType, wrapperType }) {
			var underlying = Enum.GetUnderlyingType (type);
			var actual = Enum.GetNames (type).Select (n => (n, Convert.ToInt64 (Enum.Parse (type, n)))).ToArray ();
			if (type == originalType && typeName == "Cairo.Status")
				Assert.Equal (actual, manifestType.EnumValues.Take (actual.Length));
			else
				Assert.Equal (manifestType.EnumValues, actual);
			// Literal values keep the underlying type (no silent widening of the constant).
			foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.Static))
				Assert.Equal (underlying, f.GetRawConstantValue ().GetType ());
		}

		// Row-for-row public surface: every const and value__ row of the manifest.
		var expectedRows = ManifestSignatures (typeName).OrderBy (s => s, StringComparer.Ordinal).ToArray ();
		Assert.NotEmpty (expectedRows);
		if (typeName == "Cairo.Status")
			Assert.All (PublicRows (originalType), row => Assert.Contains (row, expectedRows));
		else
			Assert.Equal (expectedRows, PublicRows (originalType));
		Assert.Equal (expectedRows, PublicRows (wrapperType));
	}

	// ---------------------------------------------------------------- struct layout

	static string DescribeLayout (Type type)
	{
		var sb = new StringBuilder ();
		var layout = type.StructLayoutAttribute;
		sb.Append ($"visibility={(type.IsPublic ? "public" : "other")} sealed={type.IsSealed} attrs={type.Attributes}");
		sb.Append ($" layoutKind={layout?.Value} pack={layout?.Pack} size={layout?.Size} charSet={layout?.CharSet}");
		sb.Append ($" readonly={type.IsDefined (typeof (System.Runtime.CompilerServices.IsReadOnlyAttribute), false)} byref={type.IsByRefLike}");
		sb.Append (" interfaces=" + string.Join (",", type.GetInterfaces ().Select (i => i.FullName).OrderBy (s => s, StringComparer.Ordinal)));
		sb.Append ($" marshalSize={TryMarshal (() => Marshal.SizeOf (type))} unsafeSize={UnsafeSizeOf (type)}");
		foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy (f => f.MetadataToken)) {
			string offset = f.IsStatic ? "-" : TryMarshal (() => Marshal.OffsetOf (type, f.Name).ToInt64 ().ToString (CultureInfo.InvariantCulture));
			sb.Append ($"\n  {(f.IsStatic ? "static" : "instance")} {f.FieldType.FullName} {f.Name} {f.Attributes} offset={offset}");
		}
		return sb.ToString ();
	}

	static string TryMarshal (Func<object> f)
	{
		try { return Convert.ToString (f (), CultureInfo.InvariantCulture); }
		catch (Exception e) { return "!" + e.GetType ().Name; }
	}

	static int UnsafeSizeOf (Type type) =>
		(int)typeof (Unsafe).GetMethod (nameof (Unsafe.SizeOf)).MakeGenericMethod (type).Invoke (null, null);

	[Theory]
	[MemberData (nameof (StructTypeNames))]
	public void ValueStructLayoutAndSurfaceMatchOriginalAndManifest (string typeName)
	{
		var originalType = RequireType (original.Value.Assembly, typeName);
		var wrapperType = RequireType (Wrapper, typeName);
		Assert.True (originalType.IsValueType && !originalType.IsEnum);
		Assert.True (wrapperType.IsValueType && !wrapperType.IsEnum);

		// StructLayout kind/pack/size, field order, field types, field offsets and sizes (including private backing fields).
		Assert.Equal (DescribeLayout (originalType), DescribeLayout (wrapperType));

		// Row-for-row public/protected surface against the manifest.
		var expectedRows = ManifestSignatures (typeName).OrderBy (s => s, StringComparer.Ordinal).ToArray ();
		Assert.NotEmpty (expectedRows);
		Assert.Equal (expectedRows, PublicRows (originalType));
		Assert.Equal (expectedRows, PublicRows (wrapperType));
	}

	// ---------------------------------------------------------------- struct behavior (managed-only members)

	static string Format (object value, int depth = 0)
	{
		switch (value) {
		case null: return "null";
		case double d: return d.ToString ("R", CultureInfo.InvariantCulture);
		case float f: return f.ToString ("R", CultureInfo.InvariantCulture);
		case bool or int or long or string: return Convert.ToString (value, CultureInfo.InvariantCulture);
		}
		var type = value.GetType ();
		if (type.IsEnum) return type.Name + "." + value;
		var sb = new StringBuilder (type.Name + "{");
		foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderBy (f => f.MetadataToken))
			sb.Append ($"{f.Name}={(depth > 4 ? "…" : Format (f.GetValue (value), depth + 1))};");
		return sb.Append ('}').ToString ();
	}

	static object[] SampleArguments (Assembly assembly, ParameterInfo[] parameters, ref int seed)
	{
		var args = new object[parameters.Length];
		for (int i = 0; i < args.Length; i++) args[i] = Sample (assembly, parameters[i].ParameterType, ref seed);
		return args;
	}

	static object Sample (Assembly assembly, Type type, ref int seed)
	{
		seed++;
		if (type == typeof (double)) return seed * 1.25 + 0.0625;
		if (type == typeof (float)) return seed * 1.5f;
		if (type == typeof (int)) return seed * 7 + 1;
		if (type == typeof (long)) return seed * 1000L + 3;
		if (type == typeof (bool)) return seed % 2 == 0;
		if (type == typeof (string)) return "s" + seed;
		if (type.IsEnum) return Enum.GetValues (type).GetValue (seed % Enum.GetValues (type).Length);
		if (type.IsValueType) {
			var ctor = type.GetConstructors (BindingFlags.Public | BindingFlags.Instance).OrderByDescending (c => c.GetParameters ().Length).FirstOrDefault ();
			var instance = ctor == null || ctor.GetParameters ().Length == 0
				? Activator.CreateInstance (type)
				: ctor.Invoke (SampleArguments (assembly, ctor.GetParameters (), ref seed));
			foreach (var p in type.GetProperties (BindingFlags.Public | BindingFlags.Instance).Where (p => p.SetMethod?.IsPublic == true))
				p.SetValue (instance, Sample (assembly, p.PropertyType, ref seed));
			foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.Instance))
				f.SetValue (instance, Sample (assembly, f.FieldType, ref seed));
			return instance;
		}
		throw new NotSupportedException ($"No sample for {type}");
	}

	static string Try (Func<object> action)
	{
		try { return Format (action ()); }
		catch (TargetInvocationException e) { return "!" + e.InnerException.GetType ().Name; }
		catch (Exception e) { return "!" + e.GetType ().Name; }
	}

	/// <summary>Exercises every public constructor, property accessor, Equals, GetHashCode, ToString and operator.</summary>
	static List<string> Exercise (Assembly assembly, string typeName)
	{
		var type = RequireType (assembly, typeName);
		var results = new List<string> ();
		int seed = 0;
		object @default = Activator.CreateInstance (type);
		results.Add ("default => " + Format (@default));

		foreach (var ctor in type.GetConstructors (BindingFlags.Public | BindingFlags.Instance).OrderBy (c => MemberSignature (c), StringComparer.Ordinal)) {
			int s = 10;
			var args = ctor.GetParameters ().Select (p => Sample (assembly, p.ParameterType, ref s)).ToArray ();
			results.Add ($"{MemberSignature (ctor)} => " + Try (() => ctor.Invoke (args)));
		}

		var a = Sample (assembly, type, ref seed);
		var b = Sample (assembly, type, ref seed);
		var copy = RuntimeHelpers.GetUninitializedObject (type);
		foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) f.SetValue (copy, f.GetValue (a));
		results.Add ("sample a => " + Format (a));
		results.Add ("sample b => " + Format (b));

		foreach (var p in type.GetProperties (BindingFlags.Public | BindingFlags.Instance).OrderBy (p => p.Name, StringComparer.Ordinal)) {
			results.Add ($"get {p.Name} a => " + Try (() => p.GetValue (a)));
			if (p.SetMethod?.IsPublic == true) {
				int s = 500;
				var value = Sample (assembly, p.PropertyType, ref s);
				var boxed = RuntimeHelpers.GetUninitializedObject (type);
				foreach (var f in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) f.SetValue (boxed, f.GetValue (a));
				results.Add ($"set {p.Name} => " + Try (() => { p.SetValue (boxed, value); return boxed; }));
			}
		}

		// Object overrides only where the type declares them (an inherited ValueType override is covered by the framework).
		var equals = type.GetMethod ("Equals", new[] { typeof (object) });
		var hash = type.GetMethod ("GetHashCode", Type.EmptyTypes);
		var toString = type.GetMethod ("ToString", Type.EmptyTypes);
		results.Add ($"declares Equals={equals.DeclaringType == type} GetHashCode={hash.DeclaringType == type} ToString={toString.DeclaringType == type}");
		foreach (var (label, left, right) in new[] { ("a,copy", a, copy), ("a,b", a, b), ("default,default", @default, Activator.CreateInstance (type)), ("a,default", a, @default) }) {
			results.Add ($"Equals {label} => " + Try (() => equals.Invoke (left, new[] { right })));
			results.Add ($"hash-equal {label} => " + Try (() => (int)hash.Invoke (left, null) == (int)hash.Invoke (right, null)));
		}
		results.Add ("Equals null => " + Try (() => equals.Invoke (a, new object[] { null })));
		results.Add ("Equals string => " + Try (() => equals.Invoke (a, new object[] { "x" })));
		// Hash values themselves, if the original defines its own, must agree numerically.
		if (hash.DeclaringType == type) {
			results.Add ("hash a => " + Try (() => hash.Invoke (a, null)));
			results.Add ("hash b => " + Try (() => hash.Invoke (b, null)));
			results.Add ("hash default => " + Try (() => hash.Invoke (@default, null)));
		}
		if (toString.DeclaringType == type) {
			results.Add ("ToString a => " + Try (() => toString.Invoke (a, null)));
			results.Add ("ToString default => " + Try (() => toString.Invoke (@default, null)));
		}
		foreach (var op in type.GetMethods (BindingFlags.Public | BindingFlags.Static).Where (m => m.IsSpecialName && m.Name.StartsWith ("op_", StringComparison.Ordinal)).OrderBy (m => MemberSignature (m), StringComparer.Ordinal)) {
			if (op.GetParameters ().Length != 2 || op.GetParameters ().Any (p => p.ParameterType != type)) { results.Add ($"{MemberSignature (op)} => unexercised"); continue; }
			foreach (var (label, left, right) in new[] { ("a,copy", a, copy), ("a,b", a, b), ("default,default", @default, @default) })
				results.Add ($"{op.Name} {label} => " + Try (() => op.Invoke (null, new[] { left, right })));
		}
		return results;
	}

	[Theory]
	[MemberData (nameof (StructTypeNames))]
	public void ValueStructManagedMembersBehaveLikeTheOriginal (string typeName)
	{
		var expected = Exercise (original.Value.Assembly, typeName);
		var actual = Exercise (Wrapper, typeName);
		Assert.True (expected.Count > 5, "behavior probe produced too few observations for " + typeName);
		Assert.Equal (expected, actual);
	}
}
