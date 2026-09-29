namespace Aukenid.Core.Contracts;

public sealed record ThreadDto(
    string Id,
    string Title,
    string FolderPath,
    DateTimeOffset Created,
    bool HasMessages = false);

public sealed record FolderDto(
    string Path,
    string Name,
    bool IsStandard);

public sealed record MessageDto(
    string Id,
    string ThreadId,
    string? ParentId,
    string Role,
    string Content,
    DateTimeOffset Created,
    IReadOnlyList<string>? Attachments = null);
