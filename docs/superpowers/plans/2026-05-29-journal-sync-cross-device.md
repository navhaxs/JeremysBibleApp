# Journal Sync Cross-Device Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix cross-device journal sync by making the auto-sync loop pull from Drive (not just push), repairing the no-op reconnect handler, updating the cached modifiedTime after journal pushes, enriching the CLI demo with journal inspection, and adding journal diagnostics to the in-app debug UI.

**Architecture:** `SyncCoordinator.AutoSyncLoop` replaces its manual queue-drain loop with a single call to the existing `PullFromDriveAsync()`, which already pulls journals if changed, merges, and drains the queue. `OnNetworkConnectivityChanged` similarly delegates to `PullFromDriveAsync`. `SyncJournalDataAsync` updates the cached Drive modifiedTime after each push so subsequent pulls avoid redundant downloads.

**Tech Stack:** C# / .NET (NSubstitute + xUnit for tests, Avalonia for UI)

---

## File Map

| File | Change |
|---|---|
| `MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs` | Fix `AutoSyncLoop`, `OnNetworkConnectivityChanged`, `SyncJournalDataAsync` |
| `MyBibleApp.Sync.Tests/SyncCoordinatorTests.cs` | Add tests for reconnect pull and journal mod-time caching |
| `MyBibleApp.Sync.Demo/Program.cs` | Add journal display to option 8, storage keys to option 6, new option J |
| `MyBibleApp/ViewModels/AppViewModel.cs` | Add journal info + cached mod times + `SyncJournalNowAsync` to debug data |
| `MyBibleApp/Views/SyncDebugView.axaml` | Add "Sync Journal Now" button |
| `MyBibleApp/Views/SyncDebugView.axaml.cs` | Wire button to `vm.SyncJournalNowAsync()` |

---

## Task 1: Fix AutoSyncLoop — pull instead of push-only

**Files:**
- Modify: `MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs`

- [ ] **Step 1: Replace AutoSyncLoop body**

  In `SyncCoordinator.cs`, replace the entire `AutoSyncLoop` method (lines ~503–541) with:

  ```csharp
  private async Task AutoSyncLoop(TimeSpan interval, CancellationToken cancellationToken)
  {
      while (!cancellationToken.IsCancellationRequested)
      {
          try
          {
              await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

              if (_isOffline || !_authService.IsAuthenticated)
                  continue;

              await PullFromDriveAsync().ConfigureAwait(false);
          }
          catch (TaskCanceledException)
          {
              break;
          }
          catch (Exception ex)
          {
              System.Diagnostics.Debug.WriteLine($"Error in auto-sync loop: {ex.Message}");
          }
      }
  }
  ```

- [ ] **Step 2: Build to confirm no compile errors**

  ```
  dotnet build MyBibleApp.Sync/MyBibleApp.Sync.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

  ```bash
  git add MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs
  git commit -m "fix: AutoSyncLoop calls PullFromDriveAsync instead of push-only queue drain"
  ```

---

## Task 2: Fix OnNetworkConnectivityChanged — call PullFromDriveAsync on reconnect

**Files:**
- Modify: `MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs`
- Test: `MyBibleApp.Sync.Tests/SyncCoordinatorTests.cs`

- [ ] **Step 1: Write the failing test**

  Add to `SyncCoordinatorTests.cs`:

  ```csharp
  [Fact]
  public async Task OnNetworkReconnect_WhenAuthenticated_CallsPullFromDrive()
  {
      _authService.IsAuthenticated.Returns(true);
      _syncService.GetFileModifiedTimesAsync()
          .Returns(new Dictionary<string, DateTime?>());
      _queueManager.GetPendingOperationsAsync()
          .Returns(new List<SyncQueueItem>());

      // Simulate going offline then online
      _networkMonitor.ConnectivityChanged += Raise.Event<Action<bool>>(false);
      _networkMonitor.ConnectivityChanged += Raise.Event<Action<bool>>(true);

      // Allow the Task.Delay(500) + async work to complete
      await Task.Delay(1200);

      await _syncService.Received().GetFileModifiedTimesAsync();
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  ```
  dotnet test MyBibleApp.Sync.Tests --filter "OnNetworkReconnect_WhenAuthenticated_CallsPullFromDrive" -v
  ```

  Expected: FAIL — `GetFileModifiedTimesAsync` was not called (old code calls the no-op `SyncAllAsync`).

- [ ] **Step 3: Replace OnNetworkConnectivityChanged**

  In `SyncCoordinator.cs`, replace the entire `OnNetworkConnectivityChanged` method with:

  ```csharp
  private void OnNetworkConnectivityChanged(bool isConnected)
  {
      _isOffline = !isConnected;

      if (isConnected && _authService.IsAuthenticated)
      {
          _ = Task.Run(async () =>
          {
              await Task.Delay(500).ConfigureAwait(false); // Brief delay for network stabilization
              await PullFromDriveAsync().ConfigureAwait(false);
          });
      }
  }
  ```

- [ ] **Step 4: Run test to verify it passes**

  ```
  dotnet test MyBibleApp.Sync.Tests --filter "OnNetworkReconnect_WhenAuthenticated_CallsPullFromDrive" -v
  ```

  Expected: PASS.

- [ ] **Step 5: Run full test suite**

  ```
  dotnet test MyBibleApp.Sync.Tests -v
  ```

  Expected: All tests pass.

- [ ] **Step 6: Commit**

  ```bash
  git add MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs MyBibleApp.Sync.Tests/SyncCoordinatorTests.cs
  git commit -m "fix: OnNetworkConnectivityChanged calls PullFromDriveAsync instead of no-op SyncAllAsync"
  ```

---

## Task 3: Fix SyncJournalDataAsync — update cached modifiedTime after push

**Files:**
- Modify: `MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs`
- Test: `MyBibleApp.Sync.Tests/SyncCoordinatorTests.cs`

- [ ] **Step 1: Write the failing test**

  Add to `SyncCoordinatorTests.cs`:

  ```csharp
  [Fact]
  public async Task SyncJournalDataAsync_AfterSuccessfulPush_CachesNewModifiedTime()
  {
      _authService.IsAuthenticated.Returns(true);

      var journalProvider = Substitute.For<IJournalSyncProvider>();
      journalProvider.GetSnapshotJsonAsync().Returns("{\"journals\":[]}");
      journalProvider.MergeRemoteJsonAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

      _syncService.GetJournalDataAsync().Returns((string?)null);
      _syncService.SaveJournalDataAsync(Arg.Any<string>()).Returns(SyncResult.Success(1));

      var newModTime = new DateTime(2026, 5, 29, 10, 0, 0, DateTimeKind.Utc);
      _syncService.GetFileModifiedTimesAsync()
          .Returns(new Dictionary<string, DateTime?> { ["journals.json"] = newModTime });

      _coordinator.SetJournalSyncProvider(journalProvider);

      await _coordinator.SyncJournalDataAsync();

      await _localStorage.Received(1).SaveAsync(
          "DriveModTime_journals.json",
          newModTime.ToString("O"));
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  ```
  dotnet test MyBibleApp.Sync.Tests --filter "SyncJournalDataAsync_AfterSuccessfulPush_CachesNewModifiedTime" -v
  ```

  Expected: FAIL — `SaveAsync("DriveModTime_journals.json", ...)` was not called.

- [ ] **Step 3: Update SyncJournalDataAsync to cache modifiedTime after push**

  In `SyncCoordinator.cs`, modify `SyncJournalDataAsync`. Replace:

  ```csharp
  var localJson = await _journalSyncProvider.GetSnapshotJsonAsync().ConfigureAwait(false);
  var pushResult = await _syncService.SaveJournalDataAsync(localJson).ConfigureAwait(false);

  return pushResult;
  ```

  With:

  ```csharp
  var localJson = await _journalSyncProvider.GetSnapshotJsonAsync().ConfigureAwait(false);
  var pushResult = await _syncService.SaveJournalDataAsync(localJson).ConfigureAwait(false);

  if (pushResult.IsSuccess)
  {
      var updatedTimes = await _syncService.GetFileModifiedTimesAsync().ConfigureAwait(false);
      if (updatedTimes.TryGetValue("journals.json", out var newModTime) && newModTime.HasValue)
          await SaveCachedModifiedTimeAsync("journals.json", newModTime.Value).ConfigureAwait(false);
  }

  return pushResult;
  ```

- [ ] **Step 4: Run test to verify it passes**

  ```
  dotnet test MyBibleApp.Sync.Tests --filter "SyncJournalDataAsync_AfterSuccessfulPush_CachesNewModifiedTime" -v
  ```

  Expected: PASS.

- [ ] **Step 5: Run full test suite**

  ```
  dotnet test MyBibleApp.Sync.Tests -v
  ```

  Expected: All tests pass.

- [ ] **Step 6: Commit**

  ```bash
  git add MyBibleApp.Sync/Services/Sync/SyncCoordinator.cs MyBibleApp.Sync.Tests/SyncCoordinatorTests.cs
  git commit -m "fix: SyncJournalDataAsync caches Drive modifiedTime after push to avoid redundant re-downloads"
  ```

---

## Task 4: CLI Demo — enrich option 6 and option 8 with journal/storage data

**Files:**
- Modify: `MyBibleApp.Sync.Demo/Program.cs`

- [ ] **Step 1: Add BibleReadingProgress and cached mod times to DoShowQueueAndStorage (option 6)**

  In `Program.cs`, find `DoShowQueueAndStorage`. After the existing three `_localStorage.GetAsync` lines (user, progress, prefs), append:

  ```csharp
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
  ```

- [ ] **Step 2: Add journal data to DoFetchRemoteData (option 8)**

  In `DoFetchRemoteData`, after the Annotations block and before the Preferences block, insert:

  ```csharp
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
                  var name = hasMeta && meta.TryGetProperty("name", out var np) ? np.GetString() : "?";
                  var modified = hasMeta && meta.TryGetProperty("lastModifiedUtc", out var mp) ? mp.GetString() : "?";
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
  ```

- [ ] **Step 3: Build**

  ```
  dotnet build MyBibleApp.Sync.Demo/MyBibleApp.Sync.Demo.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

  ```bash
  git add MyBibleApp.Sync.Demo/Program.cs
  git commit -m "feat(demo): show BibleReadingProgress, cached mod times, and remote journal data"
  ```

---

## Task 5: CLI Demo — add option J for raw journal JSON inspection

**Files:**
- Modify: `MyBibleApp.Sync.Demo/Program.cs`

- [ ] **Step 1: Add the DoInspectJournalJson method**

  Add the following method to `Program.cs` (before `PrintHeader`):

  ```csharp
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
  ```

- [ ] **Step 2: Add J to the menu and switch**

  In `PrintMenu`, replace the existing menu literal with one that includes option J. Find the menu string in `PrintMenu()` and add the J line:

  ```csharp
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
  ```

  In `RunMenuLoop`, inside the switch, add the J case before the default:

  ```csharp
  case ConsoleKey.J:
      await DoInspectJournalJson();
      break;
  ```

- [ ] **Step 3: Build**

  ```
  dotnet build MyBibleApp.Sync.Demo/MyBibleApp.Sync.Demo.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

  ```bash
  git add MyBibleApp.Sync.Demo/Program.cs
  git commit -m "feat(demo): add option J to inspect raw remote journals.json"
  ```

---

## Task 6: AppViewModel — add journal diagnostics to RefreshSyncDebugDataAsync

**Files:**
- Modify: `MyBibleApp/ViewModels/AppViewModel.cs`

- [ ] **Step 1: Add cached mod times section**

  In `AppViewModel.cs`, in `RefreshSyncDebugDataAsync`, after the existing `--- Local Sync Data ---` block and before the `--- Remote Sync Data ---` block, insert:

  ```csharp
  if (_localStorageProvider != null)
  {
      try
      {
          var journalModTime = await _localStorageProvider.GetAsync("DriveModTime_journals.json").ConfigureAwait(false);
          var userDataModTime = await _localStorageProvider.GetAsync("DriveModTime_user_data.json").ConfigureAwait(false);
          lines.Add("--- Cached Drive Mod Times ---");
          lines.Add($"journals.json:   {(string.IsNullOrWhiteSpace(journalModTime) ? "(none)" : journalModTime)}");
          lines.Add($"user_data.json:  {(string.IsNullOrWhiteSpace(userDataModTime) ? "(none)" : userDataModTime)}");
      }
      catch (Exception ex)
      {
          lines.Add($"Cached mod time read error: {ex.Message}");
      }
  }
  ```

- [ ] **Step 2: Add remote journal data to the Remote Sync Data section**

  In `RefreshSyncDebugDataAsync`, inside the existing `if (_googleDriveSyncService != null && IsAuthenticated)` block, after the existing three `lines.Add` calls, append:

  ```csharp
  try
  {
      var journalJson = await _googleDriveSyncService.GetJournalDataAsync().ConfigureAwait(false);
      if (!string.IsNullOrEmpty(journalJson))
      {
          using var doc = System.Text.Json.JsonDocument.Parse(journalJson);
          if (doc.RootElement.TryGetProperty("journals", out var arr)
              && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
          {
              lines.Add($"Remote Journals: {arr.GetArrayLength()} journal(s)  ({journalJson.Length:N0} bytes)");
              var n = 0;
              foreach (var j in arr.EnumerateArray())
              {
                  if (n++ >= 3) break;
                  var hasMeta = j.TryGetProperty("metadata", out var meta);
                  var name = hasMeta && meta.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                  var modified = hasMeta && meta.TryGetProperty("lastModifiedUtc", out var mp) ? mp.GetString() ?? "?" : "?";
                  var shortName = name.Length > 28 ? name[..28] + "…" : name;
                  var shortMod = modified.Length > 20 ? modified[..20] : modified;
                  lines.Add($"  · \"{shortName}\"  {shortMod}");
              }
              if (arr.GetArrayLength() > 3)
                  lines.Add($"  … and {arr.GetArrayLength() - 3} more");
          }
          else
          {
              lines.Add($"Remote Journals: (non-standard format, {journalJson.Length:N0} bytes)");
          }
      }
      else
      {
          lines.Add("Remote Journals: (none on Drive)");
      }
  }
  catch (Exception ex)
  {
      lines.Add($"Remote journal read error: {ex.Message}");
  }
  ```

- [ ] **Step 3: Add SyncJournalNowAsync method**

  After the existing `ForceSync` method in `AppViewModel.cs`, add:

  ```csharp
  public async Task SyncJournalNowAsync()
  {
      if (_syncCoordinator == null || !IsAuthenticated)
      {
          AppendSyncDebugLog("Cannot sync journal: not authenticated.");
          return;
      }
      AppendSyncDebugLog("Manual journal sync requested.");
      var result = await _syncCoordinator.SyncJournalDataAsync().ConfigureAwait(false);
      AppendSyncDebugLog(result.IsSuccess
          ? $"Journal sync succeeded ({result.ItemsSynced} item(s))."
          : $"Journal sync failed: {result.ErrorMessage}");
      await RefreshSyncDebugDataAsync().ConfigureAwait(false);
  }
  ```

- [ ] **Step 4: Build**

  ```
  dotnet build MyBibleApp/MyBibleApp.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

  ```bash
  git add MyBibleApp/ViewModels/AppViewModel.cs
  git commit -m "feat: add journal diagnostics and SyncJournalNowAsync to AppViewModel debug data"
  ```

---

## Task 7: SyncDebugView — add Sync Journal Now button

**Files:**
- Modify: `MyBibleApp/Views/SyncDebugView.axaml`
- Modify: `MyBibleApp/Views/SyncDebugView.axaml.cs`

- [ ] **Step 1: Add button to AXAML**

  In `SyncDebugView.axaml`, in the `WrapPanel` (Grid.Row="1"), add after the existing "Sync Now" button:

  ```xml
  <Button Content="Sync Journal Now" MinWidth="140" Margin="0,0,8,4" Click="OnSyncJournalNowClick" IsEnabled="{Binding CanForceSync}" />
  ```

- [ ] **Step 2: Wire button in code-behind**

  In `SyncDebugView.axaml.cs`, add after `OnSyncNowClick`:

  ```csharp
  private void OnSyncJournalNowClick(object? sender, RoutedEventArgs e)
  {
      if (DataContext is AppViewModel vm)
          _ = vm.SyncJournalNowAsync();
  }
  ```

- [ ] **Step 3: Build**

  ```
  dotnet build MyBibleApp/MyBibleApp.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run full test suite to confirm nothing broken**

  ```
  dotnet test MyBibleApp.Sync.Tests -v
  ```

  Expected: All tests pass.

- [ ] **Step 5: Commit**

  ```bash
  git add MyBibleApp/Views/SyncDebugView.axaml MyBibleApp/Views/SyncDebugView.axaml.cs
  git commit -m "feat: add Sync Journal Now button to SyncDebugView"
  ```

---

## Self-Review

**Spec coverage:**
- ✅ Bug 1 (AutoSyncLoop push-only) — Task 1
- ✅ Bug 2 (OnNetworkConnectivityChanged no-op) — Task 2
- ✅ Bug 3 (SyncJournalDataAsync cached time) — Task 3
- ✅ CLI option 6: BibleReadingProgress + cached mod times — Task 4
- ✅ CLI option 8: remote journal data — Task 4
- ✅ CLI option J: raw JSON inspection — Task 5
- ✅ Debug UI: cached mod times — Task 6
- ✅ Debug UI: remote journal count/names — Task 6
- ✅ Debug UI: Sync Journal Now button — Task 7

**Placeholder scan:** None found.

**Type consistency:** `SyncJournalNowAsync` defined in Task 6, called in Task 7 ✓. `GetJournalDataAsync` is on `IGoogleDriveSyncService` ✓. `SaveCachedModifiedTimeAsync` is private on `SyncCoordinator` and accessible from `SyncJournalDataAsync` ✓.
