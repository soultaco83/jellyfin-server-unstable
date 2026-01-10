using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Plugins.Tmdb.TV;

/// <summary>
/// TMDb-based similar items provider for TV series.
/// </summary>
public class TmdbSeriesSimilarProvider : IRemoteSimilarItemsProvider<Series>
{
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromDays(7);

    private readonly TmdbClientManager _tmdbClientManager;
    private readonly ILogger<TmdbSeriesSimilarProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbSeriesSimilarProvider"/> class.
    /// </summary>
    /// <param name="tmdbClientManager">The TMDb client manager.</param>
    /// <param name="logger">The logger.</param>
    public TmdbSeriesSimilarProvider(TmdbClientManager tmdbClientManager, ILogger<TmdbSeriesSimilarProvider> logger)
    {
        _tmdbClientManager = tmdbClientManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => TmdbUtils.ProviderName;

    /// <inheritdoc/>
    public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;

    /// <inheritdoc/>
    public async Task<SimilarItemProviderResponse?> GetSimilarItemsAsync(
        Series item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr) || !int.TryParse(tmdbIdStr, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return null;
        }

        var providerName = MetadataProvider.Tmdb.ToString();
        var page = query.StartPage;

        IReadOnlyList<TMDbLib.Objects.Search.SearchTv> pageResults;
        int totalPages;
        try
        {
            (pageResults, totalPages) = await _tmdbClientManager
                .GetSeriesSimilarPageAsync(tmdbId, page, TmdbUtils.GetImageLanguagesParam(string.Empty), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get similar TV shows from TMDb for {TmdbId} page {Page}", tmdbId, page);
            return null;
        }

        if (pageResults.Count == 0)
        {
            return null;
        }

        var matches = new List<SimilarItemReference>();
        foreach (var similar in pageResults)
        {
            matches.Add(new SimilarItemReference
            {
                ProviderName = providerName,
                ProviderId = similar.Id.ToString(CultureInfo.InvariantCulture)
            });
        }

        return new SimilarItemProviderResponse
        {
            Matches = matches,
            ProviderName = Name,
            NextPage = page + 1 < totalPages ? page + 1 : null,
            CacheDuration = _cacheDuration
        };
    }
}
