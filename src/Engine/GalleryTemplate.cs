namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Maps a gallery architecture (models.json <c>template</c>) to llama.cpp's named chat template
/// via the <c>apply</c> field in templates.json.
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
        if (string.IsNullOrWhiteSpace(galleryTemplate))
        {
            return null;
        }

        var path = Path.Combine(dataDirectory, "templates.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var rows = JsonSerializer.Deserialize<List<TemplateRow>>(File.ReadAllText(path), JsonOptions);
        var row = rows?.Find(item =>
            item.Template.Equals(galleryTemplate, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(item.Apply)
                && item.Apply.Equals(galleryTemplate, StringComparison.OrdinalIgnoreCase)));
        if (row is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(row.Apply) ? row.Template : row.Apply;
    }

    public static string? GalleryName(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Template;

    public static string? GalleryHash(string dataDirectory, string? modelId, string? weightsPath) =>
        FindModel(dataDirectory, modelId, weightsPath)?.Hash;

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
