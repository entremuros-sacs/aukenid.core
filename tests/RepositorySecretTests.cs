namespace Aukenid.Core.Tests;

public sealed class RepositorySecretTests
{
    [Fact]
    public void Sources_DoNotEmbedSearchApiKeyName()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".json" or ".md"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("AUKENID_SEARCH_API_KEY", text, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aukenid.Core.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
