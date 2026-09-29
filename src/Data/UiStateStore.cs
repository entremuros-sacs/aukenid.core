namespace Aukenid.Core.Data;

using System.Text.Json;

/// <summary>Remembers which sidebar folder was last expanded across app launches.</summary>
public sealed class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly Lock _sync = new();

    public string SelectedFolder { get; private set; } = ConversationStore.GeneralFolder;

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
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new FileModel(SelectedFolder), JsonOptions));
        }
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
                var parsed = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (!string.IsNullOrWhiteSpace(parsed?.SelectedFolder))
                {
                    SelectedFolder = parsed.SelectedFolder;
                }
            }
            catch (JsonException)
            {
                // Keep the default folder if the file is unreadable.
            }
        }
    }

    private sealed record FileModel(string SelectedFolder);
}
