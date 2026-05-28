using System;
using System.Collections.Generic;
using System.Text.Json;
using MyBibleApp.Models;
using MyBibleApp.Services;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class JournalJsonSerializerTests
{
    [Fact]
    public void SerializeStroke_ProducesValidJson()
    {
        var stroke = new JournalInkStroke
        {
            Id = "test-id-123",
            Points = [new StrokePoint(10.5, 20.75), new StrokePoint(30.25, 40.0)],
            Color = "#FF0000",
            StrokeWidth = 2.5,
            IsHighlight = false
        };

        var json = JournalJsonSerializer.SerializeStroke(stroke);

        Assert.Contains("\"id\"", json);
        Assert.Contains("\"test-id-123\"", json);
        Assert.Contains("\"points\"", json);
        Assert.Contains("\"color\"", json);
        Assert.Contains("\"#FF0000\"", json);
        Assert.Contains("\"strokeWidth\"", json);
        Assert.Contains("\"isHighlight\"", json);
    }

    [Fact]
    public void DeserializeStroke_ValidJson_ReturnsSuccess()
    {
        var stroke = new JournalInkStroke
        {
            Id = "abc-123",
            Points = [new StrokePoint(1.5, 2.5), new StrokePoint(3.0, 4.0)],
            Color = "#00FF00",
            StrokeWidth = 3.0,
            IsHighlight = true
        };

        var json = JournalJsonSerializer.SerializeStroke(stroke);
        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("abc-123", result.Value!.Id);
        Assert.Equal(2, result.Value.Points.Count);
        Assert.Equal(1.5, result.Value.Points[0].X);
        Assert.Equal(2.5, result.Value.Points[0].Y);
        Assert.Equal("#00FF00", result.Value.Color);
        Assert.Equal(3.0, result.Value.StrokeWidth);
        Assert.True(result.Value.IsHighlight);
    }

    [Fact]
    public void DeserializeStroke_MissingId_ReturnsDescriptiveError()
    {
        var json = """
        {
            "points": [{"x": 1.0, "y": 2.0}],
            "color": "#FF0000",
            "strokeWidth": 2.0,
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("id", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_MissingPoints_ReturnsDescriptiveError()
    {
        var json = """
        {
            "id": "test-id",
            "color": "#FF0000",
            "strokeWidth": 2.0,
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("points", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_MissingColor_ReturnsDescriptiveError()
    {
        var json = """
        {
            "id": "test-id",
            "points": [{"x": 1.0, "y": 2.0}],
            "strokeWidth": 2.0,
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("color", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_ZeroStrokeWidth_ReturnsDescriptiveError()
    {
        var json = """
        {
            "id": "test-id",
            "points": [{"x": 1.0, "y": 2.0}],
            "color": "#FF0000",
            "strokeWidth": 0,
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("strokeWidth", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_NegativeStrokeWidth_ReturnsDescriptiveError()
    {
        var json = """
        {
            "id": "test-id",
            "points": [{"x": 1.0, "y": 2.0}],
            "color": "#FF0000",
            "strokeWidth": -1.5,
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("strokeWidth", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_MultipleFieldsMissing_ReportsAllErrors()
    {
        var json = """
        {
            "isHighlight": false
        }
        """;

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("id", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("points", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("strokeWidth", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeStroke_MalformedJson_ReturnsDescriptiveError()
    {
        var json = "{ this is not valid json }}}";

        var result = JournalJsonSerializer.DeserializeStroke(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("Malformed JSON", result.ErrorMessage!);
    }

    [Fact]
    public void DeserializeStroke_EmptyString_ReturnsError()
    {
        var result = JournalJsonSerializer.DeserializeStroke("");

        Assert.False(result.IsSuccess);
        Assert.Contains("null or empty", result.ErrorMessage!);
    }

    [Fact]
    public void DeserializeStroke_NullString_ReturnsError()
    {
        var result = JournalJsonSerializer.DeserializeStroke(null!);

        Assert.False(result.IsSuccess);
        Assert.Contains("null or empty", result.ErrorMessage!);
    }

    [Fact]
    public void SerializeSnapshot_ProducesValidJson()
    {
        var snapshot = new JournalDataSnapshot
        {
            Journals =
            [
                new JournalEntry
                {
                    Metadata = new Models.Journal
                    {
                        Id = "journal-1",
                        Name = "Test Journal",
                        TranslationId = "eng_bsb",
                        BookCode = "JHN",
                        StartChapter = 1,
                        StartVerse = 1,
                        EndChapter = 1,
                        EndVerse = 18,
                        ContentHash = "abc123",
                        Layout = new JournalLayout
                        {
                            TextColumnWidthDip = 600,
                            LeftMarginDip = 100,
                            RightMarginDip = 100
                        },
                        CreatedAtUtc = DateTime.UtcNow,
                        LastModifiedUtc = DateTime.UtcNow
                    },
                    InkStrokes =
                    [
                        new JournalInkStroke
                        {
                            Id = "stroke-1",
                            Points = [new StrokePoint(10.5, 20.75)],
                            Color = "#FF0000",
                            StrokeWidth = 2.0,
                            IsHighlight = false
                        }
                    ]
                }
            ],
            LastModifiedUtc = DateTime.UtcNow
        };

        var json = JournalJsonSerializer.SerializeSnapshot(snapshot);

        Assert.Contains("\"journals\"", json);
        Assert.Contains("\"journal-1\"", json);
        Assert.Contains("\"stroke-1\"", json);
    }

    [Fact]
    public void DeserializeSnapshot_ValidJson_ReturnsSuccess()
    {
        var snapshot = new JournalDataSnapshot
        {
            Journals =
            [
                new JournalEntry
                {
                    Metadata = new Models.Journal
                    {
                        Id = "journal-1",
                        Name = "Test",
                        TranslationId = "eng_bsb",
                        BookCode = "JHN",
                        StartChapter = 1,
                        StartVerse = 1,
                        EndChapter = 1,
                        EndVerse = 5,
                        ContentHash = "hash123",
                        Layout = new JournalLayout
                        {
                            TextColumnWidthDip = 600,
                            LeftMarginDip = 100,
                            RightMarginDip = 100
                        },
                        CreatedAtUtc = DateTime.UtcNow,
                        LastModifiedUtc = DateTime.UtcNow
                    },
                    InkStrokes =
                    [
                        new JournalInkStroke
                        {
                            Id = "s1",
                            Points = [new StrokePoint(1.0, 2.0)],
                            Color = "#000000",
                            StrokeWidth = 1.5,
                            IsHighlight = false
                        }
                    ]
                }
            ],
            LastModifiedUtc = DateTime.UtcNow
        };

        var json = JournalJsonSerializer.SerializeSnapshot(snapshot);
        var result = JournalJsonSerializer.DeserializeSnapshot(json);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value!.Journals);
        Assert.Equal("journal-1", result.Value.Journals[0].Metadata.Id);
    }

    [Fact]
    public void DeserializeSnapshot_InvalidStrokeInEntry_ReturnsError()
    {
        // Snapshot with a stroke missing required fields
        var json = """
        {
            "journals": [
                {
                    "metadata": {
                        "id": "j1",
                        "name": "Test",
                        "translationId": "eng_bsb",
                        "bookCode": "JHN",
                        "startChapter": 1,
                        "startVerse": 1,
                        "endChapter": 1,
                        "endVerse": 5,
                        "contentHash": "hash",
                        "layout": { "textColumnWidthDip": 600, "leftMarginDip": 100, "rightMarginDip": 100 },
                        "createdAtUtc": "2024-01-01T00:00:00Z",
                        "lastModifiedUtc": "2024-01-01T00:00:00Z"
                    },
                    "inkStrokes": [
                        {
                            "id": "",
                            "points": [],
                            "color": "",
                            "strokeWidth": 0,
                            "isHighlight": false
                        }
                    ]
                }
            ],
            "lastModifiedUtc": "2024-01-01T00:00:00Z"
        }
        """;

        var result = JournalJsonSerializer.DeserializeSnapshot(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("id", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeSnapshot_MalformedJson_ReturnsError()
    {
        var result = JournalJsonSerializer.DeserializeSnapshot("not json at all");

        Assert.False(result.IsSuccess);
        Assert.Contains("Malformed JSON", result.ErrorMessage!);
    }

    [Fact]
    public void DeserializeSnapshot_EmptyString_ReturnsError()
    {
        var result = JournalJsonSerializer.DeserializeSnapshot("");

        Assert.False(result.IsSuccess);
        Assert.Contains("null or empty", result.ErrorMessage!);
    }

    [Fact]
    public void Options_UsesCamelCase()
    {
        Assert.Equal(JsonNamingPolicy.CamelCase, JournalJsonSerializer.Options.PropertyNamingPolicy);
    }

    [Fact]
    public void Options_IsIndented()
    {
        Assert.True(JournalJsonSerializer.Options.WriteIndented);
    }

    [Fact]
    public void Options_IsCaseInsensitiveOnRead()
    {
        Assert.True(JournalJsonSerializer.Options.PropertyNameCaseInsensitive);
    }
}
