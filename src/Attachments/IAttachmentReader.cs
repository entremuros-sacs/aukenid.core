namespace Aukenid.Core.Attachments;

internal readonly record struct AttachmentReadResult(bool Ok, string? Text, string? ErrorKey)
{
    public static AttachmentReadResult FromText(string text) => new(true, text, null);

    public static AttachmentReadResult Fail(string errorKey) => new(false, null, errorKey);
}

internal interface IAttachmentReader
{
    bool CanRead(string fileName);

    AttachmentReadResult Read(string path, int maxChars);
}
