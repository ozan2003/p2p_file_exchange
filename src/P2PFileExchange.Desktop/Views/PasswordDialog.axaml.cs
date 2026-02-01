using Avalonia.Controls;
using Avalonia.Interactivity;
using P2PFileExchange.Core.Services.Security;

namespace P2PFileExchange.Desktop.Views;

/// <summary>
/// Dialog for entering the identity key password.
/// </summary>
public partial class PasswordDialog : Window
{
    /// <summary>
    /// Minimum password length for identity key protection.
    /// </summary>
    private const int MinPasswordLength = 3;

    private readonly bool m_isNewPassword;
    private readonly int? m_attemptsRemaining;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordDialog"/> class.
    /// </summary>
    /// <param name="isNewPassword">Whether this is for creating a new password.</param>
    /// <param name="attemptsRemaining">Number of attempts remaining (null for new password).</param>
    public PasswordDialog(bool isNewPassword, int? attemptsRemaining)
    {
        this.InitializeComponent();
        this.m_isNewPassword = isNewPassword;
        this.m_attemptsRemaining = attemptsRemaining;

        this.ConfigureDialog();
    }

    /// <summary>
    /// Parameterless constructor for XAML designer.
    /// </summary>
    public PasswordDialog()
        : this(false, IdentityKeyManager.MaxPasswordAttempts) { }

    private void ConfigureDialog()
    {
        if (this.m_isNewPassword)
        {
            this.HeaderText.Text = "Create Identity Key Password";
            this.DescriptionText.Text =
                "Create a password to protect your identity key. "
                + "This password will be required each time you start the application.";
            this.ConfirmPanel.IsVisible = true;
            this.OkButton.Content = "Create";
        }
        else
        {
            this.HeaderText.Text = "Unlock Identity Key";

            if (
                this.m_attemptsRemaining.HasValue
                && this.m_attemptsRemaining.Value
                    < IdentityKeyManager.MaxPasswordAttempts
            )
            {
                this.DescriptionText.Text =
                    $"Enter your password to unlock your identity key. "
                    + $"{this.m_attemptsRemaining.Value} attempt(s) remaining.";
            }
            else
            {
                this.DescriptionText.Text =
                    "Enter your password to unlock your identity key.";
            }

            this.ConfirmPanel.IsVisible = false;
            this.OkButton.Content = "Unlock";
        }

        this.PasswordBox.AttachedToVisualTree += (_, _) =>
            this.PasswordBox.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        string password = this.PasswordBox.Text ?? string.Empty;

        if (string.IsNullOrEmpty(password))
        {
            this.ShowError("Password cannot be empty.");
            return;
        }

        if (this.m_isNewPassword)
        {
            if (password.Length < MinPasswordLength)
            {
                this.ShowError(
                    $"Password must be at least {MinPasswordLength} characters."
                );
                return;
            }

            string confirmPassword =
                this.ConfirmPasswordBox.Text ?? string.Empty;
            if (password != confirmPassword)
            {
                this.ShowError("Passwords do not match.");
                return;
            }
        }

        this.Close(password);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        this.Close(null);
    }

    private void ShowError(string message)
    {
        this.ErrorText.Text = message;
        this.ErrorText.IsVisible = true;
    }
}
