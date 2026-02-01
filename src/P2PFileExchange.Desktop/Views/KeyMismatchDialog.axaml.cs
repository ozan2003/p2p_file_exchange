using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Result from the KeyMismatchDialog.
/// </summary>
public enum KeyMismatchDialogResult
{
    /// <summary>
    /// User approved the key change (verified out-of-band).
    /// </summary>
    Approve,

    /// <summary>
    /// User rejected the connection but did not block.
    /// </summary>
    Reject,

    /// <summary>
    /// User blocked the peer.
    /// </summary>
    Block,
}

/// <summary>
/// Dialog shown when a previously trusted peer's public key has changed.
/// This is a security-critical warning that could indicate a MITM attack.
/// </summary>
public partial class KeyMismatchDialog : Window
{
    private TaskCompletionSource<KeyMismatchDialogResult>? m_resultTcs;

    /// <summary>
    /// Gets the peer ID associated with this dialog.
    /// </summary>
    public Guid PeerId { get; private set; }

    /// <summary>
    /// Gets the peer's display name.
    /// </summary>
    public string PeerName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the peer's new Ed25519 public key.
    /// </summary>
    public byte[] NewPublicKey { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyMismatchDialog"/> class.
    /// </summary>
    public KeyMismatchDialog()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Sets the dialog content based on the key mismatch information.
    /// </summary>
    /// <param name="peerId">The peer's unique identifier.</param>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="oldFingerprint">The previously trusted fingerprint.</param>
    /// <param name="newFingerprint">The new fingerprint received.</param>
    /// <param name="newPublicKey">The new Ed25519 public key.</param>
    public void SetContent(
        Guid peerId,
        string peerName,
        string oldFingerprint,
        string newFingerprint,
        byte[] newPublicKey
    )
    {
        this.PeerId = peerId;
        this.PeerName = peerName;
        this.NewPublicKey = newPublicKey;

        this.WarningText.Text =
            $"The peer \"{peerName}\" is presenting a different public key than "
            + "the one you previously trusted. This could indicate a security breach.";

        this.OldFingerprintText.Text = oldFingerprint;
        this.NewFingerprintText.Text = newFingerprint;
    }

    /// <summary>
    /// Shows the dialog as a standalone window and waits for user response.
    /// </summary>
    /// <param name="owner">Optional owner window for initial positioning.</param>
    /// <returns>The user's decision regarding the key mismatch.</returns>
    public Task<KeyMismatchDialogResult> ShowAndWaitAsync(Window? owner = null)
    {
        this.m_resultTcs = new TaskCompletionSource<KeyMismatchDialogResult>();

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

    private void OnApproveClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(KeyMismatchDialogResult.Approve);
        this.Close();
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(KeyMismatchDialogResult.Reject);
        this.Close();
    }

    private void OnBlockClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(KeyMismatchDialogResult.Block);
        this.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // If window is closed without clicking a button, treat as rejection.
        this.m_resultTcs?.TrySetResult(KeyMismatchDialogResult.Reject);
    }
}
