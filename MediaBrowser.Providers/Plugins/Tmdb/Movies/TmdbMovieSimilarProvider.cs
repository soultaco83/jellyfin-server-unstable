using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace MediaBrowser.Providers.Plugins.Tmdb.Movies;

/// <summary>
/// TMDb-based similar items provider for movies.
/// </summary>
public class TmdbMovieSimilarProvider : IRemoteSimilarItemsProvider<Movie>
{
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromDays(7);

    private readonly TmdbClientManager _tmdbClientManager;
    private readonly ILogger<TmdbMovieSimilarProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbMovieSimilarProvider"/> class.
    /// </summary>
    /// <param name="tmdbClientManager">The TMDb client manager.</param>
    /// <param name="logger">The logger.</param>
    public TmdbMovieSimilarProvider(TmdbClientManager tmdbClientManager, ILogger<TmdbMovieSimilarProvider> logger)
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
        Movie item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdStr) || !int.TryParse(tmdbIdStr, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return null;
        }

        var providerName = MetadataProvider.Tmdb.ToString();
        var page = query.StartPage;

        IReadOnlyList<TMDbLib.Objects.Search.SearchMovie> pageResults;
        int totalPages;
        try
        {
            (pageResults, totalPages) = await _tmdbClientManager
                .GetMovieSimilarPageAsync(tmdbId, page, TmdbUtils.GetImageLanguagesParam(string.Empty), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get similar movies from TMDb for {TmdbId} page {Page}", tmdbId, page);
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
