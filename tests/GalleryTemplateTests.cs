using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests;

[TestClass]
public sealed class GalleryTemplateTests
{
    [TestMethod]
    public void ChatTemplateAll_MapsFamilyToLlamaCppName()
    {
        Assert.AreEqual("gemma", GalleryTemplate.LlamaCppName("gemma4"));
        Assert.AreEqual("gemma", GalleryTemplate.LlamaCppName("gemma"));
        Assert.AreEqual("mistral-v1", GalleryTemplate.LlamaCppName("mistral"));
        Assert.AreEqual("chatml", GalleryTemplate.LlamaCppName("qwen3"));
        Assert.IsNull(GalleryTemplate.LlamaCppName("missing"));
        Assert.AreEqual(
            "gemma4",
            GalleryTemplate.FamilyFromMetadata(new Dictionary<string, string>
            {
                ["general.architecture"] = "gemma3",
            }));
        Assert.AreEqual(
            "llama3",
            GalleryTemplate.FamilyFromMetadata(new Dictionary<string, string>
            {
                ["general.architecture"] = "llama",
            }));
    }

    [TestMethod]
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

            Assert.AreEqual("mistral", GalleryTemplate.GalleryName(dir, "ministral3-8b", null));
            Assert.AreEqual(
                "mistral",
                GalleryTemplate.GalleryName(dir, null, "/tmp/Ministral-3-8B-Instruct-2512-Q4_K_M.gguf"));
            Assert.IsNull(GalleryTemplate.GalleryHash(dir, "ministral3-8b", null));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
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

            Assert.AreEqual("sha256:abc", GalleryTemplate.GalleryHash(dir, "qwen35-4b", null));
            Assert.AreEqual("sha256:abc", GalleryTemplate.GalleryHash(dir, null, "/tmp/Qwen.gguf"));
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
