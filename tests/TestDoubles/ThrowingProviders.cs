using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aukenid.Core.Engine;

namespace Aukenid.Core.Tests.TestDoubles;

internal sealed class ThrowingSearchProvider : ISearchProvider
{
    public Task<IReadOnlyList<WebHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Search provider should not be invoked in this test.");
}

internal sealed class ThrowingScholarProvider : IScholarProvider
{
    public Task<IReadOnlyList<ScholarPaper>> SearchAsync(ScholarQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Scholar provider should not be invoked in this test.");
}

internal sealed class ThrowingWikiProvider : IWikiProvider
{
    public Task<IReadOnlyList<WikiArticle>> SearchAsync(WikiQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Wiki provider should not be invoked in this test.");
}

