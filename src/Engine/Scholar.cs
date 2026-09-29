using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aukenid.Core.Engine;

/// <summary>One academic paper. Vendor JSON is not passed through to the UI or the GGUF.</summary>
public sealed record ScholarPaper(
    string Title,
    int? Year,
    string Authors,
    string? Doi,
    string? Url,
    string? Tldr,
    string? Abstract,
    bool IsOpenAccess,
    string? PdfUrl,
    string? Venue,
    int? CitationCount);

public sealed record ScholarQuery(string Text, int Limit = 8);

public interface IScholarProvider
{
    Task<IReadOnlyList<ScholarPaper>> SearchAsync(ScholarQuery query, CancellationToken cancellationToken);
}
