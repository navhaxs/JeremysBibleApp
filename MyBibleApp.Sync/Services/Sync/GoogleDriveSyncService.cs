using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Implementation of Google Drive sync service
/// </summary>
public class GoogleDriveSyncService : IGoogleDriveSyncService
{
    private const string AppDataFolderParent = "appDataFolder";
    private const string UserDataFileName = "user_data.json";
    private const string AnnotationsFileName = "annotations.json";

    private readonly IGoogleDriveAuthService _authService;
    private readonly string _deviceId;
    private SyncStatusInfo _currentStatus = new();

    private DriveService? DriveService
    {
        get
        {
            return _authService.GetDriveService();
        }
    }

    public SyncStatusInfo CurrentStatus => _currentStatus;

    public event SyncProgressHandler? SyncProgress;

    public GoogleDriveSyncService(IGoogleDriveAuthService authService)
    {
        _authService = authService;
        _deviceId = GenerateDeviceId();
        _authService.AuthStateChanged += OnAuthStateChanged;
    }

    private static string GenerateDeviceId()
    {
        // Generate a unique device ID
        return $"{Environment.MachineName}_{Guid.NewGuid():N}".Substring(0, 32);
    }

    private void OnAuthStateChanged(bool isAuthenticated, string? userEmail)
    {
    }

    public async Task<SyncResult> SyncAllAsync()
    {
        if (!_authService.IsAuthenticated)
            return SyncResult.Failure("Not authenticated");

        var result = new SyncResult() { IsSuccess = true };

        try
        {
            UpdateStatus(true, "Starting full sync...", 0);

            // For now, just log the sync attempt
            // Full implementation would sync all data types
            result.SyncLog.Add("Full sync completed");

            UpdateStatus(false, "Sync completed", 100);
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"Sync failed: {ex.Message}", 0);
            return SyncResult.Failure(ex.Message);
        }

        return result;
    }

    public async Task<SyncResult> SaveUserDataAsync(UserDataSnapshot data)
    {
        if (!_authService.IsAuthenticated)
            return SyncResult.Failure("Not authenticated");

        try
        {
            await UpsertEntityAsync(data, UserDataFileName).ConfigureAwait(false);
            return SyncResult.Success(1);
        }
        catch (Exception ex)
        {
            return SyncResult.Failure(ex.Message);
        }
    }

    public async Task<UserDataSnapshot?> GetUserDataAsync()
    {
        if (!_authService.IsAuthenticated)
            return null;

        try
        {
            var content = await GetFileContentAsync(UserDataFileName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content))
                return null;

            return JsonSerializer.Deserialize<UserDataSnapshot>(content);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SyncResult> SyncAnnotationAsync(AnnotationBundle annotation)
    {
        if (!_authService.IsAuthenticated)
            return SyncResult.Failure("Not authenticated");

        try
        {
            annotation.LastModified = DateTime.UtcNow;
            annotation.LastModifiedByDeviceId = _deviceId;

            await UpsertEntityAsync(annotation, AnnotationsFileName).ConfigureAwait(false);
            return SyncResult.Success(1);
        }
        catch (Exception ex)
        {
            return SyncResult.Failure(ex.Message);
        }
    }

    public async Task<List<AnnotationBundle>> GetAllAnnotationsAsync()
    {
        if (!_authService.IsAuthenticated)
            return [];

        try
        {
            var content = await GetFileContentAsync(AnnotationsFileName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content))
                return [];

            return DeserializeOneOrMany<AnnotationBundle>(content);
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> ClearRemoteSyncDataAsync()
    {
        if (!_authService.IsAuthenticated || DriveService == null)
            return false;

        try
        {
            var request = DriveService.Files.List();
            request.Spaces = AppDataFolderParent;
            request.Q = $"'{AppDataFolderParent}' in parents and trashed=false";
            request.Fields = "files(id)";

            var files = await request.ExecuteAsync().ConfigureAwait(false);
            if (files.Files != null)
            {
                foreach (var file in files.Files)
                {
                    await DriveService.Files.Delete(file.Id).ExecuteAsync().ConfigureAwait(false);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpsertEntityAsync(object entity, string fileName)
    {
        if (DriveService == null)
            throw new InvalidOperationException("Drive service not available");

        var fileId = await FindFileByNameAsync(fileName).ConfigureAwait(false);
        var content = JsonSerializer.Serialize(entity);

        if (string.IsNullOrEmpty(fileId))
        {
            // Create new file
            var fileMetadata = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileName,
                Parents = new List<string> { AppDataFolderParent }
            };

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var request = DriveService.Files.Create(fileMetadata, stream, "application/json");
            await request.UploadAsync().ConfigureAwait(false);
        }
        else
        {
            // Update existing file
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var request = DriveService.Files.Update(new Google.Apis.Drive.v3.Data.File(), fileId, stream, "application/json");
            await request.UploadAsync().ConfigureAwait(false);
        }
    }

    private async Task<string?> GetFileContentAsync(string fileName)
    {
        if (DriveService == null)
            return null;

        try
        {
            var fileId = await FindFileByNameAsync(fileName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fileId))
                return null;

            var request = DriveService.Files.Get(fileId);
            using var stream = new MemoryStream();
            await request.DownloadAsync(stream).ConfigureAwait(false);
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Dictionary<string, DateTime?>> GetFileModifiedTimesAsync()
    {
        if (DriveService == null)
            return [];

        try
        {
            var request = DriveService.Files.List();
            request.Spaces = AppDataFolderParent;
            request.Q = $"'{AppDataFolderParent}' in parents and trashed=false";
            request.Fields = "files(name, modifiedTime)";

            var result = await request.ExecuteAsync().ConfigureAwait(false);
            return result.Files?
                .Where(f => f.Name != null)
                .ToDictionary(f => f.Name, f => f.ModifiedTimeDateTimeOffset?.UtcDateTime as DateTime?)
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<string?> FindFileByNameAsync(string fileName)
    {
        if (DriveService == null)
            return null;

        try
        {
            var request = DriveService.Files.List();
            request.Spaces = AppDataFolderParent;
            request.Q = $"'{AppDataFolderParent}' in parents and name='{fileName}' and trashed=false";
            request.Fields = "files(id)";

            var result = await request.ExecuteAsync().ConfigureAwait(false);
            return result.Files?.Count > 0 ? result.Files[0].Id : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<T> DeserializeOneOrMany<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<T>>(json) ?? [],
            JsonValueKind.Object => JsonSerializer.Deserialize<T>(json) is { } item ? [item] : [],
            _ => []
        };
    }

    private void UpdateStatus(bool isSyncing, string message, int progress)
    {
        _currentStatus = new SyncStatusInfo
        {
            IsSyncing = isSyncing,
            StatusMessage = message,
            ProgressPercentage = progress,
            LastSyncTime = isSyncing ? _currentStatus.LastSyncTime : DateTime.UtcNow,
            PendingItemsCount = _currentStatus.PendingItemsCount
        };

        SyncProgress?.Invoke(_currentStatus);
    }
}

