using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Manages ECDSA P-256 keypair generation, persistence, and cryptographic operations
/// for discovery broadcast authentication.
///
/// <list type="bullet">
/// <item>At first launch, a keypair is generated and saved to the application data directory.</item>
/// <item>The keypair is used to sign discovery broadcasts.</item>
/// <item>The public key is exported and shared with other peers.</item>
/// <item>The private key is kept secure and never exposed to other peers.</item>
/// <item>The keypair is loaded from the application data directory on subsequent launches.</item>
/// </list>
/// </summary>
public sealed class SigningKeyManager
{
    #region Constants
    /// <summary>Default app data directory name for signing keys.</summary>
    private const string DefaultSigningKeyDirectoryName =
        AppConstants.AppDataDirectoryName;

    /// <summary>Default file name for signing key storage.</summary>
    private const string DefaultSigningKeyFileName = "signing.key";
    #endregion Constants

    #region Paths
    /// <summary>
    /// Gets the default signing key path in the application data directory.
    /// </summary>
    public static string DefaultSigningKeyPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData
            ),
            DefaultSigningKeyDirectoryName,
            DefaultSigningKeyFileName
        );
    #endregion Paths

    /// <summary>
    /// Generates a new ECDSA P-256 keypair.
    /// </summary>
    /// <returns>A new ECDsa instance with generated keys.</returns>
    public ECDsa GenerateKeyPair()
    {
        return ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>
    /// Saves an ECDSA keypair to a PEM file.
    /// </summary>
    /// <param name="key">The ECDSA key to save.</param>
    /// <param name="filePath">The target file path.</param>
    public void SaveKeyPair(ECDsa key, string filePath)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        EnsureFilePath(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string pem = key.ExportECPrivateKeyPem();
        File.WriteAllText(filePath, pem, Encoding.UTF8);
    }

    /// <summary>
    /// Loads an ECDSA keypair from a PEM file.
    /// </summary>
    /// <param name="filePath">The PEM file path.</param>
    /// <returns>The loaded ECDsa instance.</returns>
    public ECDsa LoadKeyPair(string filePath)
    {
        EnsureFilePath(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "Signing key file not found.",
                filePath
            );
        }

        string pem = File.ReadAllText(filePath, Encoding.UTF8);
        ECDsa key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    /// <summary>
    /// Loads the default signing key or generates and saves one if missing.
    /// </summary>
    /// <returns>The ECDsa keypair.</returns>
    public ECDsa GetOrCreateDefaultKeyPair()
    {
        return this.GetOrCreateKeyPair(DefaultSigningKeyPath);
    }

    /// <summary>
    /// Loads an existing signing key or generates and saves a new one if missing.
    /// </summary>
    /// <param name="filePath">The key file path.</param>
    /// <returns>The ECDsa keypair.</returns>
    public ECDsa GetOrCreateKeyPair(string filePath)
    {
        if (File.Exists(filePath))
        {
            return this.LoadKeyPair(filePath);
        }

        ECDsa key = this.GenerateKeyPair();
        this.SaveKeyPair(key, filePath);
        return key;
    }

    /// <summary>
    /// Exports the public key as a base64-encoded string for network transmission.
    /// </summary>
    /// <param name="key">The ECDSA key.</param>
    /// <returns>Base64-encoded public key.</returns>
    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));

        byte[] publicKeyBytes = key.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(publicKeyBytes);
    }

    /// <summary>
    /// Imports a public key from a base64-encoded string.
    /// </summary>
    /// <param name="base64PublicKey">The base64-encoded public key.</param>
    /// <returns>An ECDsa instance with only the public key loaded.</returns>
    public static ECDsa ImportPublicKey(string base64PublicKey)
    {
        if (string.IsNullOrWhiteSpace(base64PublicKey))
        {
            throw new ArgumentException(
                "Public key is required.",
                nameof(base64PublicKey)
            );
        }

        byte[] publicKeyBytes = Convert.FromBase64String(base64PublicKey);
        ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return key;
    }

    /// <summary>
    /// Signs data using ECDSA with SHA256.
    /// </summary>
    /// <param name="key">The ECDSA private key.</param>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature bytes.</returns>
    public static byte[] SignData(ECDsa key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        ArgumentNullException.ThrowIfNull(data, nameof(data));

        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Signs data and returns the signature as a base64 string.
    /// </summary>
    /// <param name="key">The ECDSA private key.</param>
    /// <param name="data">The data to sign.</param>
    /// <returns>Base64-encoded signature.</returns>
    public static string SignDataToBase64(ECDsa key, byte[] data)
    {
        byte[] signature = SignData(key, data);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies a signature using ECDSA with SHA256.
    /// </summary>
    /// <param name="key">The ECDSA public key.</param>
    /// <param name="data">The original data.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <returns>True if the signature is valid; otherwise, false.</returns>
    public static bool VerifySignature(ECDsa key, byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        ArgumentNullException.ThrowIfNull(data, nameof(data));
        ArgumentNullException.ThrowIfNull(signature, nameof(signature));

        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Verifies a base64-encoded signature.
    /// </summary>
    /// <param name="key">The ECDSA public key.</param>
    /// <param name="data">The original data.</param>
    /// <param name="base64Signature">The base64-encoded signature.</param>
    /// <returns>True if the signature is valid; otherwise, false.</returns>
    public static bool VerifySignatureFromBase64(
        ECDsa key,
        byte[] data,
        string base64Signature
    )
    {
        if (string.IsNullOrWhiteSpace(base64Signature))
        {
            return false;
        }

        try
        {
            byte[] signature = Convert.FromBase64String(base64Signature);
            return VerifySignature(key, data, signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the data bytes for signing a peer announcement.
    /// <list type="bullet">
    /// <item>Format: SHA256(PeerId + DisplayName + TcpPort + CertificateFingerprint)</item>
    /// </list>
    /// </summary>
    /// <param name="peerId">The peer identifier.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="tcpPort">The TCP port.</param>
    /// <param name="certificateFingerprint">The certificate fingerprint.</param>
    /// <returns>The data bytes to sign.</returns>
    public static byte[] CreateAnnouncementSigningData(
        Guid peerId,
        string displayName,
        ushort tcpPort,
        string certificateFingerprint
    )
    {
        string concatenated = string.Concat(
            peerId.ToString("D"),
            displayName ?? string.Empty,
            tcpPort,
            certificateFingerprint ?? string.Empty
        );

        byte[] dataBytes = Encoding.UTF8.GetBytes(concatenated);
        return SHA256.HashData(dataBytes);
    }

    private static void EnsureFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "File path is required.",
                nameof(filePath)
            );
        }
    }
}
