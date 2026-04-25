# Google Drive Sync - Getting Started

## Setup Checklist

### 1. Google Cloud Console (5 min)

- [ ] Go to https://console.cloud.google.com
- [ ] Create a new project (name: "MyBibleApp")
- [ ] Enable **Google Drive API** (APIs & Services → Library)
- [ ] Go to **APIs & Services → Credentials**
- [ ] Click **Create Credentials → OAuth client ID**
- [ ] Select **Desktop application**, click Create
- [ ] Download the JSON file, rename to `credentials.json`
- [ ] Copy to: `MyBibleApp/Assets/credentials.json`
- [ ] Also configure the **OAuth consent screen** (add `drive.appdata` scope)

### 2. Build & Run (2 min)

```powershell
dotnet build MyBibleApp.Desktop/MyBibleApp.Desktop.csproj
dotnet run --project MyBibleApp.Desktop/MyBibleApp.Desktop.csproj
```

### 3. Test

- Look for the **Sync** controls in the app UI
- Click **Authenticate** → browser opens for Google sign-in
- After login, status should show "Authenticated"
- Click **Sync Now** to trigger a manual sync
- Check `%APPDATA%\MyBibleApp\LocalStorage\` for local data files

---

## Debugging

Use the **Sync Debug** panel in the app (gear icon or Sync menu) to:
- View current sync status and progress log
- Inspect local data snapshot
- Clear remote Drive data (for testing)
- Force a manual sync

### Local storage location

`%APPDATA%\MyBibleApp\LocalStorage\` — plain JSON files, readable in any text editor.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Build errors | Verify all files exist in `MyBibleApp.Sync/Services/Sync/` |
| "Credentials file not found" | Put `credentials.json` in `MyBibleApp/Assets/` (not project root) |
| Authenticate button does nothing | Rebuild; check Windows Firewall; verify credentials JSON is valid |
| "Not authenticated" on sync | Click Authenticate first; credentials may have expired |
| Sync shows error | Check network; verify Drive API is enabled in Google Cloud |
| UI sync bar not visible | Check `AppShellView.axaml` includes `SyncControlView` |

---

## Expected Behaviour

| State | What happens |
|-------|-------------|
| First launch | Loads local data; syncs from Drive if authenticated |
| After auth | Auto-sync runs every 2 minutes in background; sync on exit |
| During sync | Progress indicator shows; buttons disabled |
| Offline | Operations queued to `sync_queue.json`; auto-resume on reconnect |
| App restart | Tokens persist in `token_cache_appdata/`; no re-auth needed |

---

## Platform Next Steps

- **Android** – see `ANDROID_SETUP.md`
- **iOS/Browser** – see `ARCHITECTURE.md` for platform notes
