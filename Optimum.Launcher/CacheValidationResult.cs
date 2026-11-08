using System;

namespace Optimum.Launcher;

/// <summary>
/// Status codes representing the outcome of a cache validation check.
/// </summary>
public enum CacheValidationStatus
{
    Valid,
    ManifestMissing,
    ManifestCorrupt,
    VersionMismatch,
    RequiredAssemblyMissing,
    VanillaAssemblyMissing,
    CachedAssemblyMissing,
    CachedAssemblyCorrupted,
    ApiContractInvalid,
    DonorMissing,
    DonorHashMismatch,
    VanillaHashMismatch
}

/// <summary>
/// Detailed diagnostic result of a cache validation check.
/// </summary>
public sealed class CacheValidationResult
{
    public CacheValidationStatus Status { get; }
    public string? TargetAssembly { get; }
    public string? Detail { get; }
    public CacheManifest? Manifest { get; }

    public bool IsValid => Status == CacheValidationStatus.Valid && Manifest is not null;

    public CacheValidationResult(
        CacheValidationStatus status,
        string? targetAssembly = null,
        string? detail = null,
        CacheManifest? manifest = null)
    {
        Status = status;
        TargetAssembly = targetAssembly;
        Detail = detail;
        Manifest = manifest;
    }

    public static CacheValidationResult Success(CacheManifest manifest) =>
        new(CacheValidationStatus.Valid, manifest: manifest);

    public static CacheValidationResult Failure(
        CacheValidationStatus status,
        string? targetAssembly = null,
        string? detail = null) =>
        new(status, targetAssembly, detail);

    public static implicit operator CacheManifest?(CacheValidationResult? result) =>
        result?.Manifest;

    public override string ToString()
    {
        if (IsValid)
        {
            return "Cache valid";
        }

        string target = !string.IsNullOrEmpty(TargetAssembly) ? $" [{TargetAssembly}]" : string.Empty;
        string detail = !string.IsNullOrEmpty(Detail) ? $": {Detail}" : string.Empty;
        return $"{Status}{target}{detail}";
    }
}
