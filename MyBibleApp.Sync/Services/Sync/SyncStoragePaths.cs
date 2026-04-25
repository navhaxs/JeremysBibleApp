using System;
using System.IO;

namespace MyBibleApp.Services.Sync;

internal static class SyncStoragePaths
{
    public static string GetLocalStorageDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MyBibleApp", "LocalStorage");
    }

    public static string GetQueueStorageDirectory()
    {
        return GetLocalStorageDirectory();
    }
}