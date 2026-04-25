using System;
using System.Collections.Generic;

namespace MyBibleApp.Services.Sync;

/// <summary>
/// Snapshot of user preferences and settings for syncing
/// </summary>
public sealed class PreferencesSnapshot : SyncEntity
{
    /// <summary>
    /// Font size preference
    /// </summary>
    public double FontSize { get; set; } = 16.0;

    /// <summary>
    /// Preferred theme (Light, Dark, Auto)
    /// </summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>
    /// Whether to show verse numbers
    /// </summary>
    public bool ShowVerseNumbers { get; set; } = true;

    /// <summary>
    /// Whether to show footnotes
    /// </summary>
    public bool ShowFootnotes { get; set; } = true;

    /// <summary>
    /// Whether to show debug mode
    /// </summary>
    public bool ShowDebugMode { get; set; } = false;

    /// <summary>
    /// Language preference (e.g., "en-US")
    /// </summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// Custom preference dictionary for extension data
    /// </summary>
    public Dictionary<string, string> CustomSettings { get; set; } = [];
}


