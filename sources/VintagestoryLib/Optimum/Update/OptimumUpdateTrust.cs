using System;
using System.Security.Cryptography;

namespace Optimum.Update;

/// <summary>
/// The trust root of the one-click update: the public half of the key that signs
/// release manifests.
///
/// A hash in a manifest proves the archive is the one the manifest names. It does
/// not prove the manifest is Optimum's, because both are published from the same
/// place. The detached signature closes that gap: it is made in the release
/// workflow with a private key that never leaves CI, and checked here against a
/// key compiled into the game.
///
/// While <see cref="ManifestPublicKey"/> is empty no signature can verify, so the
/// update button stays off and the main menu keeps linking to the release page.
/// </summary>
public static class OptimumUpdateTrust
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the ECDSA P-256 release key. Generate it with
    /// scripts/generate-release-signing-key.sh and paste the printed value here.
    /// </summary>
    public const string ManifestPublicKey = "";

    /// <summary>Longest detached signature accepted: a DER ECDSA P-256 signature is at most 72 bytes.</summary>
    public const int MaxSignatureBytes = 128;

    public static bool IsConfigured => ManifestPublicKey.Length > 0;
}

/// <summary>Checks a detached ECDSA P-256 / SHA-256 signature over the exact manifest bytes.</summary>
public static class ManifestSignature
{
    /// <summary>
    /// True only when <paramref name="signatureDer"/> is a valid DER ECDSA signature of
    /// <paramref name="manifest"/> under <paramref name="publicKeySpkiBase64"/>, and that key is a P-256 key.
    /// Never throws: a malformed key or signature is a failed check.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signatureDer, string publicKeySpkiBase64)
    {
        if (string.IsNullOrEmpty(publicKeySpkiBase64)) return false;
        if (signatureDer.Length == 0 || signatureDer.Length > OptimumUpdateTrust.MaxSignatureBytes) return false;
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            ECParameters parameters = ecdsa.ExportParameters(false);
            if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" && parameters.Curve.Oid.FriendlyName != "nistP256")
                return false;
            return ecdsa.VerifyData(manifest, signatureDer, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
