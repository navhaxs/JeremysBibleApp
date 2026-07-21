using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyBibleApp.Services;

public sealed class UiPreferencesStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public UiPreferencesStore(string? storagePath = null)
    {
        var storageDir = storagePath ?? GetDefaultStoragePath();
        _filePath = Path.Combine(storageDir, "ui-prefs.json");
    }

    public async Task<(double? PortraitX, double? LandscapeX)> LoadJournalPanAsync()
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(_filePath)) return ((double?)null, (double?)null);
            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<UiPreferencesData>(json, JsonOptions);
                return (data?.PortraitJournalPanX, data?.LandscapeJournalPanX);
            }
            catch
            {
                return ((double?)null, (double?)null);
            }
        }).ConfigureAwait(false);
    }

    public async Task SaveJournalPanAsync(double? portraitX, double? landscapeX)
    {
        await Task.Run(() =>
        {
            var data = new UiPreferencesData
            {
                PortraitJournalPanX = portraitX,
                LandscapeJournalPanX = landscapeX
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            WriteAtomically(_filePath, json);
        }).ConfigureAwait(false);
    }

    private static void WriteAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempFilePath, content);
        File.Move(tempFilePath, filePath, overwrite: true);
    }

    private static string GetDefaultStoragePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MyBibleApp");
    }

    private sealed class UiPreferencesData
    {
        public double? PortraitJournalPanX { get; set; }
        public double? LandscapeJournalPanX { get; set; }
    }
}
