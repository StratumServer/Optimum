using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class ApiPatcherQueuedGuiContextTests
{
	[Fact]
	public void QueuedContextHandoffDoesNotWaitForGpuCertification()
	{
		string source = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));
		int start = source.IndexOf("internal static int PatchQueuedGuiContexts", System.StringComparison.Ordinal);
		int end = source.IndexOf("internal static int PatchClientApiThreadContract", start, System.StringComparison.Ordinal);
		Assert.True(start >= 0 && end > start, "Could not find the queued GUI context patch method.");

		string patch = source[start..end];
		Assert.DoesNotContain("RenderingRequested", patch);
		Assert.DoesNotContain("get_RenderingRequested", patch);
		Assert.Contains("queued GPU context handoff", patch);
		Assert.Contains("called.Name == \"EnqueueMainThreadTask\"", patch);
		Assert.Contains("Instruction.Create(OpCodes.Callvirt, dispose)", patch);
	}
}
