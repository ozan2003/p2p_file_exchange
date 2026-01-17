using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace P2PFileTransfer.Desktop.Views;

/// <summary>
/// A standalone window for confirming incoming file transfer requests.
/// </summary>
public partial class TransferConfirmationDialog : Window
{
    private TaskCompletionSource<bool>? m_resultTcs;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransferConfirmationDialog"/> class.
    /// </summary>
    public TransferConfirmationDialog()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Sets the dialog content based on the transfer request.
    /// </summary>
    /// <param name="senderName">The display name of the sender.</param>
    /// <param name="fileName">The name of the file being sent.</param>
    /// <param name="fileSize">The size of the file in bytes.</param>
    public void SetContent(string senderName, string fileName, long fileSize)
    {
        this.SenderText.Text = $"{senderName} wants to send you a file:";
        this.FileInfoText.Text = $"{fileName} ({FormatFileSize(fileSize)})";
    }

    /// <summary>
    /// Shows the dialog as a standalone window and waits for user response.
    /// </summary>
    /// <param name="owner">Optional owner window for initial positioning.</param>
    /// <returns>True if the user accepted the transfer; otherwise, false.</returns>
    public Task<bool> ShowAndWaitAsync(Window? owner = null)
    {
        this.m_resultTcs = new TaskCompletionSource<bool>();

        if (owner != null)
        {
            // Show centered on owner, but as independent window.
            this.Show(owner);
        }
        else
        {
            this.Show();
        }

        this.Activate();
        return this.m_resultTcs.Task;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(true);
        this.Close();
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(false);
        this.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // If window is closed without clicking a button, treat as rejection.
        this.m_resultTcs?.TrySetResult(false);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.##} {suffixes[suffixIndex]}";
    }
}
