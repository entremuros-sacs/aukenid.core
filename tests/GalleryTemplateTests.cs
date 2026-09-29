using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

public sealed class GalleryTemplateTests
{
    [Fact]
    public void ChatTemplateAll_MapsFamilyToLlamaCppName()
    {
        Assert.Equal("gemma", GalleryTemplate.LlamaCppName("gemma4"));
        Assert.Equal("gemma", GalleryTemplate.LlamaCppName("gemma"));
        Assert.Equal("mistral-v1", GalleryTemplate.LlamaCppName("mistral"));
        Assert.Equal("chatml", GalleryTemplate.LlamaCppName("qwen3"));
        Assert.Null(GalleryTemplate.LlamaCppName("missing"));
        Assert.Equal(
            "gemma4",
            GalleryTemplate.FamilyFromMetadata(new Dictionary<string, string>
            {
                ["general.architecture"] = "gemma3",
            }));
        Assert.Equal(
            "llama3",
            GalleryTemplate.FamilyFromMetadata(new Dictionary<string, string>
            {
                ["general.architecture"] = "llama",
            }));
    }

    [Fact]
    public void GalleryName_MatchesIdAndUrlFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aukenid-template-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "models.json"), """
                [
                  {
                    "id": "ministral3-8b",
                    "template": "mistral",
                    "url": "https://huggingface.co/org/Ministral-3-8B-Instruct-2512-Q4_K_M.gguf"
                  }
                ]
                """);

            Assert.Equal("mistral", GalleryTemplate.GalleryName(dir, "ministral3-8b", null));
            Assert.Equal(
                "mistral",
                GalleryTemplate.GalleryName(dir, null, "/tmp/Ministral-3-8B-Instruct-2512-Q4_K_M.gguf"));
            Assert.Null(GalleryTemplate.GalleryHash(dir, "ministral3-8b", null));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void GalleryHash_ReadsSha256FromTheMatchingRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aukenid-template-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "models.json"), """
                [
                  {
                    "id": "qwen35-4b",
                    "template": "qwen3",
                    "url": "https://example.com/Qwen.gguf",
                    "hash": "sha256:abc"
                  }
                ]
                """);

            Assert.Equal("sha256:abc", GalleryTemplate.GalleryHash(dir, "qwen35-4b", null));
            Assert.Equal("sha256:abc", GalleryTemplate.GalleryHash(dir, null, "/tmp/Qwen.gguf"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
