using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The per-method attribution that ModPatcherManifestConsistencyTests and
/// TaaRuntimeDonorCoverageTests rest on. Both used to compare whole files, so a
/// patch that changed one method of a type vouched for every other method of it.
/// These fixtures pin the discrimination itself: the same marker, in the same
/// file, in the wrong method, must not read as covered.
/// </summary>
public sealed class PatchMethodScopesTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("optimum-scopes").FullName;

    private const string Source = """
namespace Demo;

public class Widget
{
	private int count;

	public Widget(int start)
	{
		count = start;
		OptimumStandardMotion.Apply("ctor");
	}

	public void Draw(float dt)
	{
		if (count > 0)
		{
			OptimumMotionWrite.Begin();
		}
		Console.WriteLine("drawing the widget now");
	}

	public void Tick(float dt)
	{
		count++;
		Console.WriteLine("ticking the widget now");
	}
}
""";

    private const string Patch = """
diff --git a/Demo/Widget.cs b/Demo/Widget.cs
--- a/Demo/Widget.cs
+++ b/Demo/Widget.cs
@@ -14,6 +14,10 @@ public class Widget
 	public void Draw(float dt)
 	{
+		if (count > 0)
+		{
+			OptimumMotionWrite.Begin();
+		}
 		Console.WriteLine("drawing the widget now");
 	}
""";

    [Fact]
    public void AddedLinesAreAttributedToTheMethodThatEnclosesThem()
    {
        var touched = PatchMethodScopes.MethodsTouched(Write("widget.patch", Patch), Source);

        Assert.Contains("Draw", touched);
        // The whole-file check these tests replaced would have accepted Tick as
        // covered too, because the patch and the file share a type.
        Assert.DoesNotContain("Tick", touched);
        Assert.DoesNotContain("Widget", touched);
    }

    [Fact]
    public void MarkersAreKeyedByTheirEnclosingMethod()
    {
        var byMethod = PatchMethodScopes.MarkersByMethod(
            Write("markers.patch", Patch), Source, new[] { "OptimumMotionWrite.Begin", "OptimumStandardMotion.Apply" });

        Assert.Equal(new[] { "Draw" }, byMethod.Keys);
        Assert.Equal(new[] { "OptimumMotionWrite.Begin" }, byMethod["Draw"]);
    }

    /// <summary>
    /// The failure this guards: the same marker present in the file, but in the
    /// constructor instead of the draw call, so Cecil's per-method transplant
    /// still ships a vanilla Draw.
    /// </summary>
    [Fact]
    public void AMarkerInTheWrongMethodIsNotCoverageForTheRightOne()
    {
        // Same patch, but a tree whose only OptimumMotionWrite.Begin sits in the
        // constructor: the file-wide marker set is identical, the per-method one
        // is not.
        string misplaced = """
namespace Demo;

public class Widget
{
	private int count;

	public Widget(int start)
	{
		count = start;
		OptimumMotionWrite.Begin();
	}

	public void Draw(float dt)
	{
		Console.WriteLine("drawing the widget now");
	}
}
""";

        var byMethod = PatchMethodScopes.MarkersByMethod(
            Write("misplaced.patch", Patch), misplaced, new[] { "OptimumMotionWrite.Begin" });

        Assert.Equal(new[] { "Widget" }, byMethod.Keys);
        Assert.DoesNotContain("Draw", byMethod.Keys);
    }

    [Fact]
    public void BracesInsideCommentsAndStringsDoNotShiftTheNesting()
    {
        string source = """
namespace Demo;

public class Widget
{
	public void Draw(float dt)
	{
		// a stray } brace in a comment
		Console.WriteLine("a stray } brace in a string");
		OptimumMotionWrite.Begin();
	}

	public void Tick(float dt)
	{
		Console.WriteLine("ticking the widget now");
	}
}
""";
        var scopes = PatchMethodScopes.Parse(source);
        int at = source.IndexOf("OptimumMotionWrite.Begin", StringComparison.Ordinal);

        Assert.Equal("Draw", PatchMethodScopes.MethodAt(scopes, at));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
