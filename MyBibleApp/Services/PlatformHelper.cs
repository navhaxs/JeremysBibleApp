using System;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;

namespace MyBibleApp.Services;

/// <summary>
/// Helper class to determine the current platform
/// </summary>
public static class PlatformHelper
{
    private static readonly OSPlatform AndroidPlatform = OSPlatform.Create("ANDROID");
    private static readonly OSPlatform IosPlatform = OSPlatform.Create("IOS");

    /// <summary>
    /// Gets whether the app is running on Android
    /// </summary>
    public static bool IsAndroid
    {
        get
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(AndroidPlatform)
                       || string.Equals(Environment.GetEnvironmentVariable("ANDROID_RUNTIME"), "1", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets whether the app is running on iOS
    /// </summary>
    public static bool IsIOS
    {
        get
        {
            try
            {
                return RuntimeInformation.IsOSPlatform(IosPlatform);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets whether the app is running on a desktop platform
    /// </summary>
    public static bool IsDesktop => !IsAndroid && !IsIOS &&
                                    (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                     RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

    /// <summary>
    /// Gets the current platform name
    /// </summary>
    public static string GetPlatformName()
    {
        if (IsAndroid) return "Android";
        if (IsIOS) return "iOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return "Unknown";
    }
}


