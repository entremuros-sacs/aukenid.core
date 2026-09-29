namespace Aukenid.Core.Attachments;

internal sealed class AttachmentReaderRegistry
{
    private readonly IReadOnlyList<IAttachmentReader> _readers;

    public AttachmentReaderRegistry(IEnumerable<IAttachmentReader>? readers = null)
    {
        _readers = readers is null
            ? [new PlainTextAttachmentReader(), new PdfAttachmentReader(), new ImageAttachmentReader()]
            : readers.ToList();
    }

    public IAttachmentReader? ForFileName(string fileName)
    {
        foreach (var reader in _readers)
        {
            if (reader.CanRead(fileName))
            {
                return reader;
            }
        }

        return null;
    }

    public bool IsSupported(string fileName) => ForFileName(fileName) is not null;
}
