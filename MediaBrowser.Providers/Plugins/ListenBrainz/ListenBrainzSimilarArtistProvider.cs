using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.Plugins.ListenBrainz.Api;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Plugins.ListenBrainz;

/// <summary>
/// ListenBrainz-based similar items provider for music artists.
/// </summary>
public class ListenBrainzSimilarArtistProvider : IRemoteSimilarItemsProvider<MusicArtist>
{
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromDays(14);

    private readonly ListenBrainzLabsClient _labsClient;
    private readonly ILogger<ListenBrainzSimilarArtistProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListenBrainzSimilarArtistProvider"/> class.
    /// </summary>
    /// <param name="labsClient">The ListenBrainz Labs API client.</param>
    /// <param name="logger">The logger.</param>
    public ListenBrainzSimilarArtistProvider(
        ListenBrainzLabsClient labsClient,
        ILogger<ListenBrainzSimilarArtistProvider> logger)
    {
        _labsClient = labsClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "ListenBrainz";

    /// <inheritdoc/>
    public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;

    /// <inheritdoc/>
    public async Task<SimilarItemProviderResponse?> GetSimilarItemsAsync(
        MusicArtist item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);

        // ListenBrainz doesn't support pagination - only return results on first page (page 0)
        if (query.StartPage > 0)
        {
            return null;
        }

        if (!item.TryGetProviderId(MetadataProvider.MusicBrainzArtist, out var mbidStr) || !Guid.TryParse(mbidStr, out var mbid))
        {
            _logger.LogDebug("No MusicBrainz Artist ID found for {ArtistName}", item.Name);
            return null;
        }

        IReadOnlyList<Guid> similarMbids;
        try
        {
            similarMbids = await _labsClient.GetSimilarArtistsAsync(mbid, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch similar artists from ListenBrainz for {ArtistMbid}", mbid);
            return null;
        }

        if (similarMbids.Count == 0)
        {
            return null;
        }

        var providerName = MetadataProvider.MusicBrainzArtist.ToString();
        var matches = new List<SimilarItemReference>();

        foreach (var similarMbid in similarMbids)
        {
            matches.Add(new SimilarItemReference
            {
                ProviderName = providerName,
                ProviderId = similarMbid.ToString()
            });
        }

        return new SimilarItemProviderResponse
        {
            Matches = matches,
            ProviderName = MetadataProvider.MusicBrainzArtist.ToString(),
            NextPage = null,
            CacheDuration = _cacheDuration
        };
    }
}
