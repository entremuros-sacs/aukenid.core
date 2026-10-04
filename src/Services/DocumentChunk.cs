namespace Aukenid.Core.Services;

/// <summary>The kind of markdown block (or run of same-kind blocks) a <see cref="DocumentChunk"/> wraps.</summary>
internal enum DocumentChunkKind
{
    Heading,
    Paragraph,
    List,
    Table,
    Code,
    Quote,
    Other,
}

/// <summary>
/// One addressable, independently-editable unit of a document: a whole heading section when it fits
/// <see cref="DocumentChunker.ChunkBudgetChars"/>, otherwise one block or run of blocks within an
/// oversized section. <see cref="Text"/> is the exact original markdown substring for this chunk
/// (Markdig's own block span) - an untouched chunk is therefore reproduced byte-for-byte, never
/// re-rendered from a parsed object graph. <see cref="HeadingPath"/> is a human-readable label for the
/// prompt outline only (the enclosing section's heading, suffixed for a split section's later parts);
/// it is not part of <see cref="Text"/> unless the chunk itself IS that heading block.
/// </summary>
internal sealed record DocumentChunk(string Id, DocumentChunkKind Kind, string? HeadingPath, string Text);
