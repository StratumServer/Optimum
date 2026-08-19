using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class PersistentUploadCoverageTests
{
    [Fact]
    public void PersistentUploadMethodsAreRegisteredWithExactSignatures()
    {
        string source = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));

        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"updateVAO\", 6", source);
        Assert.Contains("\"System.Single[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Int32[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Int16[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.UInt16[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"System.Byte[]\", \"System.Int32\", \"System.Int32\", \"System.Int32\", \"System.IntPtr\", \"System.Boolean\"", source);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"updateIndices\", 5", source);
        Assert.Contains("\"Vintagestory.Client.NoObf.VAO\", \"System.Boolean\"", source);
    }

    [Fact]
    public void PersistentUploadPatchCopiesAllVertexAndIndexBuffersInBlocks()
    {
        string patch = File.ReadAllText(FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch"));

        Assert.Equal(6, Count(patch, "Unsafe.CopyBlockUnaligned"));
        Assert.DoesNotContain("RecordPersistentMappedUpload", patch);
        Assert.DoesNotContain("RecordChunkUpload(byteCount", patch);
        Assert.Contains("byte* dest = (byte*)vao.indicesPtr + IndicesOffset;", patch);
        Assert.Contains("GL.BufferSubData<int>((BufferTarget)34963", patch);
    }

    [Fact]
    public void PersistentUploadDonorMatchesThePatchAndPreservesTheFallback()
    {
        string donor = File.ReadAllText(FindRepositoryFile(
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs"));

        Assert.Equal(6, Count(donor, "Unsafe.CopyBlockUnaligned"));
        Assert.DoesNotContain("RecordPersistentMappedUpload", donor);
        Assert.DoesNotContain("*(indicesPtr++) = Indices[i];", donor);
        Assert.Contains("GL.BufferSubData<int>((BufferTarget)34963", donor);
        Assert.Contains("vao.IndicesCount = IndicesCount;", donor);
    }

    [Fact]
    public void ChunkAndPersistentUploadCountersRemainSeparate()
    {
        string diagnostics = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Config/OptimumConfig.cs"));
        string pool = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Client/MeshPool/MeshDataPool.cs"));

        Assert.Contains("RecordChunkUpload(int bytes, long ticks)", diagnostics);
        Assert.Contains("RecordChunkUpload(uploadBytes, uploadTicks)", pool);
        Assert.DoesNotContain("RecordPersistentMappedUpload", diagnostics);
    }

    private static int Count(string source, string value)
    {
        return (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
