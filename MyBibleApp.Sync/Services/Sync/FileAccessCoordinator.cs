using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MyBibleApp.Services.Sync;

internal static class FileAccessCoordinator
{
    private static readonly ConcurrentDictionary<string, object> LocalLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(5);

    public static void Execute(string lockPath, Action action)
    {
        Execute(lockPath, () =>
        {
            action();
            return true;
        });
    }

    public static T Execute<T>(string lockPath, Func<T> action)
    {
        var normalizedPath = Path.GetFullPath(lockPath);
        var localLock = LocalLocks.GetOrAdd(normalizedPath, static _ => new object());

        lock (localLock)
        {
            using var mutex = CreateMutex(normalizedPath);
            var lockTaken = false;

            try
            {
                lockTaken = mutex.WaitOne(CrossProcessLockTimeout);
                if (!lockTaken)
                    throw new IOException($"Timed out waiting for file lock: {normalizedPath}");

                return action();
            }
            catch (AbandonedMutexException)
            {
                return action();
            }
            finally
            {
                if (lockTaken)
                    mutex.ReleaseMutex();
            }
        }
    }

    public static void WriteAllTextAtomically(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempFilePath, content);
        File.Move(tempFilePath, filePath, overwrite: true);
    }

    private static Mutex CreateMutex(string normalizedPath)
    {
        var nameBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var mutexName = $"MyBibleApp_FileAccess_{Convert.ToHexString(nameBytes)}";
        return new Mutex(initiallyOwned: false, mutexName);
    }
}