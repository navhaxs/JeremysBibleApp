using System;
using System.Collections.Generic;
using MyBibleApp.Services.Sync;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class SyncModelTests
{
    // ── SyncEntity defaults ─────────────────────────────────────────────

    [Fact]
    public void SyncEntity_HasUniqueId()
    {
        var a = new ReadingProgressSnapshot();
        var b = new ReadingProgressSnapshot();

        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void SyncEntity_DefaultSyncStatus_IsPending()
    {
        var entity = new ReadingProgressSnapshot();
        Assert.Equal(SyncStatus.Pending, entity.SyncStatus);
    }

    [Fact]
    public void SyncEntity_DefaultVersion_IsOne()
    {
        var entity = new AnnotationBundle();
        Assert.Equal(1, entity.Version);
    }

    [Fact]
    public void SyncEntity_LastModified_IsRecentUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var entity = new PreferencesSnapshot();
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.InRange(entity.LastModified, before, after);
    }

    // ── ReadingProgressSnapshot ─────────────────────────────────────────

    [Fact]
    public void ReadingProgressSnapshot_DefaultValues()
    {
        var snap = new ReadingProgressSnapshot();

        Assert.Equal(string.Empty, snap.BookCode);
        Assert.Equal(1, snap.Chapter);
        Assert.Equal(1, snap.Verse);
    }

    [Fact]
    public void ReadingProgressSnapshot_SetProperties()
    {
        var snap = new ReadingProgressSnapshot
        {
            BookCode = "ROM",
            Chapter = 8,
            Verse = 28
        };

        Assert.Equal("ROM", snap.BookCode);
        Assert.Equal(8, snap.Chapter);
        Assert.Equal(28, snap.Verse);
    }

    // ── PreferencesSnapshot ─────────────────────────────────────────────

    [Fact]
    public void PreferencesSnapshot_DefaultValues()
    {
        var prefs = new PreferencesSnapshot();

        Assert.Equal(16.0, prefs.FontSize);
        Assert.Equal("Auto", prefs.Theme);
        Assert.True(prefs.ShowVerseNumbers);
        Assert.True(prefs.ShowFootnotes);
        Assert.False(prefs.ShowDebugMode);
        Assert.Equal("en-US", prefs.Language);
        Assert.NotNull(prefs.CustomSettings);
        Assert.Empty(prefs.CustomSettings);
    }

    [Fact]
    public void PreferencesSnapshot_CustomSettings_RoundTrips()
    {
        var prefs = new PreferencesSnapshot();
        prefs.CustomSettings["font_family"] = "Serif";
        prefs.CustomSettings["line_spacing"] = "1.5";

        Assert.Equal("Serif", prefs.CustomSettings["font_family"]);
        Assert.Equal(2, prefs.CustomSettings.Count);
    }

    // ── AnnotationBundle ────────────────────────────────────────────────

    [Fact]
    public void AnnotationBundle_DefaultValues()
    {
        var ann = new AnnotationBundle();

        Assert.Equal(string.Empty, ann.BookCode);
        Assert.Equal(0, ann.Chapter);
        Assert.Equal(0, ann.Verse);
        Assert.Equal(string.Empty, ann.Notes);
        Assert.NotNull(ann.InkStrokes);
        Assert.Empty(ann.InkStrokes);
        Assert.Null(ann.HighlightColor);
        Assert.False(ann.IsBookmarked);
    }

    [Fact]
    public void AnnotationBundle_WithInkStrokes()
    {
        var ann = new AnnotationBundle
        {
            BookCode = "PSA",
            Chapter = 119,
            Verse = 105,
            InkStrokes =
            [
                new SerializedInkStroke
                {
                    Color = "#FF0000",
                    StrokeWidth = 3.0,
                    Points = [(10.0, 20.0), (30.0, 40.0)]
                }
            ]
        };

        Assert.Single(ann.InkStrokes);
        Assert.Equal("#FF0000", ann.InkStrokes[0].Color);
        Assert.Equal(3.0, ann.InkStrokes[0].StrokeWidth);
        Assert.Equal(2, ann.InkStrokes[0].Points.Count);
    }

    // ── SerializedInkStroke ─────────────────────────────────────────────

    [Fact]
    public void SerializedInkStroke_DefaultValues()
    {
        var stroke = new SerializedInkStroke();

        Assert.Equal("#000000", stroke.Color);
        Assert.Equal(2.0, stroke.StrokeWidth);
        Assert.Empty(stroke.Points);
    }

    // ── SyncResult ──────────────────────────────────────────────────────

    [Fact]
    public void SyncResult_Success_Factory()
    {
        var result = SyncResult.Success(5, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.ItemsSynced);
        Assert.Equal(2, result.ConflictsResolved);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void SyncResult_Failure_Factory()
    {
        var result = SyncResult.Failure("Network error");

        Assert.False(result.IsSuccess);
        Assert.Equal("Network error", result.ErrorMessage);
        Assert.Equal(0, result.ItemsSynced);
    }

    [Fact]
    public void SyncResult_SyncLog_DefaultsEmpty()
    {
        var result = SyncResult.Success(1);
        Assert.NotNull(result.SyncLog);
        Assert.Empty(result.SyncLog);
    }

    // ── AuthenticationResult ────────────────────────────────────────────

    [Fact]
    public void AuthenticationResult_Success_Factory()
    {
        var result = AuthenticationResult.Success("token", "user@example.com");

        Assert.True(result.IsSuccess);
        Assert.Equal("token", result.AccessToken);
        Assert.Equal("user@example.com", result.UserEmail);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void AuthenticationResult_Failure_Factory()
    {
        var result = AuthenticationResult.Failure("Invalid credentials");

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid credentials", result.ErrorMessage);
        Assert.Null(result.AccessToken);
        Assert.Null(result.UserEmail);
    }

    // ── SyncStatusInfo ──────────────────────────────────────────────────

    [Fact]
    public void SyncStatusInfo_DefaultValues()
    {
        var info = new SyncStatusInfo();

        Assert.False(info.IsSyncing);
        Assert.Null(info.LastSyncTime);
        Assert.Equal(0, info.PendingItemsCount);
        Assert.Equal(0, info.ProgressPercentage);
        Assert.Equal(string.Empty, info.StatusMessage);
    }

    // ── SyncQueueItem ───────────────────────────────────────────────────

    [Fact]
    public void SyncQueueItem_DefaultValues()
    {
        var item = new SyncQueueItem();

        Assert.False(string.IsNullOrWhiteSpace(item.Id));
        Assert.Equal(string.Empty, item.OperationType);
        Assert.Equal(string.Empty, item.Data);
        Assert.Equal(0, item.RetryCount);
        Assert.False(item.IsSynced);
    }

    // ── SyncStatus enum ─────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncStatus.Pending)]
    [InlineData(SyncStatus.Synced)]
    [InlineData(SyncStatus.Failed)]
    [InlineData(SyncStatus.Deleted)]
    public void SyncStatus_AllValues_Defined(SyncStatus status)
    {
        Assert.True(Enum.IsDefined(typeof(SyncStatus), status));
    }
}

