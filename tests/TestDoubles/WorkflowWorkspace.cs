using System;
using System.IO;
using Aukenid.Core.Attachments;
using Aukenid.Core.Data;
using Aukenid.Core.Services;

namespace Aukenid.Core.Tests.TestDoubles;

internal sealed class WorkflowWorkspace : IDisposable
{
    private readonly string _root;

    public WorkflowWorkspace()
    {
        _root = Path.Combine(Path.GetTempPath(), "aukenid-workflow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Store = new ConversationStore(Path.Combine(_root, "conversations"));

        Attachments = new AttachmentReaderRegistry();
        DocumentTool = new DocumentTool(Store);
    }

    public ConversationStore Store { get; }

    public AttachmentReaderRegistry Attachments { get; }

    public DocumentTool DocumentTool { get; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

