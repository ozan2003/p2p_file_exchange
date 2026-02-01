using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using P2PFileExchange.Core.Models;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Result from the PeerSecurityDetailsDialog.
/// </summary>
public enum PeerSecurityDialogResult
{
    /// <summary>
    /// Dialog was closed without changes.
    /// </summary>
    Closed,

    /// <summary>
    /// Notes were saved.
    /// </summary>
    Saved,

    /// <summary>
    /// Peer was blocked.
    /// </summary>
    Blocked,

    /// <summary>
    /// Peer was removed from trust database.
    /// </summary>
    Removed,
}

/// <summary>
/// Detailed dialog result including any updated notes.
/// </summary>
public sealed record PeerSecurityDialogResultInfo(
    PeerSecurityDialogResult Result,
    string? UpdatedNotes
);

/// <summary>
/// Dialog showing detailed security information about a trusted peer.
/// Allows viewing fingerprint, statistics, and managing trust status.
/// </summary>
public partial class PeerSecurityDetailsDialog : Window
{
    private TaskCompletionSource<PeerSecurityDialogResultInfo>? m_resultTcs;
    private TrustedPeerInfo? m_peerInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSecurityDetailsDialog"/> class.
    /// </summary>
    public PeerSecurityDetailsDialog()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Sets the dialog content from a TrustedPeerInfo record.
    /// </summary>
    /// <param name="peerInfo">The trusted peer information.</param>
    /// <param name="currentDisplayName">The current display name from discovery (optional, falls back to cached).</param>
    public void SetContent(
        TrustedPeerInfo peerInfo,
        string? currentDisplayName = null
    )
    {
        this.m_peerInfo = peerInfo;

        this.PeerNameText.Text =
            currentDisplayName ?? peerInfo.CachedDisplayName ?? "Unknown";
        this.FingerprintText.Text = peerInfo.PublicKeyFingerprint;
        this.FirstSeenText.Text = FormatDateTime(peerInfo.FirstTrusted);
        this.LastSeenText.Text = FormatDateTime(peerInfo.LastSeen);
        this.TransferCountText.Text = peerInfo.TransferCount.ToString();
        this.FailedTransferCountText.Text =
            peerInfo.FailedTransferCount.ToString();
        this.PeerIdText.Text = peerInfo.PeerId.ToString();
        this.NotesTextBox.Text = peerInfo.Notes ?? string.Empty;

        // Set trust level badge
        this.SetTrustLevelBadge(peerInfo.TrustLevel);
    }

    /// <summary>
    /// Sets the trust level badge appearance.
    /// </summary>
    private void SetTrustLevelBadge(TrustLevel trustLevel)
    {
        switch (trustLevel)
        {
            case TrustLevel.Trusted:
                this.TrustBadge.Background = new SolidColorBrush(
                    Color.FromArgb(0x44, 0x44, 0xBB, 0x44)
                );
                this.TrustLevelText.Text = "Trusted";
                this.TrustLevelText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0x44, 0xBB, 0x44)
                );
                this.BlockButton.Content = "Block";
                break;

            case TrustLevel.Blocked:
                this.TrustBadge.Background = new SolidColorBrush(
                    Color.FromArgb(0x44, 0xFF, 0x44, 0x44)
                );
                this.TrustLevelText.Text = "Blocked";
                this.TrustLevelText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0xFF, 0x66, 0x66)
                );
                this.BlockButton.Content = "Unblock";
                break;

            case TrustLevel.Unknown:
            default:
                this.TrustBadge.Background = new SolidColorBrush(
                    Color.FromArgb(0x44, 0xAA, 0xAA, 0xAA)
                );
                this.TrustLevelText.Text = "? Unknown";
                this.TrustLevelText.Foreground = new SolidColorBrush(
                    Color.FromRgb(0xAA, 0xAA, 0xAA)
                );
                this.BlockButton.Content = "Block";
                break;
        }
    }

    /// <summary>
    /// Shows the dialog as a standalone window and waits for user response.
    /// </summary>
    /// <param name="owner">Optional owner window for initial positioning.</param>
    /// <returns>The dialog result including any updated notes.</returns>
    public Task<PeerSecurityDialogResultInfo> ShowAndWaitAsync(
        Window? owner = null
    )
    {
        this.m_resultTcs =
            new TaskCompletionSource<PeerSecurityDialogResultInfo>();

        if (owner != null)
        {
            this.Show(owner);
        }
        else
        {
            this.Show();
        }

        this.Activate();
        return this.m_resultTcs.Task;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        string? notes = string.IsNullOrWhiteSpace(this.NotesTextBox.Text)
            ? null
            : this.NotesTextBox.Text;

        this.m_resultTcs?.TrySetResult(
            new PeerSecurityDialogResultInfo(
                PeerSecurityDialogResult.Saved,
                notes
            )
        );
        this.Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(
            new PeerSecurityDialogResultInfo(
                PeerSecurityDialogResult.Closed,
                null
            )
        );
        this.Close();
    }

    private void OnBlockClick(object? sender, RoutedEventArgs e)
    {
        // If already blocked, this becomes "Unblock" - but we still return Blocked
        // The caller will handle toggling the state
        this.m_resultTcs?.TrySetResult(
            new PeerSecurityDialogResultInfo(
                PeerSecurityDialogResult.Blocked,
                null
            )
        );
        this.Close();
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(
            new PeerSecurityDialogResultInfo(
                PeerSecurityDialogResult.Removed,
                null
            )
        );
        this.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        this.m_resultTcs?.TrySetResult(
            new PeerSecurityDialogResultInfo(
                PeerSecurityDialogResult.Closed,
                null
            )
        );
    }

    /// <summary>
    /// Formats a DateTimeOffset for display.
    /// </summary>
    private static string FormatDateTime(DateTimeOffset dateTime)
    {
        TimeSpan age = DateTimeOffset.UtcNow - dateTime;

        if (age.TotalMinutes < 1)
        {
            return "Just now";
        }
        else if (age.TotalHours < 1)
        {
            int minutes = (int)age.TotalMinutes;
            return $"{minutes} minute{(minutes != 1 ? "s" : "")} ago";
        }
        else if (age.TotalDays < 1)
        {
            int hours = (int)age.TotalHours;
            return $"{hours} hour{(hours != 1 ? "s" : "")} ago";
        }
        else if (age.TotalDays < 7)
        {
            int days = (int)age.TotalDays;
            return $"{days} day{(days != 1 ? "s" : "")} ago";
        }
        else
        {
            return dateTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
    }
}
