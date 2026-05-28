using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MyBibleApp.Models;

namespace MyBibleApp.Services;

/// <summary>
/// Provides JSON serialization and deserialization for journal types with validation
/// and descriptive error messages. Uses System.Text.Json with camelCase naming.
/// </summary>
public static class JournalJsonSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Serializes a <see cref="JournalInkStroke"/> to a JSON string.
    /// </summary>
    public static string SerializeStroke(JournalInkStroke stroke)
    {
        return JsonSerializer.Serialize(stroke, Options);
    }

    /// <summary>
    /// Serializes a <see cref="JournalDataSnapshot"/> to a JSON string.
    /// </summary>
    public static string SerializeSnapshot(JournalDataSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, Options);
    }

    /// <summary>
    /// Deserializes a JSON string to a <see cref="JournalInkStroke"/> with validation.
    /// Returns a descriptive error if required fields are missing or invalid.
    /// </summary>
    public static Result<JournalInkStroke> DeserializeStroke(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<JournalInkStroke>.Failure("JSON input is null or empty.");

        JournalInkStroke? stroke;
        try
        {
            stroke = JsonSerializer.Deserialize<JournalInkStroke>(json, Options);
        }
        catch (JsonException ex)
        {
            return Result<JournalInkStroke>.Failure($"Malformed JSON: {ex.Message}");
        }

        if (stroke is null)
            return Result<JournalInkStroke>.Failure("Deserialization produced a null object.");

        return ValidateStroke(stroke);
    }

    /// <summary>
    /// Deserializes a JSON string to a <see cref="JournalDataSnapshot"/> with validation.
    /// Returns a descriptive error if the JSON is malformed or required fields are missing.
    /// </summary>
    public static Result<JournalDataSnapshot> DeserializeSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<JournalDataSnapshot>.Failure("JSON input is null or empty.");

        JournalDataSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<JournalDataSnapshot>(json, Options);
        }
        catch (JsonException ex)
        {
            return Result<JournalDataSnapshot>.Failure($"Malformed JSON: {ex.Message}");
        }

        if (snapshot is null)
            return Result<JournalDataSnapshot>.Failure("Deserialization produced a null object.");

        // Validate each journal entry in the snapshot
        var errors = new List<string>();
        for (var i = 0; i < snapshot.Journals.Count; i++)
        {
            var entry = snapshot.Journals[i];
            if (entry.Metadata is null)
            {
                errors.Add($"Journals[{i}]: metadata is missing.");
                continue;
            }

            if (string.IsNullOrEmpty(entry.Metadata.Id))
                errors.Add($"Journals[{i}]: metadata.id is missing or empty.");

            // Validate each stroke in the entry
            for (var j = 0; j < entry.InkStrokes.Count; j++)
            {
                var strokeResult = ValidateStroke(entry.InkStrokes[j]);
                if (!strokeResult.IsSuccess)
                    errors.Add($"Journals[{i}].inkStrokes[{j}]: {strokeResult.ErrorMessage}");
            }
        }

        if (errors.Count > 0)
            return Result<JournalDataSnapshot>.Failure(string.Join(" ", errors));

        return Result<JournalDataSnapshot>.Success(snapshot);
    }

    /// <summary>
    /// Validates that a deserialized <see cref="JournalInkStroke"/> has all required fields
    /// with valid values.
    /// </summary>
    private static Result<JournalInkStroke> ValidateStroke(JournalInkStroke stroke)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(stroke.Id))
            errors.Add("Field 'id' is missing or empty.");

        if (stroke.Points is null || stroke.Points.Count == 0)
            errors.Add("Field 'points' is missing or empty.");

        if (string.IsNullOrEmpty(stroke.Color))
            errors.Add("Field 'color' is missing or empty.");

        if (stroke.StrokeWidth <= 0)
            errors.Add("Field 'strokeWidth' must be a positive number.");

        if (errors.Count > 0)
            return Result<JournalInkStroke>.Failure(string.Join(" ", errors));

        return Result<JournalInkStroke>.Success(stroke);
    }
}
