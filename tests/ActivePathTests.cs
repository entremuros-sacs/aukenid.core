using Aukenid.Core.Engine;
using Aukenid.Core.Contracts;

namespace Aukenid.Core.Tests;

public sealed class ActivePathTests
{
    [Fact]
    public void Resolve_WalksParentLinksAndDropsSiblingBranches()
    {
        var root = Msg("u1", null, "user", "Hola");
        var a1 = Msg("a1", "u1", "Aukenid", "Hola!");
        var u2 = Msg("u2", "a1", "user", "Cuéntame un chiste");
        var a2 = Msg("a2", "u2", "Aukenid", "¿Por qué el libro de matemáticas...");
        var u2b = Msg("u2b", "a1", "user", "¿Cómo te llamas?");
        var a2b = Msg("a2b", "u2b", "Aukenid", "Aukenid");

        var path = ActivePath.Resolve([root, a1, u2, a2, u2b, a2b], "u2b");

        Assert.Equal(["u1", "a1", "u2b"], path.Select(m => m.Id));
    }

    [Fact]
    public void ToTurns_MapsAukenidRoleToAssistantAndKeepsUser()
    {
        var root = Msg("u1", null, "user", "Hola");
        var a1 = Msg("a1", "u1", "Aukenid", "Hola!");
        var u2 = Msg("u2", "a1", "user", "Otra vez");

        var turns = ActivePath.ToTurns([root, a1, u2], "u2");

        Assert.Equal(
            [
                new ChatTurn("user", "Hola"),
                new ChatTurn("assistant", "Hola!"),
                new ChatTurn("user", "Otra vez"),
            ],
            turns);
    }

    [Fact]
    public void ToTurns_StripsUiNoticeMarkerFromStoredContent()
    {
        var root = Msg("u1", null, "user", "Escribe una receta");
        var a1 = Msg("a1", "u1", "Aukenid", "%%auk:document.updated%%\n\nReceta de ají de gallina...");
        var u2 = Msg("u2", "a1", "user", "Hazla más picante");

        var turns = ActivePath.ToTurns([root, a1, u2], "u2");

        Assert.Equal("Receta de ají de gallina...", turns[1].Content);
    }

    [Fact]
    public void ToTurns_StripsBareNoticeMarkerDownToEmptyContent()
    {
        var root = Msg("u1", null, "user", "Escribe una receta");
        var a1 = Msg("a1", "u1", "Aukenid", "%%auk:document.updated%%");
        var u2 = Msg("u2", "a1", "user", "Hazla más picante");

        var turns = ActivePath.ToTurns([root, a1, u2], "u2");

        Assert.Equal(string.Empty, turns[1].Content);
    }

    [Fact]
    public void Resolve_WithoutParentLinks_UsesChronologicalPrefixThroughLeaf()
    {
        var u1 = Msg("u1", null, "user", "uno");
        var a1 = Msg("a1", null, "Aukenid", "dos");
        var u2 = Msg("u2", null, "user", "tres");
        var a2 = Msg("a2", null, "Aukenid", "cuatro");

        var path = ActivePath.Resolve([u1, a1, u2, a2], "u2");

        Assert.Equal(["u1", "a1", "u2"], path.Select(m => m.Id));
    }

    [Fact]
    public void Resolve_EmptyThread_ReturnsEmpty()
    {
        Assert.Empty(ActivePath.Resolve([], "missing"));
    }

    [Fact]
    public void Resolve_UnknownLeafWithLinks_ReturnsAllMessages()
    {
        var root = Msg("u1", null, "user", "Hola");
        var a1 = Msg("a1", "u1", "Aukenid", "Hola!");

        var path = ActivePath.Resolve([root, a1], "missing");

        Assert.Equal(["u1", "a1"], path.Select(m => m.Id));
    }

    private static MessageDto Msg(string id, string? parentId, string role, string content) =>
        new(id, "thread", parentId, role, content, DateTimeOffset.UnixEpoch);
}
