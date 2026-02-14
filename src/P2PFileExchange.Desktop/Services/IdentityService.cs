using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using P2PFileExchange.Core.Services.Security;

namespace P2PFileExchange.Desktop.Services;

/// <summary>
/// Service that manages identity key lifecycle, including initialization,
/// password prompting, and auto-unlock functionality.
/// </summary>
public sealed class IdentityService : IDisposable
{
    private readonly IdentityKeyManager m_keyManager;
    private readonly IPasswordProvider m_passwordProvider;
    private readonly string m_keyPath;
    private readonly bool m_requirePassword;
    private bool m_disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityService"/> class.
    /// </summary>
    /// <param name="keyManager">The identity key manager.</param>
    /// <param name="passwordProvider">The password provider.</param>
    /// <param name="keyPath">Path to the identity key file.</param>
    /// <param name="requirePassword">Whether to require password on startup.</param>
    public IdentityService(
        IdentityKeyManager keyManager,
        IPasswordProvider passwordProvider,
        string keyPath,
        bool requirePassword
    )
    {
        this.m_keyManager = keyManager;
        this.m_passwordProvider = passwordProvider;
        this.m_keyPath = keyPath;
        this.m_requirePassword = requirePassword;
    }

    /// <summary>
    /// Gets whether the identity key is loaded and ready.
    /// </summary>
    public bool IsReady => this.m_keyManager.IsLoaded;

    /// <summary>
    /// Gets the peer ID derived from the identity key.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public Guid PeerId => this.m_keyManager.PeerId;

    /// <summary>
    /// Gets the identity fingerprint for display.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public string Fingerprint => this.m_keyManager.Fingerprint;

    /// <summary>
    /// Gets the public key as Base64.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public string PublicKeyBase64 => this.m_keyManager.PublicKeyBase64;

    /// <summary>
    /// Gets the public key bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if not initialized.</exception>
    public ImmutableArray<byte> PublicKey => this.m_keyManager.PublicKey;

    /// <summary>
    /// Initializes the identity key, either by loading an existing key or creating a new one.
    /// Handles password prompting with retry logic.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the initialization.</returns>
    public async Task<IdentityInitResult> InitializeAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        bool keyExists = IdentityKeyManager.KeyExists(this.m_keyPath);

        if (keyExists)
        {
            return await this.LoadExistingKeyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return await this.CreateNewKeyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<IdentityInitResult> LoadExistingKeyAsync(
        CancellationToken cancellationToken
    )
    {
        // Try auto-unlock first if enabled
        if (!this.m_requirePassword && AutoUnlockManager.IsConfigured())
        {
            string? autoSecret = await AutoUnlockManager
                .GetSecretAsync(cancellationToken)
                .ConfigureAwait(false);

            if (autoSecret is not null)
            {
                try
                {
                    await this
                        .m_keyManager.LoadAsync(
                            this.m_keyPath,
                            autoSecret.AsMemory(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return IdentityInitResult.Success;
                }
                catch (CryptographicException)
                {
                    // Auto-unlock secret is invalid, fall back to password
                    AutoUnlockManager.RemoveSecret();
                }
            }
        }

        // Prompt for password with retry logic
        int attemptsRemaining = IdentityKeyManager.MaxPasswordAttempts;

        while (attemptsRemaining > 0)
        {
            string? password = await this
                .m_passwordProvider.GetPasswordAsync(
                    attemptsRemaining,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (password is null)
            {
                return IdentityInitResult.Cancelled;
            }

            try
            {
                await this
                    .m_keyManager.LoadAsync(
                        this.m_keyPath,
                        password.AsMemory(),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // Set up auto-unlock if not requiring password
                if (
                    !this.m_requirePassword && !AutoUnlockManager.IsConfigured()
                )
                {
                    await this.SetupAutoUnlockAsync(password, cancellationToken)
                        .ConfigureAwait(false);
                }

                return IdentityInitResult.Success;
            }
            catch (CryptographicException)
            {
                attemptsRemaining--;

                if (attemptsRemaining > 0)
                {
                    await this
                        .m_passwordProvider.NotifyInvalidPasswordAsync(
                            attemptsRemaining,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidDataException)
            {
                return IdentityInitResult.CorruptedFile;
            }
        }

        await this
            .m_passwordProvider.NotifyPasswordAttemptsExhaustedAsync(
                cancellationToken
            )
            .ConfigureAwait(false);

        return IdentityInitResult.TooManyAttempts;
    }

    private async Task<IdentityInitResult> CreateNewKeyAsync(
        CancellationToken cancellationToken
    )
    {
        string? password;

        if (!this.m_requirePassword)
        {
            // Auto-unlock mode: generate random secret
            password = await AutoUnlockManager
                .GenerateAndStoreSecretAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Prompt user to create a new password
            password = await this
                .m_passwordProvider.CreatePasswordAsync(cancellationToken)
                .ConfigureAwait(false);

            if (password is null)
            {
                return IdentityInitResult.Cancelled;
            }
        }

        try
        {
            await this
                .m_keyManager.GenerateAndSaveAsync(
                    this.m_keyPath,
                    password.AsMemory(),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return IdentityInitResult.Created;
        }
        catch (Exception)
        {
            // Clean up auto-unlock secret if key generation failed
            if (!this.m_requirePassword)
            {
                AutoUnlockManager.RemoveSecret();
            }
            throw;
        }
    }

    private async Task SetupAutoUnlockAsync(
        string password,
        CancellationToken cancellationToken
    )
    {
        // For auto-unlock, we need to re-encrypt the key with a generated secret
        // and store that secret securely
        string autoSecret = await AutoUnlockManager
            .GenerateAndStoreSecretAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Re-encrypt the key with the auto-unlock secret
            await this
                .m_keyManager.ChangePasswordAsync(
                    this.m_keyPath,
                    password.AsMemory(),
                    autoSecret.AsMemory(),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            // If re-encryption fails, remove the auto-unlock secret
            AutoUnlockManager.RemoveSecret();
            throw;
        }
    }

    /// <summary>
    /// Signs data using the loaded identity key.
    /// </summary>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature.</returns>
    public byte[] Sign(byte[] data)
    {
        this.ThrowIfDisposed();
        return this.m_keyManager.Sign(data);
    }

    /// <summary>
    /// Exports the identity key file to a backup location.
    /// </summary>
    /// <param name="destinationPath">The destination path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExportKeyAsync(
        string destinationPath,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        await IdentityKeyManager
            .ExportKeyAsync(this.m_keyPath, destinationPath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates the identity key with a new keypair.
    /// Warning: This will change the peer's identity.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the regeneration.</returns>
    public async Task<IdentityInitResult> RegenerateKeyAsync(
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        string? password;

        if (!this.m_requirePassword)
        {
            // Generate new auto-unlock secret
            AutoUnlockManager.RemoveSecret();
            password = await AutoUnlockManager
                .GenerateAndStoreSecretAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Prompt for new password
            password = await this
                .m_passwordProvider.CreatePasswordAsync(cancellationToken)
                .ConfigureAwait(false);

            if (password is null)
            {
                return IdentityInitResult.Cancelled;
            }
        }

        try
        {
            await this
                .m_keyManager.RegenerateAsync(
                    this.m_keyPath,
                    password.AsMemory(),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return IdentityInitResult.Created;
        }
        catch
        {
            if (!this.m_requirePassword)
            {
                AutoUnlockManager.RemoveSecret();
            }
            throw;
        }
    }

    /// <summary>
    /// Changes the password for the identity key.
    /// </summary>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();
        await this
            .m_keyManager.ChangePasswordAsync(
                this.m_keyPath,
                currentPassword.AsMemory(),
                newPassword.AsMemory(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Enables auto-unlock mode with the current password.
    /// </summary>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnableAutoUnlockAsync(
        string currentPassword,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        if (AutoUnlockManager.IsConfigured())
        {
            return;
        }

        await this.SetupAutoUnlockAsync(currentPassword, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Disables auto-unlock mode and requires a new password.
    /// </summary>
    /// <param name="newPassword">The new password to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DisableAutoUnlockAsync(
        string newPassword,
        CancellationToken cancellationToken = default
    )
    {
        this.ThrowIfDisposed();

        if (!AutoUnlockManager.IsConfigured())
        {
            return;
        }

        // Get the current auto-unlock secret
        string? autoSecret =
            await AutoUnlockManager
                .GetSecretAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Auto-unlock is not configured."
            );

        // Re-encrypt with the new user password
        await this
            .m_keyManager.ChangePasswordAsync(
                this.m_keyPath,
                autoSecret.AsMemory(),
                newPassword.AsMemory(),
                cancellationToken
            )
            .ConfigureAwait(false);

        // Remove the auto-unlock secret
        AutoUnlockManager.RemoveSecret();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.m_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.m_disposed)
        {
            return;
        }

        this.m_disposed = true;
        this.m_keyManager.Dispose();
    }
}

/// <summary>
/// Result of identity initialization.
/// </summary>
public enum IdentityInitResult
{
    /// <summary>
    /// Identity key loaded successfully.
    /// </summary>
    Success,

    /// <summary>
    /// New identity key was created.
    /// </summary>
    Created,

    /// <summary>
    /// User cancelled the operation.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Too many failed password attempts.
    /// </summary>
    TooManyAttempts,

    /// <summary>
    /// Identity key file is corrupted.
    /// </summary>
    CorruptedFile,
}
