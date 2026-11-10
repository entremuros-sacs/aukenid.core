namespace Aukenid.Core.Contracts;

/// <summary>
/// Stable string keys emitted by the orchestration layer for UI notices and warnings.
/// These keys are also used in the persisted %%auk:&lt;key&gt;%% message markers.
/// </summary>
public static class NoticeKeys
{
    public const string WebSearchIssue = "issue.web";
    public const string ScholarSearchIssue = "issue.papers";
    public const string WikiSearchIssue = "issue.wiki";

    public const string ContextIssue = "error.context";

    public const string DocumentUpdated = "document.updated";
    public const string DocumentUpdateFailed = "document.update-failed";
    public const string DocumentGenerating = "document.generating";
}
