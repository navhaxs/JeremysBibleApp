using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Interface for platform-independent local storage
/// </summary>
public interface ILocalStorageProvider
{
    /// <summary>
    /// Saves a string value with the given key
    /// </summary>
    Task SaveAsync(string key, string value);

    /// <summary>
    /// Retrieves a string value by key
    /// </summary>
    Task<string?> GetAsync(string key);

    /// <summary>
    /// Saves an object as JSON
    /// </summary>
    Task SaveObjectAsync<T>(string key, T obj);

    /// <summary>
    /// Retrieves an object from JSON
    /// </summary>
    Task<T?> GetObjectAsync<T>(string key);

    /// <summary>
    /// Removes a value by key
    /// </summary>
    Task RemoveAsync(string key);

    /// <summary>
    /// Checks if a key exists
    /// </summary>
    Task<bool> ContainsKeyAsync(string key);

    /// <summary>
    /// Clears all stored data
    /// </summary>
    Task ClearAsync();
}

/// <summary>
/// File-based implementation of local storage (works cross-platform)
/// </summary>
public class FileBasedLocalStorageProvider : ILocalStorageProvider
{
    private readonly string _storagePath;
    private readonly string _storageLockPath;

    public FileBasedLocalStorageProvider(string? storagePath = null)
    {
        _storagePath = storagePath ?? GetDefaultStoragePath();
        _storageLockPath = Path.Combine(_storagePath, ".storage.lock");
        if (!Directory.Exists(_storagePath))
            Directory.CreateDirectory(_storagePath);
    }

    public async Task SaveAsync(string key, string value)
    {
        await Task.Run(() =>
        {
            FileAccessCoordinator.Execute(_storageLockPath, () =>
            {
                try
                {
                    var filePath = GetFilePath(key);
                    FileAccessCoordinator.WriteAllTextAtomically(filePath, value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error saving key '{key}': {ex.Message}");
                }
            });
        });
    }

    public async Task<string?> GetAsync(string key)
    {
        return await Task.Run(() =>
        {
            return FileAccessCoordinator.Execute(_storageLockPath, () =>
            {
                try
                {
                    var filePath = GetFilePath(key);
                    return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading key '{key}': {ex.Message}");
                    return null;
                }
            });
        });
    }

    public async Task SaveObjectAsync<T>(string key, T obj)
    {
        var json = JsonSerializer.Serialize(obj);
        await SaveAsync(key, json);
    }

    public async Task<T?> GetObjectAsync<T>(string key)
    {
        var json = await GetAsync(key);
        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    public async Task RemoveAsync(string key)
    {
        await Task.Run(() =>
        {
            FileAccessCoordinator.Execute(_storageLockPath, () =>
            {
                try
                {
                    var filePath = GetFilePath(key);
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error removing key '{key}': {ex.Message}");
                }
            });
        });
    }

    public async Task<bool> ContainsKeyAsync(string key)
    {
        return await Task.Run(() =>
        {
            return FileAccessCoordinator.Execute(_storageLockPath, () =>
            {
                try
                {
                    var filePath = GetFilePath(key);
                    return File.Exists(filePath);
                }
                catch
                {
                    return false;
                }
            });
        });
    }

    public async Task ClearAsync()
    {
        await Task.Run(() =>
        {
            FileAccessCoordinator.Execute(_storageLockPath, () =>
            {
                try
                {
                    if (Directory.Exists(_storagePath))
                    {
                        foreach (var file in Directory.GetFiles(_storagePath))
                        {
                            File.Delete(file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error clearing storage: {ex.Message}");
                }
            });
        });
    }

    private string GetFilePath(string key)
    {
        var sanitizedKey = System.Text.RegularExpressions.Regex.Replace(key, @"[<>:""/\\|?*]", "_");
        return Path.Combine(_storagePath, sanitizedKey);
    }

    private static string GetDefaultStoragePath()
    {
        return SyncStoragePaths.GetLocalStorageDirectory();
    }
}

