namespace Aukenid.Core.Data;

using Aukenid.Core.Contracts;

internal static class FolderOrder
{
    public static int Rank(FolderDto folder) => Rank(folder.Path);

    public static int Rank(string path)
    {
        if (path.Equals(ConversationStore.TemporalFolder, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (path.Equals(ConversationStore.GeneralFolder, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    public static IReadOnlyList<FolderDto> Sort(IEnumerable<FolderDto> folders) =>
        folders
            .OrderBy(Rank)
            .ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
