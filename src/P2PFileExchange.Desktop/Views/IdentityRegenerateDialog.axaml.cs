using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Result from the IdentityRegenerateDialog.
/// </summary>
public enum IdentityRegenerateDialogResult
{
    /// <summary>User cancelled the operation.</summary>
    Cancel,

    /// <summary>User confirmed regeneration.</summary>
    Regenerate,
}

/// <summary>
/// Dialog shown to confirm identity key regeneration.
/// Warns the user that this action cannot be undone.
/// </summary>
public partial class IdentityRegenerateDialog : Window
{
    private TaskCompletionSource<IdentityRegenerateDialogResult>? m_resultTcs;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityRegenerateDialog"/> class.
    /// </summary>
    public IdentityRegenerateDialog()
    {
        this.InitializeComponent();
        this.Closed += this.OnWindowClosed;
    }

    /// <summary>
    /// Shows the dialog as a standalone window and waits for user response.
    /// </summary>
    /// <param name="owner">Optional owner window for initial positioning.</param>
    /// <returns>The user's decision.</returns>
    public Task<IdentityRegenerateDialogResult> ShowAndWaitAsync(
        Window? owner = null
    )
    {
        this.m_resultTcs =
            new TaskCompletionSource<IdentityRegenerateDialogResult>();

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

    private void OnRegenerateClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(
            IdentityRegenerateDialogResult.Regenerate
        );
        this.Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        this.m_resultTcs?.TrySetResult(IdentityRegenerateDialogResult.Cancel);
        this.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        this.m_resultTcs?.TrySetResult(IdentityRegenerateDialogResult.Cancel);
    }
}
