using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Manages X509 certificate generation, persistence, and fingerprinting.
/// </summary>
public sealed class CertificateManager
{
    #region Constants
    /// <summary>RSA key size used for self-signed certificates.</summary>
    private const int RsaKeySize = 2048;

    /// <summary>The default validity duration in years for generated certificates.</summary>
    public const int DefaultValidityYears = 10;

    /// <summary>Default app data directory name for certificates.</summary>
    private const string DefaultCertificateDirectoryName =
        AppConstants.AppDataDirectoryName;

    /// <summary>Default file name for certificate storage.</summary>
    private const string DefaultCertificateFileName = "peer.pfx";
    #endregion Constants

    #region Paths
    /// <summary>
    /// Gets the default certificate path in the application data directory.
    /// </summary>
    public static string DefaultCertificatePath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData
            ),
            DefaultCertificateDirectoryName,
            DefaultCertificateFileName
        );
    #endregion Paths

    /// <summary>
    /// Generates a self-signed certificate with an exportable private key.
    /// </summary>
    public X509Certificate2 GenerateSelfSignedCertificate()
    {
        return this.GenerateSelfSignedCertificate(DefaultValidityYears);
    }

    /// <summary>
    /// Generates a self-signed certificate with an exportable private key.
    /// </summary>
    /// <param name="validityYears">The certificate validity duration in years.</param>
    public X509Certificate2 GenerateSelfSignedCertificate(int validityYears)
    {
        if (validityYears <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validityYears),
                "Validity years must be positive."
            );
        }

        string commonName = ResolveCommonName();
        using RSA rsa = RSA.Create();
        rsa.KeySize = RsaKeySize;

        CertificateRequest certificateRequest = new(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        certificateRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false)
        );
        certificateRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature
                    | X509KeyUsageFlags.KeyEncipherment,
                false
            )
        );
        certificateRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(
                certificateRequest.PublicKey,
                false
            )
        );

        DateTimeOffset notBefore = DateTimeOffset.UtcNow;
        DateTimeOffset notAfter = notBefore.AddYears(validityYears);

        using X509Certificate2 certificate =
            certificateRequest.CreateSelfSigned(notBefore, notAfter);

        return CreateExportableCertificate(certificate);
    }

    /// <summary>
    /// Saves the certificate as a PFX file.
    /// </summary>
    /// <param name="certificate">The certificate to save.</param>
    /// <param name="filePath">The target file path.</param>
    /// <param name="password">The PFX password.</param>
    public void SaveCertificate(
        X509Certificate2 certificate,
        string filePath,
        string password
    )
    {
        ArgumentNullException.ThrowIfNull(certificate, nameof(certificate));

        EnsureFilePath(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] pfxData = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(filePath, pfxData);
    }

    /// <summary>
    /// Loads a PFX certificate from disk.
    /// </summary>
    /// <param name="filePath">The PFX file path.</param>
    /// <param name="password">The PFX password.</param>
    public X509Certificate2 LoadCertificate(string filePath, string password)
    {
        EnsureFilePath(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                "Certificate file not found.",
                filePath
            );
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            filePath,
            password,
            X509KeyStorageFlags.Exportable,
            null
        );
    }

    /// <summary>
    /// Computes the SHA-256 fingerprint of a certificate.
    /// </summary>
    /// <param name="certificate">The certificate to hash.</param>
    public string GetCertificateFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate, nameof(certificate));

        byte[] hashBytes = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Loads the default certificate or generates and saves one if missing.
    /// </summary>
    /// <param name="password">The PFX password.</param>
    public X509Certificate2 GetOrCreateDefaultCertificate(string password)
    {
        return this.GetOrCreateCertificate(
            DefaultCertificatePath,
            password,
            DefaultValidityYears
        );
    }

    /// <summary>
    /// Loads an existing certificate or generates and saves a new one if missing.
    /// </summary>
    /// <param name="filePath">The PFX file path.</param>
    /// <param name="password">The PFX password.</param>
    /// <param name="validityYears">The certificate validity duration in years.</param>
    public X509Certificate2 GetOrCreateCertificate(
        string filePath,
        string password,
        int validityYears
    )
    {
        EnsureFilePath(filePath);
        if (File.Exists(filePath))
        {
            return this.LoadCertificate(filePath, password);
        }

        X509Certificate2 certificate = this.GenerateSelfSignedCertificate(
            validityYears
        );
        this.SaveCertificate(certificate, filePath, password);
        return certificate;
    }

    private static string ResolveCommonName()
    {
        string machineName = Environment.MachineName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(machineName))
        {
            return machineName;
        }

        return Guid.NewGuid().ToString("D");
    }

    private static X509Certificate2 CreateExportableCertificate(
        X509Certificate2 certificate
    )
    {
        byte[] pfxData = certificate.Export(X509ContentType.Pfx, string.Empty);

        return X509CertificateLoader.LoadPkcs12(
            pfxData,
            string.Empty,
            X509KeyStorageFlags.Exportable,
            null
        );
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
