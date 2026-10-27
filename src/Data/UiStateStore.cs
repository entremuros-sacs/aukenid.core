namespace Aukenid.Core.Data;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

/// <summary>Remembers which sidebar folder was last expanded, and which model was last active, across app launches.</summary>
public sealed partial class UiStateStore
{
    private readonly string _path;
    private readonly Lock _sync = new();

    public string SelectedFolder { get; private set; } = ConversationStore.GeneralFolder;

    public string? SelectedModelId { get; private set; }

    public UiStateStore(string path)
    {
        _path = path;
        Load();
    }

    public void SetSelectedFolder(string folderPath)
    {
        var value = string.IsNullOrWhiteSpace(folderPath) ? ConversationStore.GeneralFolder : folderPath;
        lock (_sync)
        {
            SelectedFolder = value;
            Persist();
        }
    }

    // Without this, the gallery's "first .gguf alphabetically" startup pick would silently
    // override whichever model the user had actually switched to in a prior session.
    public void SetSelectedModel(string? modelId)
    {
        lock (_sync)
        {
            SelectedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId;
            Persist();
        }
    }

    private void Persist()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(new FileModel(SelectedFolder, SelectedModelId), UiStateJsonContext.Default.FileModel));
    }

    private void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(File.ReadAllText(_path), UiStateJsonContext.Default.FileModel);
                if (!string.IsNullOrWhiteSpace(parsed?.SelectedFolder))
                {
                    SelectedFolder = parsed.SelectedFolder;
                }

                SelectedModelId = parsed?.SelectedModelId;
            }
            catch (JsonException)
            {
                // Keep the defaults if the file is unreadable.
            }
        }
    }

    private sealed record FileModel(string SelectedFolder, string? SelectedModelId = null);

    [JsonSerializable(typeof(FileModel))]
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    private sealed partial class UiStateJsonContext : JsonSerializerContext;
}
