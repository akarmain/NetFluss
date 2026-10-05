// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;

namespace NetFluss.Core;

/// <summary>
/// The release signature the in-app updater requires — the Windows counterpart of Sparkle's
/// EdDSA signature on macOS. A release's SHA256SUMS.txt is signed in CI with a private key
/// that exists only as a GitHub secret; the public half is compiled in below. The installer
/// is then checked against the signed list, so a release whose files were swapped — even by
/// someone able to upload to it — cannot pass: they would need the key, not the repository.
///
/// <para>ECDSA P-256 over SHA-256, the signature base64 in IEEE P1363 form (r‖s), in a file
/// named <see cref="AssetName"/> beside the list. Built into .NET, so no dependency; Sparkle's
/// Ed25519 is not.</para>
///
/// <para>Rotating the key: ship a release signed with the old key whose app carries the new
/// public key, then switch the secret. Losing the private key means installed copies can no
/// longer update themselves, and users must reinstall once by hand.</para>
/// </summary>
public static class UpdateSignature
{
    public const string AssetName = "SHA256SUMS.txt.sig";

    /// <summary>The release signing key's public half (secret NETFLUSS_UPDATE_SIGNING_KEY).</summary>
    public const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhgXqHPIGEB+EojGd1/d2JpUklprX
        jWpZPLzw3X5lQH5VggE2kOjB+kO42hrZ7Q7x9qN4SnTuGc4bxLf5bWEBYw==
        -----END PUBLIC KEY-----
        """;

    /// <summary>Whether <paramref name="signature"/> is the key's signature over exactly <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signature, string publicKeyPem = PublicKey)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The base64 signature over <paramref name="data"/>; what CI writes to <see cref="AssetName"/>.</summary>
    public static string Sign(ReadOnlySpan<byte> data, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>A new P-256 key pair as PEM: the private key for the secret, the public one for <see cref="PublicKey"/>.</summary>
    public static (string PrivateKey, string PublicKey) CreateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }
}
