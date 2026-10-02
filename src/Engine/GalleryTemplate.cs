namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Resolves a chat-template family to llama.cpp's named template via <see cref="ChatTemplate.All"/>.
/// Optional models.json in a host data directory is the Desktop gallery catalog, not shipped with Core.
/// </summary>
internal static class GalleryTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string? LlamaCppName(string? galleryTemplate) =>
        MatchExact(galleryTemplate)?.Apply;

    public static IReadOnlyList<string> StopSequences(string? galleryTemplate) =>
        MatchExact(galleryTemplate)?.StopSequences ?? [];

    public static string? FamilyFromMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in MetadataKeys(metadata))
        {
            var row = MatchExact(key) ?? MatchPrefix(key);
            if (row is not null)
            {
                return row.Family;
            }
        }

        return null;
    }

    public static string? GalleryName(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Template;

    public static string? GalleryHash(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Hash;

    private static ChatTemplate? MatchExact(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var item in ChatTemplate.All)
        {
            if (item.Family.Equals(key, StringComparison.OrdinalIgnoreCase)
                || item.Apply.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static ChatTemplate? MatchPrefix(string key)
    {
        foreach (var item in ChatTemplate.All)
        {
            if (item.Family.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(item.Family, StringComparison.OrdinalIgnoreCase)
                || item.Apply.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(item.Apply, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static IEnumerable<string> MetadataKeys(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("general.architecture", out var architecture)
            && !string.IsNullOrWhiteSpace(architecture))
        {
            yield return architecture;
            var stem = TrimTrailingDigits(architecture);
            if (!stem.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            {
                yield return stem;
            }
        }

        if (metadata.TryGetValue("general.basename", out var basename)
            && !string.IsNullOrWhiteSpace(basename))
        {
            yield return basename;
            var token = basename.Split(['-', ' ', '_'], StringSplitOptions.RemoveEmptyEntries);
            if (token.Length > 0)
            {
                yield return token[0];
            }
        }
    }

    private static string TrimTrailingDigits(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsDigit(value[end - 1]))
        {
            end--;
        }

        return end > 0 && end < value.Length ? value[..end] : value;
    }

    private static ModelRow? FindModel(string dataDirectory, string? modelId, string? weightsPath)
    {
        var path = Path.Combine(dataDirectory, "models.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var models = JsonSerializer.Deserialize<List<ModelRow>>(File.ReadAllText(path), JsonOptions);
        if (models is null)
        {
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(weightsPath) ? null : Path.GetFileName(weightsPath);
        var stem = fileName is null ? null : Path.GetFileNameWithoutExtension(fileName);

        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(modelId) && model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            if (stem is not null && model.Id.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            if (fileName is not null)
            {
                var urlName = FileNameFromUrl(model.Url);
                if (urlName is not null && fileName.Equals(urlName, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }
        }

        return null;
    }

    private static string? FileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private sealed class ModelRow
    {
        public string Id { get; set; } = string.Empty;

        public string Template { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? Hash { get; set; }
    }
}
