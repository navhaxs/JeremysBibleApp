# Android Google Drive Authentication Setup

## How It Works

Android uses the same `GoogleWebAuthorizationBroker` OAuth 2.0 flow as Desktop. `MainViewModel` detects the platform at runtime via `PlatformHelper` and creates the appropriate auth service (`AndroidGoogleDriveAuthService`, `DesktopGoogleDriveAuthService`, or `iOSGoogleDriveAuthService`).

```
User taps Authenticate
  → MainViewModel.AuthenticateAsync()
  → PlatformHelper.IsAndroid == true
  → AndroidGoogleDriveAuthService.AuthenticateAsync()
  → GoogleWebAuthorizationBroker opens browser for sign-in
  → Token stored in token_cache_appdata/
  → IsAuthenticated = true
  → GoogleDriveSyncService.GetDriveService() uses stored credential
```

---

## Setup

### 1. Get OAuth Credentials from Google Cloud Console

1. Go to https://console.cloud.google.com
2. Select (or create) your project
3. **APIs & Services → Library** → enable **Google Drive API**
4. **APIs & Services → OAuth consent screen** → add `drive.appdata` scope
5. **APIs & Services → Credentials → Create Credentials → OAuth client ID**
6. Select **Android** as application type
7. Enter your package name (e.g. `com.CompanyName.MyBibleApp`)
8. Enter your SHA-1 signing certificate fingerprint (see below)
9. Download the JSON credentials file

### 2. Get Your SHA-1 Fingerprint

**Debug key (Windows):**
```powershell
keytool -list -v -keystore $env:APPDATA\.android\debug.keystore -alias androiddebugkey -storepass android -keypass android | findstr /I "SHA1"
```

**Release key:**
```powershell
keytool -list -v -keystore path\to\your.keystore -alias your_alias
```

### 3. Place credentials.json in the Correct Location

```
✅ CORRECT:  MyBibleApp/Assets/credentials.json
❌ WRONG:    MyBibleApp.Android/Resources/raw/credentials.json
```

**Why?** Avalonia bundles everything in `MyBibleApp/Assets/` into the APK as `AvaloniaResource` items, accessible via the `avares://` URI scheme. Files in `MyBibleApp.Android/Resources/raw/` are Android-specific and not reachable by cross-platform code.

```powershell
# Verify it's in the right place
Test-Path "C:\Users\Jeremy\RiderProjects\OpenBibleApp\MyBibleApp\Assets\credentials.json"
```

### 4. Rebuild and Deploy

After placing (or moving) `credentials.json`, you **must** do a clean rebuild so the asset is bundled into the APK:

```powershell
cd C:\Users\Jeremy\RiderProjects\OpenBibleApp
dotnet clean
dotnet build MyBibleApp.Android/MyBibleApp.Android.csproj -c Debug
```

Then deploy via Rider (**Run → Run MyBibleApp.Android**) or:
```powershell
dotnet publish MyBibleApp.Android/MyBibleApp.Android.csproj -c Debug -f net10.0-android
```

---

## Verifying It Works

Check the in-app **Sync Debug** view for these log messages:

```
Platform detected: Android
Authentication succeeded.
Sync services initialized.
```

Then verify:
- **Force Sync** completes without errors
- Token is persisted — restart the app and sync still works without re-authenticating

---

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| "Credentials file not found" | File in wrong location or missing | Move to `MyBibleApp/Assets/credentials.json` and rebuild |
| Nothing happens on Authenticate | Old APK deployed; file not bundled | `dotnet clean` then rebuild and redeploy |
| "Invalid client" / "Unauthorized" | Package name or SHA-1 fingerprint mismatch | Update Google Cloud Console; download new credentials; rebuild |
| Authenticate button stays greyed out | `AuthStateChanged` event not raised | Call `SyncAuthStateWithServiceAsync()`; set a breakpoint in `AndroidGoogleDriveAuthService` |
| "Failed to sync: Not authenticated" | Credentials missing or OAuth flow cancelled | Verify credentials path; click Authenticate again |

### Still not working? Checklist

```powershell
# 1. File exists?
Get-Item "C:\Users\Jeremy\RiderProjects\OpenBibleApp\MyBibleApp\Assets\credentials.json"

# 2. File is valid JSON?
Get-Content "...\MyBibleApp\Assets\credentials.json" -Raw | ConvertFrom-Json

# 3. Asset visible in build output?
Get-ChildItem -Recurse "...\MyBibleApp\bin\Debug\net10.0" | Where-Object { $_.Name -eq "credentials.json" }
```

View Android Logcat in Rider: **Tools → Device Manager → Logcat tab**  
Filter for `mybiblapp` or `D/` debug messages.

---

## Additional NuGet Packages (for future full Android background sync)

These are not required for the current `GoogleWebAuthorizationBroker` flow but will be needed when implementing WorkManager-based background sync:

```xml
<!-- MyBibleApp.Android.csproj -->
<PackageReference Include="Xamarin.Google.Android.GMS.Auth" Version="120.5.0.300" />
<PackageReference Include="Xamarin.AndroidX.Work.Work.Runtime" Version="2.9.1.1" />
```

Add corresponding versions to `Directory.Packages.props`.

For manifest permissions when adding background sync:
```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
```

See `PLATFORM_SETUP_GUIDE.md` for a WorkManager `SyncWorker` example and iOS/Browser setup.

