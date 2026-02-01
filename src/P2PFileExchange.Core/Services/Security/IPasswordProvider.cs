using System.Threading;
using System.Threading.Tasks;

namespace P2PFileExchange.Core.Services.Security;

/// <summary>
/// Provides password input for identity key encryption/decryption.
/// Implementations handle platform-specific password prompting (dialog, console, etc.).
/// </summary>
public interface IPasswordProvider
{
    /// <summary>
    /// Prompts the user for a password to decrypt an existing identity key.
    /// </summary>
    /// <param name="attemptsRemaining">Number of attempts remaining before lockout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The password entered by the user, or null if the user cancelled.
    /// </returns>
    Task<string?> GetPasswordAsync(
        int attemptsRemaining,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Prompts the user to create a new password for identity key encryption.
    /// Implementations should require password confirmation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The new password entered by the user, or null if the user cancelled.
    /// </returns>
    Task<string?> CreatePasswordAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Notifies the user that the password was incorrect.
    /// </summary>
    /// <param name="attemptsRemaining">Number of attempts remaining before lockout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyInvalidPasswordAsync(
        int attemptsRemaining,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Notifies the user that all password attempts have been exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyPasswordAttemptsExhaustedAsync(
        CancellationToken cancellationToken = default
    );
}
