using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace Optimum.Launcher;

internal sealed record GuiDeviceIdentity(string Vendor, string Renderer, string Version, int Major, int Minor, int ProfileMask, int ContextFlags);
internal sealed record GuiDevicePreflightResult(int SchemaVersion, string Nonce, GuiDeviceIdentity Identity);

/// <summary>Runs native window-system probing in a disposable process before the game starts.</summary>
internal static class GuiDevicePreflight
{
    private const string ChildArgument = "--optimum-gui-device-preflight";
    private const int MaximumResultBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static GuiDeviceIdentity? Probe(int major, int minor)
    {
        if (major is < 3 or > 9 || minor is < 0 or > 9) return null;
        string nonce = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "optimum-gpu-preflight-" + nonce);
        string resultPath = Path.Combine(directory, "result.json");
        try
        {
            Directory.CreateDirectory(directory);
            var start = CreateChildStartInfo(nonce, resultPath, major, minor);
            using var process = Process.Start(start);
            if (process is null) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { process.WaitForExit(1000); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0 || !File.Exists(resultPath)) return null;
            byte[] bytes;
            using (var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is <= 0 or > MaximumResultBytes) return null;
                bytes = new byte[checked((int)stream.Length)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) return null;
                    offset += read;
                }
                if (stream.ReadByte() != -1) return null;
            }
            return ParseResult(bytes, nonce, major, minor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or JsonException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static GuiDeviceIdentity? ParseResult(byte[] bytes, string nonce, int major, int minor)
    {
        if (bytes.Length is <= 0 or > MaximumResultBytes) return null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(json.RootElement);
            var result = JsonSerializer.Deserialize<GuiDevicePreflightResult>(json.RootElement.GetRawText(), JsonOptions);
            if (result is null || result.SchemaVersion != 1 || result.Nonce != nonce || result.Identity is null ||
                !IsComplete(result.Identity) || result.Identity.Major < major ||
                result.Identity.Major == major && result.Identity.Minor < minor ||
                IsSoftwareRenderer(result.Identity.Renderer)) return null;
            return result.Identity;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    internal static int RunChild(string[] args)
    {
        if (args.Length != 5 || args[0] != ChildArgument || args[1].Length != 32 ||
            !int.TryParse(args[3], out int major) || !int.TryParse(args[4], out int minor)) return 2;
        string expectedDirectory = Path.Combine(Path.GetTempPath(), "optimum-gpu-preflight-" + args[1]);
        string expectedPath = Path.Combine(expectedDirectory, "result.json");
        if (!string.Equals(Path.GetFullPath(args[2]), expectedPath, PathComparison)) return 2;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                API = ContextAPI.OpenGL,
                APIVersion = new Version(major, minor),
                ClientSize = new Vector2i(1, 1),
                Title = "Optimum GPU preflight",
                StartVisible = false,
                StartFocused = false,
                Flags = OperatingSystem.IsMacOS() ? (ContextFlags)2 : ContextFlags.Default,
                AutoLoadBindings = true
            });
            window.MakeCurrent();
            string vendor = GL.GetString(StringName.Vendor) ?? "";
            string renderer = GL.GetString(StringName.Renderer) ?? "";
            string version = GL.GetString(StringName.Version) ?? "";
            GL.GetInteger(GetPName.MajorVersion, out int actualMajor);
            GL.GetInteger(GetPName.MinorVersion, out int actualMinor);
            GL.GetInteger(GetPName.ContextProfileMask, out int profileMask);
            GL.GetInteger(GetPName.ContextFlags, out int contextFlags);
            var identity = new GuiDeviceIdentity(vendor, renderer, version, actualMajor, actualMinor, profileMask, contextFlags);
            if (!IsComplete(identity) || actualMajor < major ||
                actualMajor == major && actualMinor < minor || IsSoftwareRenderer(renderer)) return 3;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new GuiDevicePreflightResult(1, args[1], identity));
            File.WriteAllBytes(expectedPath, bytes);
            return 0;
        }
        catch
        {
            return 4;
        }
    }

    private static ProcessStartInfo CreateChildStartInfo(string nonce, string path, int major, int minor)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Launcher assembly path is unavailable."));
        }
        else start.FileName = processPath;
        start.ArgumentList.Add(ChildArgument);
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(major.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(minor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }

    private static bool IsComplete(GuiDeviceIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Vendor) && !string.Equals(identity.Vendor, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(identity.Renderer) && !string.Equals(identity.Renderer, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(identity.Version) && !string.Equals(identity.Version, "unknown", StringComparison.OrdinalIgnoreCase) &&
        identity.Major >= 3 && identity.Minor >= 0;

    private static bool IsSoftwareRenderer(string renderer) =>
        renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("software", StringComparison.OrdinalIgnoreCase) ||
        renderer.Contains("swiftshader", StringComparison.OrdinalIgnoreCase);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate preflight property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
