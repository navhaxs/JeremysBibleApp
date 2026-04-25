using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Represents the result of an authentication attempt
/// </summary>
public sealed class AuthenticationResult
{
    /// <summary>
    /// Whether authentication was successful
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// OAuth access token if successful
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Error message if unsuccessful
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// User's email address if successful
    /// </summary>
    public string? UserEmail { get; set; }

    public static AuthenticationResult Success(string accessToken, string userEmail)
        => new() { IsSuccess = true, AccessToken = accessToken, UserEmail = userEmail };

    public static AuthenticationResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Interface for Google Drive authentication
/// </summary>
public interface IGoogleDriveAuthService
{
    /// <summary>
    /// Gets whether the user is currently authenticated
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the current access token if authenticated
    /// </summary>
    string? CurrentAccessToken { get; }

    /// <summary>
    /// Gets the current user's email if authenticated
    /// </summary>
    string? CurrentUserEmail { get; }

    /// <summary>
    /// Attempts authentication using cached credentials only (no browser/interactive flow).
    /// Returns a failure result instead of prompting the user when no valid token is cached.
    /// Safe to call on startup.
    /// </summary>
    Task<AuthenticationResult> TrySilentAuthAsync();

    /// <summary>
    /// Authenticates the user with Google (may open a browser / interactive prompt).
    /// </summary>
    Task<AuthenticationResult> AuthenticateAsync();

    /// <summary>
    /// Cancels any in-progress interactive authentication flow.
    /// Safe to call even when no auth is in progress.
    /// </summary>
    void CancelAuthentication();

    /// <summary>
    /// Revokes the current authentication
    /// </summary>
    Task<bool> RevokeAsync();

    /// <summary>
    /// Refreshes the current access token if possible
    /// </summary>
    Task<bool> RefreshTokenAsync();

    /// <summary>
    /// Gets a Drive service client authenticated with the current credentials
    /// </summary>
    Google.Apis.Drive.v3.DriveService? GetDriveService();

    /// <summary>
    /// Event raised when authentication state changes
    /// </summary>
    event AuthStateChangedEventHandler? AuthStateChanged;
}

/// <summary>
/// Delegate for authentication state changed events
/// </summary>
public delegate void AuthStateChangedEventHandler(bool isAuthenticated, string? userEmail);

