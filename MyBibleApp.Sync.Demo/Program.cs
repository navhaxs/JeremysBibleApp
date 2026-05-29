using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;

namespace MyBibleApp.Sync.Demo;

/// <summary>
/// Interactive console demo for testing and debugging the sync flow
/// without launching the full Avalonia app.
///
/// Run with:  dotnet run --project MyBibleApp.Sync.Demo
/// </summary>
internal static class Program
{
    // Persistent data lives next to the executable so you can inspect files
    private static readonly string DataDir = Path.Combine(
        AppContext.BaseDirectory, "demo_sync_data");

    private static readonly string QueueDir = Path.Combine(DataDir, "queue");
    private static readonly string StorageDir = Path.Combine(DataDir, "storage");

    private static DesktopGoogleDriveAuthService _authService = null!;
    private static GoogleDriveSyncService _syncService = null!;
    private static FileSyncQueueManager _queueManager = null!;
    private static NetworkStatusMonitor _networkMonitor = null!;
    private static FileBasedLocalStorageProvider _localStorage = null!;
    private static SyncCoordinator _coordinator = null!;

    private static async Task Main()
    {
        Console.Title = "MyBibleApp Sync Demo";
        PrintHeader();

        InitializeServices();

        _coordinator.SyncProgress += (_, e) =>
        {
            var tag = e.IsError ? "ERR" : e.IsCompleted ? "OK " : "...";
            Console.ForegroundColor = e.IsError ? ConsoleColor.Red
                : e.IsCompleted ? ConsoleColor.Green : ConsoleColor.DarkCyan;
            Console.WriteLine($"  [{tag}] {e.Progress,3}% | {e.Message}");
            Console.ResetColor();
        };

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  Services initialized. Data stored in:");
        Console.ResetColor();
        Console.WriteLine($"    {DataDir}");
        Console.WriteLine();

        await RunMenuLoop();

        _coordinator.Dispose();
        Console.WriteLine("\nBye!");
    }

    // ────────────────────────────────────────────────────────────────
    //  Service bootstrap
    // ────────────────────────────────────────────────────────────────

    private static void InitializeServices()
    {
        Directory.CreateDirectory(QueueDir);
        Directory.CreateDirectory(StorageDir);

        var credentialsPath = Path.Combine(AppContext.BaseDirectory, "credentials.json");
        if (!File.Exists(credentialsPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  ⚠  credentials.json not found at:\n     {credentialsPath}");
            Console.WriteLine("     The demo copies MyBibleApp/Assets/credentials.desktop.json to this path during build.");
            Console.ResetColor();
        }

        _authService = new DesktopGoogleDriveAuthService(credentialsPath);
        _syncService = new GoogleDriveSyncService(_authService);
        _queueManager = new FileSyncQueueManager(QueueDir);
        _networkMonitor = new NetworkStatusMonitor();
        _localStorage = new FileBasedLocalStorageProvider(StorageDir);

        _coordinator = new SyncCoordinator(
            _authService, _syncService, _queueManager, _networkMonitor, _localStorage);
    }

    // ────────────────────────────────────────────────────────────────
    //  Menu loop
    // ────────────────────────────────────────────────────────────────

    private static async Task RunMenuLoop()
    {
        while (true)
        {
            PrintMenu();
            var key = Console.ReadKey(intercept: true).Key;
            Console.WriteLine();

            try
            {
                switch (key)
                {
                    case ConsoleKey.D1:
                    case ConsoleKey.NumPad1:
                        await DoAuthenticate();
                        break;

                    case ConsoleKey.D2:
                    case ConsoleKey.NumPad2:
                        await DoSyncReadingProgress();
                        break;

                    case ConsoleKey.D3:
                    case ConsoleKey.NumPad3:
                        await DoSyncAnnotation();
                        break;

                    case ConsoleKey.D4:
                    case ConsoleKey.NumPad4:
                        await DoSyncPreferences();
                        break;

                    case ConsoleKey.D5:
                    case ConsoleKey.NumPad5:
                        await DoShowStatus();
                        break;

                    case ConsoleKey.D6:
                    case ConsoleKey.NumPad6:
                        await DoShowQueueAndStorage();
                        break;

                    case ConsoleKey.D7:
                    case ConsoleKey.NumPad7:
                        DoForceSync();
                        break;

                    case ConsoleKey.D8:
                    case ConsoleKey.NumPad8:
                        await DoFetchRemoteData();
                        break;

                    case ConsoleKey.D9:
                    case ConsoleKey.NumPad9:
                        DoSignOut();
                        break;

                    case ConsoleKey.D0:
                    case ConsoleKey.NumPad0:
                        await DoClearAll();
                        break;

                    case ConsoleKey.J:
                        await DoInspectJournalJson();
                        break;

                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        return;

                    default:
                        Console.WriteLine("  Unknown option.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n  Exception: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"  Inner:     {ex.InnerException.Message}");
                Console.ResetColor();
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Menu actions
    // ────────────────────────────────────────────────────────────────

    private static async Task DoAuthenticate()
    {
        Console.WriteLine("  Authenticating… (a browser window should open)");
        var ok = await _coordinator.AuthenticateAsync();
        PrintResult(ok ? "Authenticated!" : "Authentication did not succeed.");
        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    User:  {_authService.CurrentUserEmail}");
            Console.WriteLine($"    Token: {Truncate(_authService.CurrentAccessToken, 40)}");
            Console.ResetColor();
        }
    }

    private static async Task DoSyncReadingProgress()
    {
        var book = Prompt("  Book code (e.g. JHN): ", "JHN");
        var chapter = int.Parse(Prompt("  Chapter: ", "3"));
        var verse = int.Parse(Prompt("  Verse:   ", "16"));

        Console.WriteLine($"  Syncing reading progress → {book} {chapter}:{verse}…");
        var result = await _coordinator.SyncReadingProgressAsync(book, chapter, verse);
        PrintSyncResult(result);
    }

    private static async Task DoSyncAnnotation()
    {
        var book = Prompt("  Book code (e.g. PSA): ", "PSA");
        var chapter = int.Parse(Prompt("  Chapter: ", "23"));
        var verse = int.Parse(Prompt("  Verse:   ", "1"));
        var notes = Prompt("  Notes:   ", "The Lord is my shepherd");

        var annotation = new AnnotationBundle
        {
            BookCode = book,
            Chapter = chapter,
            Verse = verse,
            Notes = notes,
            IsBookmarked = true
        };

        Console.WriteLine($"  Syncing annotation → {book} {chapter}:{verse}…");
        var result = await _coordinator.SyncAnnotationAsync(annotation);
        PrintSyncResult(result);
    }

    private static async Task DoSyncPreferences()
    {
        var theme = Prompt("  Theme (Light/Dark/Auto): ", "Dark");
        var fontSize = double.Parse(Prompt("  Font size: ", "18"));

        var prefs = new PreferencesSnapshot
        {
            Theme = theme,
            FontSize = fontSize,
            CustomSettings =
            {
                ["synced_from"] = "SyncDemo",
                ["synced_at"] = DateTime.UtcNow.ToString("O")
            }
        };

        Console.WriteLine("  Syncing preferences…");
        var result = await _coordinator.SyncPreferencesAsync(prefs);
        PrintSyncResult(result);
    }

    private static async Task DoShowStatus()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Status ──────────────────────────────────────");
        Console.ResetColor();

        Console.WriteLine($"    Authenticated:   {_authService.IsAuthenticated}");
        Console.WriteLine($"    User:            {_authService.CurrentUserEmail ?? "(none)"}");
        Console.WriteLine($"    Network:         {(_networkMonitor.IsConnected ? "Online" : "Offline")}");

        var status = _syncService.CurrentStatus;
        Console.WriteLine($"    Syncing:         {status.IsSyncing}");
        Console.WriteLine($"    Last sync:       {status.LastSyncTime?.ToString("O") ?? "Never"}");
        Console.WriteLine($"    Pending items:   {status.PendingItemsCount}");
        Console.WriteLine($"    Progress:        {status.ProgressPercentage}%");

        var queueCount = await _queueManager.GetPendingCountAsync();
        Console.WriteLine($"    Queue (local):   {queueCount} pending");
    }

    private static async Task DoShowQueueAndStorage()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Local Queue ─────────────────────────────────");
        Console.ResetColor();

        var pending = await _queueManager.GetPendingOperationsAsync();
        if (pending.Count == 0)
        {
            Console.WriteLine("    (empty)");
        }
        else
        {
            foreach (var item in pending)
            {
                Console.WriteLine($"    [{item.Id[..8]}] {item.OperationType,-20} queued {item.QueuedAt:HH:mm:ss}  synced={item.IsSynced}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"             {Truncate(item.Data.ToString(), 80)}");
                Console.ResetColor();
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Local Storage ───────────────────────────────");
        Console.ResetColor();

        var progress = await _localStorage.GetAsync("CurrentReadingProgress");
        var prefs = await _localStorage.GetAsync("UserPreferences");
        var user = await _localStorage.GetAsync("LastAuthenticatedUser");

        Console.WriteLine($"    LastAuthenticatedUser:    {user ?? "(none)"}");
        Console.WriteLine($"    CurrentReadingProgress:   {Truncate(progress, 80) ?? "(none)"}");
        Console.WriteLine($"    UserPreferences:          {Truncate(prefs, 80) ?? "(none)"}");

        var bibleProgress = await _localStorage.GetAsync("BibleReadingProgress");
        Console.WriteLine($"    BibleReadingProgress:     {Truncate(bibleProgress, 80) ?? "(none)"}");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Cached Drive Mod Times ──────────────────────────");
        Console.ResetColor();
        string[] modTimeKeys = ["DriveModTime_user_data.json", "DriveModTime_journals.json"];
        foreach (var key in modTimeKeys)
        {
            var val = await _localStorage.GetAsync(key);
            Console.WriteLine($"    {key}: {val ?? "(none)"}");
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n    Files on disk: {StorageDir}");
        if (Directory.Exists(StorageDir))
        {
            foreach (var f in Directory.GetFiles(StorageDir))
                Console.WriteLine($"      {Path.GetFileName(f)}  ({new FileInfo(f).Length} bytes)");
        }
        Console.ResetColor();
    }

    private static void DoForceSync()
    {
        Console.WriteLine("  Forcing sync of queued operations…");
        _coordinator.ForceSync();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ForceSync dispatched (runs in background). Check status in a moment.");
        Console.ResetColor();
    }

    private static async Task DoFetchRemoteData()
    {
        if (!_authService.IsAuthenticated)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Not authenticated — authenticate first (option 1).");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Remote Data (Google Drive appDataFolder) ────");
        Console.ResetColor();

        Console.Write("    User data… ");
        var userData = await _syncService.GetUserDataAsync();
        var remoteProgress = userData?.ReadingProgress;
        if (remoteProgress != null)
            Console.WriteLine($"found  ({remoteProgress.BookCode} {remoteProgress.Chapter}:{remoteProgress.Verse}  {remoteProgress.ProgressTimestamp:O})");
        else
            Console.WriteLine("(none)");

        Console.Write("    Annotations… ");
        var annotations = await _syncService.GetAllAnnotationsAsync();
        Console.WriteLine($"{annotations.Count} entries");
        foreach (var a in annotations.Take(5))
            Console.WriteLine($"      {a.BookCode} {a.Chapter}:{a.Verse}  notes=\"{Truncate(a.Notes, 40)}\"");
        if (annotations.Count > 5)
            Console.WriteLine($"      … and {annotations.Count - 5} more");

        Console.Write("    Journals… ");
        var journalJson = await _syncService.GetJournalDataAsync();
        if (string.IsNullOrEmpty(journalJson))
        {
            Console.WriteLine("(none)");
        }
        else
        {
            try
            {
                using var journalDoc = JsonDocument.Parse(journalJson);
                if (journalDoc.RootElement.TryGetProperty("journals", out var journalsArr)
                    && journalsArr.ValueKind == JsonValueKind.Array)
                {
                    Console.WriteLine($"{journalsArr.GetArrayLength()} journal(s)  ({journalJson.Length:N0} bytes)");
                    var shown = 0;
                    foreach (var j in journalsArr.EnumerateArray())
                    {
                        if (shown++ >= 5) break;
                        var hasMeta = j.TryGetProperty("metadata", out var meta);
                        var name = hasMeta && meta.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                        var modified = hasMeta && meta.TryGetProperty("lastModifiedUtc", out var mp) ? mp.GetString() ?? "?" : "?";
                        var strokeCount = j.TryGetProperty("inkStrokes", out var strokes) && strokes.ValueKind == JsonValueKind.Array
                            ? strokes.GetArrayLength() : 0;
                        Console.WriteLine($"      \"{Truncate(name, 36)}\"  modified={Truncate(modified, 20)}  strokes={strokeCount}");
                    }
                    if (journalsArr.GetArrayLength() > 5)
                        Console.WriteLine($"      … and {journalsArr.GetArrayLength() - 5} more (use J to inspect full JSON)");
                }
                else
                {
                    Console.WriteLine($"(non-standard JSON format, {journalJson.Length:N0} bytes — use J to inspect)");
                }
            }
            catch
            {
                Console.WriteLine($"(JSON parse error, {journalJson.Length:N0} bytes raw)");
            }
        }

        Console.Write("    Preferences… ");
        var remotePrefs = userData?.Preferences;
        if (remotePrefs != null)
        {
            Console.WriteLine("found");
            Console.WriteLine($"      Theme={remotePrefs.Theme}  FontSize={remotePrefs.FontSize}  Lang={remotePrefs.Language}");
            if (remotePrefs.CustomSettings.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var kv in remotePrefs.CustomSettings.Take(5))
                    Console.WriteLine($"      [{kv.Key}] = {Truncate(kv.Value, 60)}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.WriteLine("(none)");
        }
    }

    private static void DoSignOut()
    {
        if (!_authService.IsAuthenticated)
        {
            Console.WriteLine("  Not currently authenticated.");
            return;
        }

        _coordinator.SignOut();
        PrintResult("Signed out.");
    }

    private static async Task DoClearAll()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  Clear local queue + storage? (y/N): ");
        Console.ResetColor();
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (confirm != "y") return;

        await _queueManager.ClearQueueAsync();
        await _localStorage.ClearAsync();
        PrintResult("Local queue and storage cleared.");
    }

    private static async Task DoInspectJournalJson()
    {
        if (!_authService.IsAuthenticated)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Not authenticated — authenticate first (option 1).");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ── Remote journals.json ────────────────────────────");
        Console.ResetColor();

        var json = await _syncService.GetJournalDataAsync();
        if (string.IsNullOrEmpty(json))
        {
            Console.WriteLine("    (no journals.json on Drive)");
            return;
        }

        Console.WriteLine($"    Size: {json.Length:N0} bytes");
        Console.WriteLine();

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            var pretty = JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
            var lines = pretty.Split('\n');
            var limit = Math.Min(lines.Length, 120);
            for (var i = 0; i < limit; i++)
                Console.WriteLine("    " + lines[i]);
            if (lines.Length > 120)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    … ({lines.Length - 120} more lines — copy to file for full view)");
                Console.ResetColor();
            }
        }
        catch
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("    (not valid JSON — dumping raw)");
            Console.ResetColor();
            Console.WriteLine(Truncate(json, 3000));
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  UI helpers
    // ────────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("""
        
          ╔═══════════════════════════════════════════╗
          ║      MyBibleApp · Sync Flow Demo          ║
          ║  Test authentication & sync independently ║
          ╚═══════════════════════════════════════════╝
        """);
        Console.ResetColor();
    }

    private static void PrintMenu()
    {
        var auth = _authService.IsAuthenticated;
        var net = _networkMonitor.IsConnected;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  [{(auth ? "✓ Authenticated" : "✗ Not authenticated")}]  [{(net ? "Online" : "Offline")}]");
        Console.ResetColor();

        Console.WriteLine("""
          ──────────────────────────────────────
          1  Authenticate (Google OAuth)
          2  Sync reading progress
          3  Sync annotation
          4  Sync preferences
          5  Show status
          6  Show queue & local storage
          7  Force sync (drain queue)
          8  Fetch remote data from Drive
          9  Sign out
          0  Clear local queue & storage
          J  Inspect journals.json (raw)
          Q  Quit
          ──────────────────────────────────────
        """);
        Console.Write("  Choice: ");
    }

    private static void PrintSyncResult(SyncResult result)
    {
        if (result.IsSuccess)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ Success — {result.ItemsSynced} item(s) synced, {result.ConflictsResolved} conflict(s).");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✗ Failed — {result.ErrorMessage}");
        }
        Console.ResetColor();
    }

    private static void PrintResult(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✓ {message}");
        Console.ResetColor();
    }

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label}[{defaultValue}] ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value == null) return null;
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}

