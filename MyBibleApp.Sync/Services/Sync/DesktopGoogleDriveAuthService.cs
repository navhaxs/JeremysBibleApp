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

    public Task<AuthenticationResult> TrySilentAuthAsync() => AuthenticateInternalAsync(silentOnly: true);

    public Task<AuthenticationResult> AuthenticateAsync() => AuthenticateInternalAsync(silentOnly: false);

    private async Task<AuthenticationResult> AuthenticateInternalAsync(bool silentOnly)
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
                return AuthenticationResult.Failure(validationError);

            await using var parsedCredentialsStream = new MemoryStream(Encoding.UTF8.GetBytes(credentialsJson));
            var clientSecrets = GoogleClientSecrets.FromStream(parsedCredentialsStream).Secrets;

            if (silentOnly)
            {
                // Silent path: check cache and refresh only — never open a browser.
                var flow = new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow(
                    new Google.Apis.Auth.OAuth2.Flows.GoogleAuthorizationCodeFlow.Initializer
                    {
                        ClientSecrets = clientSecrets,
                        Scopes        = new[] { DriveService.Scope.DriveAppdata },
                        DataStore     = new FileDataStore(TokenStorePath, true)
                    });

                var existingToken = await flow.LoadTokenAsync("user", System.Threading.CancellationToken.None)
                                              .ConfigureAwait(false);

                if (existingToken is { IsStale: false })
                {
                    _credential         = new UserCredential(flow, "user", existingToken);
                    _currentAccessToken = existingToken.AccessToken;
                    _currentUserEmail   = ExtractEmailFromIdToken(existingToken.IdToken) ?? "Unknown User";
                    AuthStateChanged?.Invoke(true, _currentUserEmail);
                    return AuthenticationResult.Success(_currentAccessToken, _currentUserEmail);
                }

                if (existingToken?.RefreshToken != null)
                {
                    try
                    {
                        var refreshed = await flow.RefreshTokenAsync(
                            "user", existingToken.RefreshToken, System.Threading.CancellationToken.None)
                            .ConfigureAwait(false);
                        _credential         = new UserCredential(flow, "user", refreshed);
                        _currentAccessToken = refreshed.AccessToken;
                        _currentUserEmail   = ExtractEmailFromIdToken(refreshed.IdToken) ?? "Unknown User";
                        AuthStateChanged?.Invoke(true, _currentUserEmail);
                        return AuthenticationResult.Success(_currentAccessToken, _currentUserEmail);
                    }
                    catch
                    {
                        // Refresh failed — treat as unauthenticated.
                    }
                }

                return AuthenticationResult.Failure("No cached credentials available.");
            }

            // Interactive path (full browser flow).
            await using var interactiveStream = new MemoryStream(Encoding.UTF8.GetBytes(credentialsJson));
            clientSecrets = GoogleClientSecrets.FromStream(interactiveStream).Secrets;

            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets,
                new[] { DriveService.Scope.DriveAppdata },
                "user",
                System.Threading.CancellationToken.None,
                new FileDataStore(TokenStorePath, true)
            ).ConfigureAwait(false);

            if (_credential.Token.IsStale)
            {
                await _credential.RefreshTokenAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _currentAccessToken = _credential.Token.AccessToken;
            _currentUserEmail   = ExtractEmailFromIdToken(_credential.Token.IdToken) ?? "Unknown User";
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
    /// Decodes the JWT ID token returned by Google and extracts the email claim.
    /// No signature verification is needed — we trust the token we just received from Google.
    /// </summary>
    private static string? ExtractEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            // Base64url → Base64 (add padding if needed)
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch
        {
            return null;
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

