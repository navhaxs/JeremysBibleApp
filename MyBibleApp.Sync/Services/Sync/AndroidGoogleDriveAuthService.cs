using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Avalonia.Platform;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Android-specific implementation of Google Drive authentication using OAuth 2.0.
///
/// Flow:
///   1. Reads credentials from the embedded APK asset.
///   2. Builds the Google authorisation URL and opens it via
///      <see cref="AndroidOAuthCallbackBridge.LaunchUri"/> (an ACTION_VIEW Intent
///      registered by <c>MainActivity</c>).
///   3. Waits for back-redirect to the custom scheme
///      <c>com.companyname.mybibleapp:/oauth2redirect</c>.
///   4. Exchanges the auth code for tokens via <see cref="GoogleAuthorizationCodeFlow"/>.
///
/// Prerequisites
///   • <c>com.companyname.mybibleapp:/oauth2redirect</c> must be listed as an
///     authorised redirect URI in the Google Cloud Console OAuth 2.0 credential.
///   • <c>MainActivity</c> must register an intent-filter for the same scheme and
///     call <see cref="AndroidOAuthCallbackBridge.TryHandleCallback"/> from
///     <c>OnNewIntent</c>.
///   • <see cref="AndroidOAuthCallbackBridge.LaunchUri"/> must be set before
///     <see cref="AuthenticateAsync"/> is called (typically in
///     <c>MainActivity.OnCreate</c>).
/// </summary>
public class AndroidGoogleDriveAuthService : IGoogleDriveAuthService
{
    // ─── Constants ────────────────────────────────────────────────────────────
    private const string ApplicationName     = "MyBibleApp";
    private const string CredentialsAssetPath = "avares://MyBibleApp/Assets/credentials.android.json";
    private const string TokenStoreFolderName = "token_cache_appdata";

    private static string TokenStorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TokenStoreFolderName);

    /// <summary>
    /// Derives the OAuth redirect URI from the client ID using Google's reversed-client-ID
    /// scheme: "com.googleusercontent.apps.{clientId}:/"
    ///
    /// This must match:
    ///   1. The DataScheme registered in MainActivity's [IntentFilter].
    ///   2. The package/SHA-1 Android credential in Google Cloud Console.
    ///
    /// For client ID "123-abc.apps.googleusercontent.com" the result is:
    ///   "com.googleusercontent.apps.123-abc:/"
    /// </summary>
    public static string GetRedirectUri(string clientId)
    {
        const string suffix = ".apps.googleusercontent.com";
        var prefix = clientId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? clientId[..^suffix.Length]
            : clientId;
        return $"com.googleusercontent.apps.{prefix}:/";
    }

    // ─── State ────────────────────────────────────────────────────────────────
    private UserCredential? _credential;
    private string?         _currentUserEmail;
    private string?         _currentAccessToken;
    private CancellationTokenSource? _interactiveCts;

    // ─── IGoogleDriveAuthService ───────────────────────────────────────────────
    /// <summary>
    /// True if we have a credential that can produce a valid access token — either the current
    /// token is still fresh, or we hold a refresh token that allows silent renewal.
    /// The DriveService returned by <see cref="GetDriveService"/> auto-refreshes stale tokens.
    /// </summary>
    public bool    IsAuthenticated    => _credential?.Token != null &&
        (!_credential.Token.IsStale || !string.IsNullOrEmpty(_credential.Token.RefreshToken));
    public string? CurrentAccessToken => _currentAccessToken;
    public string? CurrentUserEmail   => _currentUserEmail;

    public event AuthStateChangedEventHandler? AuthStateChanged;

    // ─── AuthenticateAsync ────────────────────────────────────────────────────
    public Task<AuthenticationResult> TrySilentAuthAsync() => AuthenticateInternalAsync(silentOnly: true);

    public Task<AuthenticationResult> AuthenticateAsync() => AuthenticateInternalAsync(silentOnly: false);

    public void CancelAuthentication()
    {
        _interactiveCts?.Cancel();
        AndroidOAuthCallbackBridge.TryHandleCallback(string.Empty);
    }

    public void ReopenBrowser() { } // Android uses system browser via intent; reopen is not supported

    private async Task<AuthenticationResult> AuthenticateInternalAsync(bool silentOnly)
    {
        System.Diagnostics.Debug.WriteLine("[AndroidAuth] AuthenticateAsync called.");
        System.Diagnostics.Debug.WriteLine($"[AndroidAuth] TokenStorePath={TokenStorePath}");
        try
        {
            // 1. Load credentials from the embedded APK asset.
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Loading credentials from: {CredentialsAssetPath}");
            ClientSecrets clientSecrets;
            try
            {
                await using var stream = AssetLoader.Open(new Uri(CredentialsAssetPath));
                clientSecrets = GoogleClientSecrets.FromStream(stream).Secrets;
                System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Credentials loaded. ClientId={clientSecrets.ClientId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Failed to load credentials: {ex}");
                return AuthenticationResult.Failure(
                    $"Failed to load credentials from APK asset '{CredentialsAssetPath}': {ex.Message}");
            }

            // Derive the redirect URI from the client ID.
            // Android credentials use the reversed-client-ID scheme:
            //   "com.googleusercontent.apps.{clientId}:/"
            // This must match the DataScheme in MainActivity's [IntentFilter].
            var redirectUri = GetRedirectUri(clientSecrets.ClientId);
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] RedirectUri={redirectUri}");

            // 2. Build the auth flow with persistent token store.
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Building auth flow. TokenStorePath={TokenStorePath}");
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = clientSecrets,
                Scopes        = new[] { DriveService.Scope.DriveAppdata, "openid", "email" },
                DataStore     = new FileDataStore(TokenStorePath, true)
            });

            // 3. Check for a cached, still-valid credential first.
            System.Diagnostics.Debug.WriteLine("[AndroidAuth] Checking for cached token...");
            var existingToken = await flow.LoadTokenAsync("user", CancellationToken.None)
                                          .ConfigureAwait(false);
            if (existingToken is { IsStale: false })
            {
                System.Diagnostics.Debug.WriteLine("[AndroidAuth] Found valid cached token – using it.");
                _credential = new UserCredential(flow, "user", existingToken);
                return CompleteAuthentication();
            }

            // Try a silent refresh before the full browser flow.
            if (existingToken?.RefreshToken != null)
            {
                System.Diagnostics.Debug.WriteLine("[AndroidAuth] Cached token is stale – attempting silent refresh.");
                try
                {
                    existingToken = await flow.RefreshTokenAsync(
                        "user", existingToken.RefreshToken, CancellationToken.None)
                        .ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine("[AndroidAuth] Silent refresh succeeded.");
                    _credential = new UserCredential(flow, "user", existingToken);
                    return CompleteAuthentication();
                }
                catch (Exception refreshEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Silent refresh failed: {refreshEx.Message} – falling through to browser flow.");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[AndroidAuth] No cached/refresh token – full interactive flow required.");
            }

            // 4. Full interactive OAuth with PKCE (required for Android credentials – no client_secret).
            if (silentOnly)
            {
                System.Diagnostics.Debug.WriteLine("[AndroidAuth] Silent-only mode — skipping interactive flow.");
                return AuthenticationResult.Failure("No cached credentials available.");
            }

            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] LaunchUri is {(AndroidOAuthCallbackBridge.LaunchUri == null ? "NULL – aborting" : "set – proceeding")}.");
            if (AndroidOAuthCallbackBridge.LaunchUri == null)
            {
                return AuthenticationResult.Failure(
                    "AndroidOAuthCallbackBridge.LaunchUri is not set. " +
                    "Ensure MainActivity sets AndroidOAuthCallbackBridge.LaunchUri before calling AuthenticateAsync.");
            }

            var (codeVerifier, codeChallenge) = GeneratePkce();
            var authUrl = BuildAuthorizationUrl(clientSecrets.ClientId, redirectUri, codeChallenge);
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Opening consent page (PKCE): {authUrl}");

            _interactiveCts?.Cancel();
            _interactiveCts?.Dispose();
            _interactiveCts = new CancellationTokenSource();
            var interactiveCt = _interactiveCts.Token;

            await AndroidOAuthCallbackBridge.LaunchUri(authUrl).ConfigureAwait(false);

            string? callbackUri;
            try
            {
                callbackUri = await AndroidOAuthCallbackBridge
                                        .WaitForCallbackAsync(interactiveCt)
                                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return AuthenticationResult.Failure("Authentication was cancelled.");
            }

            if (string.IsNullOrEmpty(callbackUri))
                return AuthenticationResult.Failure("Authentication was cancelled.");

            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Received callback: {callbackUri}");

            var code = ParseAuthorizationCode(callbackUri);
            if (code == null)
                return AuthenticationResult.Failure($"No authorization code in callback URI: {callbackUri}");

            // Exchange code for tokens, including the PKCE code_verifier.
            var tokenResponse = await ExchangeCodeWithPkceAsync(
                clientSecrets.ClientId, code, redirectUri, codeVerifier, CancellationToken.None)
                .ConfigureAwait(false);

            // Persist token via the flow's data store so it survives app restarts.
            await flow.DataStore.StoreAsync("user", tokenResponse).ConfigureAwait(false);

            _credential = new UserCredential(flow, "user", tokenResponse);
            System.Diagnostics.Debug.WriteLine("[AndroidAuth] Interactive PKCE OAuth completed successfully.");
            return CompleteAuthentication();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Authentication error: {ex}");
            return AuthenticationResult.Failure($"Android authentication failed: {ex.Message}");
        }
    }

    // ─── RevokeAsync ─────────────────────────────────────────────────────────
    public async Task<bool> RevokeAsync()
    {
        try
        {
            if (_credential != null)
                await _credential.RevokeTokenAsync(CancellationToken.None).ConfigureAwait(false);

            _credential         = null;
            _currentAccessToken = null;
            _currentUserEmail   = null;

            if (Directory.Exists(TokenStorePath))
                Directory.Delete(TokenStorePath, true);

            AuthStateChanged?.Invoke(false, null);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Revoke error: {ex}");
            return false;
        }
    }

    // ─── RefreshTokenAsync ────────────────────────────────────────────────────
    public async Task<bool> RefreshTokenAsync()
    {
        try
        {
            if (_credential == null)
                return false;

            await _credential.RefreshTokenAsync(CancellationToken.None).ConfigureAwait(false);
            _currentAccessToken = _credential.Token.AccessToken;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Token refresh error: {ex}");
            return false;
        }
    }

    // ─── GetDriveService ─────────────────────────────────────────────────────
    /// <summary>
    /// Gets a Drive service client authenticated with the current credentials.
    /// <see cref="UserCredential"/> automatically refreshes stale access tokens before each
    /// request, so this is safe to call even when the cached access token has expired.
    /// </summary>
    public DriveService? GetDriveService()
    {
        if (_credential == null)
            return null;

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName       = ApplicationName
        });
    }

    // ─── Private helpers ──────────────────────────────────────────────────────
    private AuthenticationResult CompleteAuthentication()
    {
        _currentAccessToken = _credential!.Token.AccessToken;
        _currentUserEmail   = ExtractEmailFromIdToken(_credential.Token.IdToken) ?? "Unknown User";
        AuthStateChanged?.Invoke(true, _currentUserEmail);
        return AuthenticationResult.Success(_currentAccessToken, _currentUserEmail);
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
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── PKCE helpers ─────────────────────────────────────────────────────────

    private static (string codeVerifier, string codeChallenge) GeneratePkce()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var codeVerifier  = Base64UrlEncode(bytes);
        var challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeHash);
        return (codeVerifier, codeChallenge);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildAuthorizationUrl(string clientId, string redirectUri, string codeChallenge)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"]             = clientId,
            ["redirect_uri"]          = redirectUri,
            ["response_type"]         = "code",
            ["scope"]                 = DriveService.Scope.DriveAppdata,
            ["code_challenge"]        = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["access_type"]           = "offline",
            ["prompt"]                = "consent"  // ensures refresh_token is returned
        };
        var qs = string.Join("&", parameters.Select(
            kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"https://accounts.google.com/o/oauth2/v2/auth?{qs}";
    }

    private static string? ParseAuthorizationCode(string callbackUri)
    {
        try
        {
            var uri   = new Uri(callbackUri);
            var query = HttpUtility.ParseQueryString(uri.Query.TrimStart('?'));
            var error = query["error"];
            if (!string.IsNullOrEmpty(error))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AndroidAuth] OAuth error in callback: {error} – {query["error_description"]}");
                return null;
            }
            return query["code"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Failed to parse callback URI: {ex.Message}");
            return null;
        }
    }

    private static async Task<TokenResponse> ExchangeCodeWithPkceAsync(
        string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        using var http    = new HttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"]          = code,
            ["client_id"]     = clientId,
            ["redirect_uri"]  = redirectUri,
            ["grant_type"]    = "authorization_code",
            ["code_verifier"] = codeVerifier
        });

        var response = await http.PostAsync("https://oauth2.googleapis.com/token", content, ct)
                                 .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        System.Diagnostics.Debug.WriteLine($"[AndroidAuth] Token exchange ({response.StatusCode}): {json}");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({response.StatusCode}): {json}");

        var tokenResponse = Google.Apis.Json.NewtonsoftJsonSerializer.Instance
                                  .Deserialize<TokenResponse>(json);
        tokenResponse.IssuedUtc = DateTime.UtcNow;
        return tokenResponse;
    }
}
