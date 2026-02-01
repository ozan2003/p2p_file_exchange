using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Utilities;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Manages auto-unlock secrets for passwordless identity key decryption.
/// Uses platform-specific secure storage (DPAPI on Windows, file-based on Linux/Mac).
/// </summary>
public static class AutoUnlockManager
{
    /// <summary>
    /// Default filename for the auto-unlock secret file.
    /// </summary>
    private const string AutoUnlockFileName = "autounlock.dat";

    /// <summary>
    /// Length of the auto-unlock secret in bytes.
    /// </summary>
    private const int SecretLength = 32;

    /// <summary>
    /// Gets the path to the auto-unlock secret file.
    /// </summary>
    private static string AutoUnlockFilePath
    {
        get
        {
            string basePath = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            return Path.Combine(
                basePath,
                AppConstants.AppDataDirectoryName,
                AutoUnlockFileName
            );
        }
    }

    /// <summary>
    /// Checks whether an auto-unlock secret exists.
    /// </summary>
    /// <returns>True if auto-unlock is configured, false otherwise.</returns>
    public static bool IsConfigured()
    {
        return File.Exists(AutoUnlockFilePath);
    }

    /// <summary>
    /// Generates and stores a new auto-unlock secret.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated secret as a Base64 string (for use as password).</returns>
    public static async Task<string> GenerateAndStoreSecretAsync(
        CancellationToken cancellationToken = default
    )
    {
        byte[] secret = RandomNumberGenerator.GetBytes(SecretLength);
        try
        {
            await StoreSecretAsync(secret, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToBase64String(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Retrieves the stored auto-unlock secret.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The secret as a Base64 string (for use as password), or null if not configured.</returns>
    public static async Task<string?> GetSecretAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!IsConfigured())
        {
            return null;
        }

        byte[]? secret = await LoadSecretAsync(cancellationToken)
            .ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Removes the stored auto-unlock secret.
    /// </summary>
    public static void RemoveSecret()
    {
        if (File.Exists(AutoUnlockFilePath))
        {
            // Securely overwrite before deletion
            try
            {
                byte[] zeros = new byte[SecretLength * 2];
                File.WriteAllBytes(AutoUnlockFilePath, zeros);
            }
            catch
            {
                // Best effort secure deletion
            }

            File.Delete(AutoUnlockFilePath);
        }
    }

    /// <summary>
    /// Stores the secret using platform-specific protection.
    /// </summary>
    private static async Task StoreSecretAsync(
        byte[] secret,
        CancellationToken cancellationToken
    )
    {
        byte[] protectedData;

        if (OperatingSystem.IsWindows())
        {
            // Use DPAPI on Windows - provides user-scope encryption
            protectedData = ProtectDataWindows(secret);
        }
        else
        {
            // On Linux/Mac, use file permissions as best-effort protection
            // The secret is stored with restrictive permissions
            protectedData = secret;
        }

        // Ensure directory exists
        string? directory = Path.GetDirectoryName(AutoUnlockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(
                AutoUnlockFilePath,
                protectedData,
                cancellationToken
            )
            .ConfigureAwait(false);

        // Set restrictive file permissions on Unix-like systems
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetUnixFilePermissions(AutoUnlockFilePath);
        }
    }

    /// <summary>
    /// Loads the secret using platform-specific unprotection.
    /// </summary>
    private static async Task<byte[]?> LoadSecretAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            byte[] protectedData = await File.ReadAllBytesAsync(
                    AutoUnlockFilePath,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (OperatingSystem.IsWindows())
            {
                // Use DPAPI on Windows
                return UnprotectDataWindows(protectedData);
            }
            else
            {
                // On Linux/Mac, the secret is stored directly
                return protectedData;
            }
        }
        catch (CryptographicException)
        {
            // DPAPI decryption failed (different user, etc.)
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows-specific DPAPI protection wrapper.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[] ProtectDataWindows(byte[] data)
    {
        return ProtectedData.Protect(
            data,
            null,
            DataProtectionScope.CurrentUser
        );
    }

    /// <summary>
    /// Windows-specific DPAPI unprotection wrapper.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectDataWindows(byte[] data)
    {
        return ProtectedData.Unprotect(
            data,
            null,
            DataProtectionScope.CurrentUser
        );
    }

    /// <summary>
    /// Sets restrictive file permissions on Unix-like systems (owner read/write only).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void SetUnixFilePermissions(string filePath)
    {
        try
        {
            // Use chmod 600 equivalent via UnixFileMode
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
        }
        catch
        {
            // Best effort - may fail on some systems
        }
    }
}
