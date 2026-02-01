using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using P2PFileExchange.Core.Services.Discovery;
using P2PFileExchange.Core.Services.Security;
using P2PFileExchange.Core.Services.Transfer;
using P2PFileExchange.Core.Utilities;
using P2PFileExchange.Desktop.Services;
using P2PFileExchange.Desktop.Settings;
using ReactiveUI;

namespace P2PFileExchange.Desktop.ViewModels;

/// <summary>
/// View model for editing application settings.
/// </summary>
public sealed class SettingsViewModel : ReactiveObject
{
    private const int BytesPerKiB = 1024;
    private readonly AppSettings m_settings;
    private readonly SettingsStore m_settingsStore;
    private readonly IFileTransferService m_fileTransferService;
    private readonly IWindowProvider m_windowProvider;
    private readonly IdentityService? m_identityService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The current application settings.</param>
    /// <param name="settingsStore">The settings store.</param>
    /// <param name="fileTransferService">The file transfer service.</param>
    /// <param name="windowProvider">The window provider.</param>
    /// <param name="identityService">The identity service (optional for backward compatibility).</param>
    public SettingsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        IFileTransferService fileTransferService,
        IWindowProvider windowProvider,
        IdentityService? identityService = null
    )
    {
        this.m_settings = settings;
        this.m_settingsStore = settingsStore;
        this.m_fileTransferService = fileTransferService;
        this.m_windowProvider = windowProvider;
        this.m_identityService = identityService;

        this.LoadFromSettings();

        this.SaveCommand = ReactiveCommand.Create(this.Save);
        this.CancelCommand = ReactiveCommand.Create(() =>
            this.RequestClose?.Invoke(this, EventArgs.Empty)
        );
        this.BrowseDownloadDirectoryCommand = ReactiveCommand.CreateFromTask(
            this.BrowseDownloadDirectoryAsync
        );
        this.BrowseCertificatePathCommand = ReactiveCommand.CreateFromTask(
            this.BrowseCertificatePathAsync
        );
        this.BrowseSigningKeyPathCommand = ReactiveCommand.CreateFromTask(
            this.BrowseSigningKeyPathAsync
        );
        this.ResetToDefaultsCommand = ReactiveCommand.Create(
            this.ResetToDefaults
        );
        this.ExportIdentityKeyCommand = ReactiveCommand.CreateFromTask(
            this.ExportIdentityKeyAsync
        );
        this.RegenerateIdentityCommand = ReactiveCommand.CreateFromTask(
            this.RegenerateIdentityAsync
        );
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? RequestClose;

    /// <summary>
    /// The broadcast interval in seconds.
    /// </summary>
    public decimal BroadcastIntervalSeconds
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasPeerTimeoutWarning));
        }
    }

    /// <summary>
    /// The peer timeout in seconds.
    /// </summary>
    public decimal PeerTimeoutSeconds
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(this.HasPeerTimeoutWarning));
        }
    }

    /// <summary>
    /// The cleanup interval in seconds.
    /// </summary>
    public decimal CleanupIntervalSeconds
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The chunk size in KiB.
    /// </summary>
    public decimal ChunkSizeKiB
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The buffer size in KiB.
    /// </summary>
    public decimal BufferSizeKiB
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The TLS handshake timeout in seconds.
    /// </summary>
    public decimal TlsHandshakeTimeoutSeconds
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The transfer request timeout in seconds.
    /// </summary>
    public decimal TransferRequestTimeoutSeconds
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The download directory.
    /// </summary>
    public string DownloadDirectory
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// The certificate path.
    /// </summary>
    public string CertificatePath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// The certificate validity in years.
    /// </summary>
    public decimal CertificateValidityYears
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The signing key path.
    /// </summary>
    public string SigningKeyPath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// The identity key path.
    /// </summary>
    public string IdentityKeyPath
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Whether to require password on startup.
    /// </summary>
    public bool RequirePasswordOnStartup
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The identity fingerprint for display.
    /// </summary>
    public string IdentityFingerprint
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Gets whether identity information is available.
    /// </summary>
    public bool HasIdentity => this.m_identityService?.IsReady ?? false;

    /// <summary>
    /// The status message for validation and save feedback.
    /// </summary>
    public string StatusMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the peer timeout is less than or equal to the broadcast interval,
    /// which could cause peers to disappear frequently.
    /// </summary>
    public bool HasPeerTimeoutWarning =>
        this.PeerTimeoutSeconds < this.BroadcastIntervalSeconds;

    /// <summary>
    /// Command to save settings.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>
    /// Command to cancel and close the window.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Command to browse for the download directory.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseDownloadDirectoryCommand { get; }

    /// <summary>
    /// Command to browse for the certificate file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseCertificatePathCommand { get; }

    /// <summary>
    /// Command to browse for the signing key file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> BrowseSigningKeyPathCommand { get; }

    /// <summary>
    /// Command to reset all settings to their default values.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    /// <summary>
    /// Command to export the identity key file.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ExportIdentityKeyCommand { get; }

    /// <summary>
    /// Command to regenerate the identity key.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RegenerateIdentityCommand { get; }

    /// <summary>
    /// Loads persisted settings into editable fields.
    /// </summary>
    private void LoadFromSettings()
    {
        this.m_settings.Normalize();

        this.BroadcastIntervalSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.BroadcastInterval.TotalSeconds
        );
        this.PeerTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.PeerTimeout.TotalSeconds
        );
        this.CleanupIntervalSeconds = Math.Max(
            1,
            (int)this.m_settings.Discovery.CleanupInterval.TotalSeconds
        );

        this.ChunkSizeKiB = Math.Max(
            1,
            (int)
                Math.Ceiling(
                    this.m_settings.Transfer.ChunkSize / (double)BytesPerKiB
                )
        );
        this.BufferSizeKiB = Math.Max(
            1,
            (int)
                Math.Ceiling(
                    this.m_settings.Transfer.BufferSize / (double)BytesPerKiB
                )
        );
        this.TlsHandshakeTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Transfer.TlsHandshakeTimeout.TotalSeconds
        );
        this.TransferRequestTimeoutSeconds = Math.Max(
            1,
            (int)this.m_settings.Transfer.TransferRequestTimeout.TotalSeconds
        );

        this.DownloadDirectory = this.m_settings.DownloadDirectory;
        this.CertificatePath = this.m_settings.Security.CertificatePath;
        this.CertificateValidityYears = this.m_settings
            .Security
            .CertificateValidityYears;
        this.SigningKeyPath = this.m_settings.Security.SigningKeyPath;
        this.IdentityKeyPath = this.m_settings.Security.IdentityKeyPath;
        this.RequirePasswordOnStartup = this.m_settings
            .Security
            .RequirePasswordOnStartup;

        // Load identity fingerprint if available
        if (this.m_identityService?.IsReady == true)
        {
            this.IdentityFingerprint = this.m_identityService.Fingerprint;
        }
    }

    /// <summary>
    /// Validates and saves settings changes.
    /// </summary>
    private void Save()
    {
        this.StatusMessage = string.Empty;

        if (
            !this.TryNormalizePath(
                this.DownloadDirectory,
                "Download directory",
                out string downloadDirectory
            )
            || !this.TryNormalizePath(
                this.CertificatePath,
                "Certificate path",
                out string certificatePath
            )
            || !this.TryNormalizePath(
                this.SigningKeyPath,
                "Signing key path",
                out string signingKeyPath
            )
            || !this.TryNormalizePath(
                this.IdentityKeyPath,
                "Identity key path",
                out string identityKeyPath
            )
        )
        {
            return;
        }

        int chunkSizeKiB = (int)this.ChunkSizeKiB;
        int bufferSizeKiB = (int)this.BufferSizeKiB;

        if (chunkSizeKiB > int.MaxValue / BytesPerKiB)
        {
            this.StatusMessage = "Chunk size is too large.";
            return;
        }

        if (bufferSizeKiB > int.MaxValue / BytesPerKiB)
        {
            this.StatusMessage = "Buffer size is too large.";
            return;
        }

        this.m_settings.Discovery.BroadcastInterval = TimeSpan.FromSeconds(
            (int)this.BroadcastIntervalSeconds
        );
        this.m_settings.Discovery.PeerTimeout = TimeSpan.FromSeconds(
            (int)this.PeerTimeoutSeconds
        );
        this.m_settings.Discovery.CleanupInterval = TimeSpan.FromSeconds(
            (int)this.CleanupIntervalSeconds
        );

        this.m_settings.Transfer.ChunkSize = chunkSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.BufferSize = bufferSizeKiB * BytesPerKiB;
        this.m_settings.Transfer.TlsHandshakeTimeout = TimeSpan.FromSeconds(
            (int)this.TlsHandshakeTimeoutSeconds
        );
        this.m_settings.Transfer.TransferRequestTimeout = TimeSpan.FromSeconds(
            (int)this.TransferRequestTimeoutSeconds
        );

        this.m_settings.DownloadDirectory = downloadDirectory;
        this.m_settings.Security.CertificatePath = certificatePath;
        this.m_settings.Security.CertificateValidityYears = (int)
            this.CertificateValidityYears;
        this.m_settings.Security.SigningKeyPath = signingKeyPath;
        this.m_settings.Security.IdentityKeyPath = identityKeyPath;
        this.m_settings.Security.RequirePasswordOnStartup =
            this.RequirePasswordOnStartup;

        try
        {
            this.m_settingsStore.Save(this.m_settings);
        }
        catch (IOException)
        {
            this.StatusMessage = "Failed to save settings to disk.";
            return;
        }
        catch (UnauthorizedAccessException)
        {
            this.StatusMessage = "Settings file is not accessible.";
            return;
        }

        if (this.m_fileTransferService is FileTransferService transferService)
        {
            transferService.UpdateDownloadDirectory(downloadDirectory);
        }

        this.RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resets all settings to their default values, persists them, and updates the UI.
    /// </summary>
    private void ResetToDefaults()
    {
        this.StatusMessage = string.Empty;

        // Apply defaults to discovery settings.
        this.m_settings.Discovery.BroadcastInterval =
            PeerDiscoveryOptions.DefaultBroadcastInterval;
        this.m_settings.Discovery.PeerTimeout =
            PeerDiscoveryOptions.DefaultPeerTimeout;
        this.m_settings.Discovery.CleanupInterval =
            PeerDiscoveryOptions.DefaultCleanupInterval;

        // Apply defaults to transfer settings.
        this.m_settings.Transfer.ChunkSize =
            FileTransferOptions.DefaultChunkSize;
        this.m_settings.Transfer.BufferSize =
            FileTransferOptions.DefaultBufferSize;
        this.m_settings.Transfer.TlsHandshakeTimeout =
            FileTransferOptions.DefaultTlsHandshakeTimeout;
        this.m_settings.Transfer.TransferRequestTimeout =
            FileTransferOptions.DefaultTransferRequestTimeout;

        // Apply defaults to download and security settings.
        this.m_settings.DownloadDirectory =
            FilePathUtilities.GetDefaultDownloadDirectory();
        this.m_settings.Security.CertificatePath =
            CertificateManager.DefaultCertificatePath;
        this.m_settings.Security.CertificateValidityYears =
            CertificateManager.DefaultValidityYears;
        this.m_settings.Security.SigningKeyPath =
            SigningKeyManager.DefaultSigningKeyPath;
        this.m_settings.Security.IdentityKeyPath =
            IdentityKeyManager.DefaultIdentityKeyPath;
        this.m_settings.Security.RequirePasswordOnStartup = true;

        try
        {
            this.m_settingsStore.Save(this.m_settings);
        }
        catch (IOException)
        {
            this.StatusMessage = "Failed to save default settings to disk.";
            return;
        }
        catch (UnauthorizedAccessException)
        {
            this.StatusMessage = "Settings file is not accessible.";
            return;
        }

        // Update the download directory in the transfer service.
        if (this.m_fileTransferService is FileTransferService transferService)
        {
            transferService.UpdateDownloadDirectory(
                this.m_settings.DownloadDirectory
            );
        }

        // Reload the UI fields from the updated settings.
        this.LoadFromSettings();

        this.StatusMessage = "Settings reverted to default.";
    }

    /// <summary>
    /// Opens a folder picker for the download directory.
    /// </summary>
    private async Task BrowseDownloadDirectoryAsync()
    {
        string? path = await this.PickFolderAsync("Select Download Directory");
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.DownloadDirectory = path;
        }
    }

    /// <summary>
    /// Exports the identity key file to a user-selected location.
    /// </summary>
    private async Task ExportIdentityKeyAsync()
    {
        if (this.m_identityService == null || !this.m_identityService.IsReady)
        {
            this.StatusMessage = "No identity key available to export.";
            return;
        }

        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return;
        }

        FilePickerSaveOptions options = new()
        {
            Title = "Export Identity Key",
            SuggestedFileName = "identity-backup.key",
            FileTypeChoices =
            [
                new FilePickerFileType("Identity Key") { Patterns = ["*.key"] },
            ],
        };

        IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(
            options
        );
        if (file == null)
        {
            return;
        }

        try
        {
            await this.m_identityService.ExportKeyAsync(file.Path.LocalPath);
            this.StatusMessage =
                "Identity key exported successfully. Keep this file secure!";
        }
        catch (Exception ex)
        {
            this.StatusMessage = $"Failed to export identity key: {ex.Message}";
        }
    }

    /// <summary>
    /// Regenerates the identity key after user confirmation.
    /// </summary>
    private async Task RegenerateIdentityAsync()
    {
        if (this.m_identityService == null)
        {
            this.StatusMessage = "Identity service not available.";
            return;
        }

        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return;
        }

        // Show confirmation dialog
        var confirmDialog = new Window
        {
            Title = "Regenerate Identity",
            Width = 450,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "⚠️ Warning",
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        FontSize = 16,
                    },
                    new TextBlock
                    {
                        Text =
                            "Regenerating your identity will:\n"
                            + "• Create a new cryptographic identity\n"
                            + "• All peers will see you as a new, untrusted peer\n"
                            + "• You will need to verify your new fingerprint with peers\n\n"
                            + "This action cannot be undone.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia
                            .Layout
                            .HorizontalAlignment
                            .Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button
                            {
                                Content = "Cancel",
                                Tag = false,
                                Width = 80,
                            },
                            new Button
                            {
                                Content = "Regenerate",
                                Tag = true,
                                Width = 100,
                            },
                        },
                    },
                },
            },
        };

        bool? result = null;
        if (
            confirmDialog.Content is StackPanel panel
            && panel.Children[2] is StackPanel buttonPanel
        )
        {
            foreach (var child in buttonPanel.Children)
            {
                if (child is Button button)
                {
                    button.Click += (_, _) =>
                    {
                        result = button.Tag is true;
                        confirmDialog.Close();
                    };
                }
            }
        }

        await confirmDialog.ShowDialog(window);

        if (result != true)
        {
            return;
        }

        try
        {
            IdentityInitResult initResult =
                await this.m_identityService.RegenerateKeyAsync();
            if (initResult == IdentityInitResult.Created)
            {
                this.IdentityFingerprint = this.m_identityService.Fingerprint;
                this.RaisePropertyChanged(nameof(this.HasIdentity));
                this.StatusMessage =
                    "Identity regenerated. Share your new fingerprint with peers.";
            }
            else if (initResult == IdentityInitResult.Cancelled)
            {
                this.StatusMessage = "Identity regeneration cancelled.";
            }
            else
            {
                this.StatusMessage = "Failed to regenerate identity.";
            }
        }
        catch (Exception ex)
        {
            this.StatusMessage = $"Failed to regenerate identity: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a file picker for the certificate path.
    /// </summary>
    private async Task BrowseCertificatePathAsync()
    {
        string? path = await this.PickFileAsync(
            "Select Certificate File",
            [new FilePickerFileType("PFX Certificate") { Patterns = ["*.pfx"] }]
        );
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.CertificatePath = path;
        }
    }

    /// <summary>
    /// Opens a file picker for the signing key path.
    /// </summary>
    private async Task BrowseSigningKeyPathAsync()
    {
        string? path = await this.PickFileAsync(
            "Select Signing Key File",
            [
                new FilePickerFileType("Key File")
                {
                    Patterns = ["*.key", "*.pem"],
                },
            ]
        );
        if (!string.IsNullOrWhiteSpace(path))
        {
            this.SigningKeyPath = path;
        }
    }

    /// <summary>
    /// Opens a folder picker dialog.
    /// </summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return null;
        }

        FolderPickerOpenOptions options = new() { Title = title };
        IReadOnlyList<IStorageFolder> folders =
            await window.StorageProvider.OpenFolderPickerAsync(options);

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Opens a file picker dialog.
    /// </summary>
    private async Task<string?> PickFileAsync(
        string title,
        FilePickerFileType[] fileTypes
    )
    {
        Window? window = this.m_windowProvider.MainWindow;
        if (window == null)
        {
            return null;
        }

        FilePickerOpenOptions options = new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes,
        };
        IReadOnlyList<IStorageFile> files =
            await window.StorageProvider.OpenFilePickerAsync(options);

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    /// <summary>
    /// Normalizes a file system path from user input.
    /// </summary>
    private bool TryNormalizePath(
        ReadOnlySpan<char> value,
        ReadOnlySpan<char> fieldName,
        out string normalized
    )
    {
        normalized = string.Empty;
        if (value.IsEmpty || MemoryExtensions.IsWhiteSpace(value))
        {
            this.StatusMessage = $"{fieldName} is required.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value.Trim().ToString());
            return true;
        }
        catch (Exception)
        {
            this.StatusMessage = $"{fieldName} is invalid.";
            return false;
        }
    }
}
