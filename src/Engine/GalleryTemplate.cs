namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Maps a chat-template family (templates.json <c>template</c>, or GGUF architecture) to
/// llama.cpp's named chat template via the <c>apply</c> field. Optional models.json in a host
/// data directory is the Desktop gallery catalog, not shipped with Core.
/// </summary>
internal static class GalleryTemplate
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static string DefaultDirectory()
    {
        var root = Path.GetDirectoryName(typeof(GalleryTemplate).Assembly.Location);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var nextToAssembly = Path.Combine(root, "wwwroot", "data");
        return Directory.Exists(nextToAssembly)
            ? nextToAssembly
            : Path.Combine(root, "data");
    }

    public static string? LlamaCppName(string dataDirectory, string? galleryTemplate)
    {
        var row = MatchExact(LoadTemplateRows(dataDirectory), galleryTemplate);
        if (row is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(row.Apply) ? row.Template : row.Apply;
    }

    public static string? FamilyFromMetadata(string dataDirectory, IReadOnlyDictionary<string, string> metadata)
    {
        var rows = LoadTemplateRows(dataDirectory);
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        foreach (var key in MetadataKeys(metadata))
        {
            var row = MatchExact(rows, key) ?? MatchPrefix(rows, key);
            if (row is not null)
            {
                return row.Template;
            }
        }

        return null;
    }

    public static string? GalleryName(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Template;

    public static string? GalleryHash(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Hash;

    private static List<TemplateRow>? LoadTemplateRows(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "templates.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<TemplateRow>>(File.ReadAllText(path), JsonOptions);
    }

    private static TemplateRow? MatchExact(List<TemplateRow>? rows, string? key)
    {
        if (rows is null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return rows.Find(item =>
            item.Template.Equals(key, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(item.Apply)
                && item.Apply.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    private static TemplateRow? MatchPrefix(List<TemplateRow> rows, string key) =>
        rows.Find(item =>
            item.Template.StartsWith(key, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(item.Template, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(item.Apply)
                && (item.Apply.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith(item.Apply, StringComparison.OrdinalIgnoreCase))));

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

    private sealed record TemplateRow(string Template, string? Apply);

    private sealed class ModelRow
    {
        public string Id { get; set; } = string.Empty;

        public string Template { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? Hash { get; set; }
    }
}
