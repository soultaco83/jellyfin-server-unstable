using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Provides similar item references from remote/external sources for a specific item type.
/// Returns lightweight references with ProviderIds that the manager resolves to library items.
/// </summary>
/// <typeparam name="TItemType">The type of item this provider handles.</typeparam>
public interface IRemoteSimilarItemsProvider<TItemType> : ISimilarItemsProvider
    where TItemType : BaseItem
{
    /// <summary>
    /// Gets similar item references from an external source for a single page.
    /// </summary>
    /// <param name="item">The source item to find similar items for.</param>
    /// <param name="query">The query options (user, limit, exclusions, page, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A single page of similar item references, or null if no results.</returns>
    Task<SimilarItemProviderResponse?> GetSimilarItemsAsync(
        TItemType item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken);
}
