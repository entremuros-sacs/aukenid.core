using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aukenid.Core.Engine;

/// <summary>Local web.search DTO. Vendor HTML/JSON is not passed through to the UI or the GGUF.</summary>
public sealed record WebHit(string Title, string Url, string Snippet, string? Date);

public sealed record SearchQuery(string Text, string Gl = "us", string Hl = "en", int Limit = 8);

/// <summary>
/// Local web.search. Swap the adapter without changing prompt assembly.
/// </summary>
public interface ISearchProvider
{
    Task<IReadOnlyList<WebHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
