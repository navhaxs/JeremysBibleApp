using System;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Desktop implementation of Google Drive authentication using OAuth 2.0
/// </summary>
public class DesktopGoogleDriveAuthService : IGoogleDriveAuthService
{
    private const string ApplicationName = "MyBibleApp";
    private const string CredentialsFileName = "credentials.json";
    private const string TokenStorePath = "token_cache_appdata";

    private UserCredential? _credential;
    private string? _currentUserEmail;
    private string? _currentAccessToken;

    public bool IsAuthenticated => _credential?.Token is { IsStale: false };

    public string? CurrentAccessToken => _currentAccessToken;

    public string? CurrentUserEmail => _currentUserEmail;

    public event AuthStateChangedEventHandler? AuthStateChanged;

    /// <summary>
    /// Initializes a new instance of the DesktopGoogleDriveAuthService
    /// </summary>
    /// <param name="credentialsFilePath">Path to the OAuth 2.0 credentials JSON file</param>
    public DesktopGoogleDriveAuthService(string credentialsFilePath = CredentialsFileName)
    {
        _credentialsFilePath = credentialsFilePath;
    }

    /// <summary>
    /// Initializes a new instance using a credentials stream factory.
    /// Useful when credentials are embedded as an application resource (e.g. Avalonia asset)
    /// rather than a file on disk.
    /// </summary>
    /// <param name="credentialsStreamFactory">Factory returning a readable stream over the OAuth 2.0 credentials JSON.</param>
    public DesktopGoogleDriveAuthService(Func<Stream> credentialsStreamFactory)
    {
        _credentialsStreamFactory = credentialsStreamFactory;
        _credentialsFilePath = string.Empty;
    }

    private readonly string _credentialsFilePath;
    private readonly Func<Stream>? _credentialsStreamFactory;

    public async Task<AuthenticationResult> AuthenticateAsync()
    {
        try
        {
            Stream credentialsStream;
            if (_credentialsStreamFactory != null)
            {
                credentialsStream = _credentialsStreamFactory();
            }
            else
            {
                if (!File.Exists(_credentialsFilePath))
                {
                    return AuthenticationResult.Failure(
                        $"Credentials file not found at: {Path.GetFullPath(_credentialsFilePath)}. " +
                        "Please download the OAuth 2.0 credentials from Google Cloud Console.");
                }
                credentialsStream = File.OpenRead(_credentialsFilePath);
            }

            await using var _ = credentialsStream;
            using var reader = new StreamReader(credentialsStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var credentialsJson = await reader.ReadToEndAsync().ConfigureAwait(false);

            var validationError = ValidateDesktopCredentials(credentialsJson);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return AuthenticationResult.Failure(validationError);
            }

            await using var parsedCredentialsStream = new MemoryStream(Encoding.UTF8.GetBytes(credentialsJson));
            var clientSecrets = GoogleClientSecrets.FromStream(parsedCredentialsStream).Secrets;

            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets,
                new[] { DriveService.Scope.DriveAppdata },
                "user",
                System.Threading.CancellationToken.None,
                new FileDataStore(TokenStorePath, true)
            ).ConfigureAwait(false);

            // If the cached access token is expired, refresh it now so IsAuthenticated
            // returns true immediately (Token.IsStale would remain true otherwise until
            // the first API call triggers an implicit refresh).
            if (_credential.Token.IsStale)
            {
                await _credential.RefreshTokenAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _currentAccessToken = _credential.Token.AccessToken;

            // Extract user email from the token or credential
            _currentUserEmail = _credential.UserId ?? "Unknown User";

            AuthStateChanged?.Invoke(true, _currentUserEmail);
            return AuthenticationResult.Success(_currentAccessToken, _currentUserEmail);
        }
        catch (Exception ex)
        {
            return AuthenticationResult.Failure($"Authentication failed: {ex.Message}");
        }
    }

    private static string? ValidateDesktopCredentials(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("installed", out var installed))
            {
                return "Credentials JSON is missing the 'installed' section. Download Desktop OAuth credentials from Google Cloud Console and replace credentials.json.";
            }

            if (!HasNonEmptyString(installed, "client_id"))
            {
                return "Credentials JSON is missing 'installed.client_id'.";
            }

            if (!HasNonEmptyString(installed, "client_secret"))
            {
                return "Credentials JSON is missing 'installed.client_secret'. This usually causes Google's 'invalid request' sign-in error. Re-download Desktop OAuth credentials and replace credentials.json.";
            }

            if (!installed.TryGetProperty("redirect_uris", out var redirectUris) ||
                redirectUris.ValueKind != JsonValueKind.Array ||
                redirectUris.GetArrayLength() == 0)
            {
                return "Credentials JSON is missing 'installed.redirect_uris'. Desktop OAuth requires loopback redirect URIs like 'http://localhost'.";
            }

            return null;
        }
        catch (JsonException)
        {
            return "Credentials JSON is invalid. Verify credentials.json is the original file downloaded from Google Cloud Console.";
        }
    }

    private static bool HasNonEmptyString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString());
    }

    public async Task<bool> RevokeAsync()
    {
        try
        {
            if (_credential != null)
            {
                await _credential.RevokeTokenAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            }

            _credential = null;
            _currentAccessToken = null;
            _currentUserEmail = null;

            // Clean up token cache
            if (Directory.Exists(TokenStorePath))
            {
                Directory.Delete(TokenStorePath, true);
            }

            AuthStateChanged?.Invoke(false, null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync()
    {
        try
        {
            if (_credential?.Token == null)
                return false;

            await _credential.RefreshTokenAsync(System.Threading.CancellationToken.None)
                .ConfigureAwait(false);

            _currentAccessToken = _credential.Token.AccessToken;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a Drive service client authenticated with the current credentials
    /// </summary>
    public DriveService? GetDriveService()
    {
        if (_credential == null || !IsAuthenticated)
            return null;

        return new DriveService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = _credential,
            ApplicationName = ApplicationName
        });
    }
}

