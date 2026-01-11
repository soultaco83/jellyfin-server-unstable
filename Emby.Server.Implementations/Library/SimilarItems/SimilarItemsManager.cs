using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.SimilarItems;

/// <summary>
/// Manages similar items providers and orchestrates similar items operations.
/// </summary>
public class SimilarItemsManager : ISimilarItemsManager
{
    private readonly ILogger<SimilarItemsManager> _logger;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private ISimilarItemsProvider[] _similarItemsProviders = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SimilarItemsManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="appPaths">The server application paths.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    public SimilarItemsManager(
        ILogger<SimilarItemsManager> logger,
        IServerApplicationPaths appPaths,
        ILibraryManager libraryManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public void AddParts(IEnumerable<ISimilarItemsProvider> providers)
    {
        _similarItemsProviders = providers.ToArray();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ISimilarItemsProvider> GetSimilarItemsProviders<T>()
        where T : BaseItem
    {
        return _similarItemsProviders
            .OfType<ILocalSimilarItemsProvider<T>>()
            .Cast<ISimilarItemsProvider>()
            .Concat(_similarItemsProviders.OfType<IRemoteSimilarItemsProvider<T>>())
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem item,
        IReadOnlyList<Guid> excludeArtistIds,
        User? user,
        DtoOptions dtoOptions,
        int? limit,
        LibraryOptions? libraryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(excludeArtistIds);

        var method = typeof(SimilarItemsManager)
            .GetMethod(nameof(GetSimilarItemsInternalAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(item.GetType());

        var task = (Task<IReadOnlyList<BaseItem>>)method.Invoke(this, [item, excludeArtistIds, user, dtoOptions, limit, libraryOptions, cancellationToken])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BaseItem>> GetSimilarItemsInternalAsync<T>(
        T item,
        IReadOnlyList<Guid> excludeArtistIds,
        User? user,
        DtoOptions dtoOptions,
        int? limit,
        LibraryOptions? libraryOptions,
        CancellationToken cancellationToken)
        where T : BaseItem
    {
        var requestedLimit = limit ?? 50;
        var itemKind = item.GetBaseItemKind();

        // Ensure ProviderIds is included in DtoOptions for matching remote provider responses
        if (!dtoOptions.Fields.Contains(ItemFields.ProviderIds))
        {
            dtoOptions.Fields = dtoOptions.Fields.Concat([ItemFields.ProviderIds]).ToArray();
        }

        var localProviders = _similarItemsProviders.OfType<ILocalSimilarItemsProvider<T>>().Cast<ISimilarItemsProvider>();
        var remoteProviders = _similarItemsProviders.OfType<IRemoteSimilarItemsProvider<T>>().Cast<ISimilarItemsProvider>();
        var matchingProviders = localProviders.Concat(remoteProviders).ToList();

        var typeOptions = libraryOptions?.GetTypeOptions(typeof(T).Name);
        if (typeOptions?.SimilarItemProviders?.Length > 0)
        {
            matchingProviders = matchingProviders
                .Where(p => typeOptions.SimilarItemProviders.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var orderedProviders = matchingProviders
            .OrderBy(p => GetConfiguredSimilarProviderOrder(typeOptions?.SimilarItemProviderOrder, p.Name))
            .ToList();

        var allResults = new List<(BaseItem Item, float Score)>();
        var excludeIds = new HashSet<Guid> { item.Id };
        foreach (var (providerOrder, provider) in orderedProviders.Index())
        {
            if (allResults.Count >= requestedLimit || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (provider is ILocalSimilarItemsProvider<T> localProvider)
            {
                var query = new SimilarItemsQuery
                {
                    User = user,
                    Limit = requestedLimit - allResults.Count,
                    DtoOptions = dtoOptions,
                    ExcludeItemIds = [.. excludeIds],
                    ExcludeArtistIds = excludeArtistIds,
                    StartPage = 1
                };

                var items = await localProvider.GetSimilarItemsAsync(item, query, cancellationToken).ConfigureAwait(false);

                foreach (var (position, resultItem) in items.Index())
                {
                    if (excludeIds.Add(resultItem.Id))
                    {
                        var score = CalculateScore(null, providerOrder, position);
                        allResults.Add((resultItem, score));
                    }
                }
            }
            else if (provider is IRemoteSimilarItemsProvider<T> remoteProvider)
            {
                var cachePath = GetSimilarItemsCachePath(provider.Name, typeof(T).Name, item.Id);

                var cachedResponses = await TryReadSimilarItemsCacheAsync(cachePath, cancellationToken).ConfigureAwait(false);
                if (cachedResponses?.Responses is not null)
                {
                    foreach (var cachedResponse in cachedResponses.Responses)
                    {
                        var resolvedItems = ResolveRemoteResponse(cachedResponse, providerOrder, user, dtoOptions, itemKind, excludeIds);
                        allResults.AddRange(resolvedItems);
                    }

                    continue;
                }

                var responses = new List<SimilarItemProviderResponse>();
                TimeSpan? cacheDuration = null;
                int? nextPage = 0;

                while (allResults.Count < requestedLimit && nextPage is not null && !cancellationToken.IsCancellationRequested)
                {
                    var query = new SimilarItemsQuery
                    {
                        User = user,
                        Limit = requestedLimit - allResults.Count,
                        DtoOptions = dtoOptions,
                        ExcludeItemIds = [.. excludeIds],
                        ExcludeArtistIds = excludeArtistIds,
                        StartPage = nextPage.Value
                    };

                    var response = await remoteProvider.GetSimilarItemsAsync(item, query, cancellationToken).ConfigureAwait(false);
                    if (response is not null && response.Matches.Count > 0)
                    {
                        responses.Add(response);
                        cacheDuration ??= response.CacheDuration;

                        var resolvedItems = ResolveRemoteResponse(response, providerOrder, user, dtoOptions, itemKind, excludeIds);
                        allResults.AddRange(resolvedItems);

                        nextPage = response.NextPage;
                    }
                    else
                    {
                        nextPage = null;
                    }
                }

                if (responses.Count > 0 && cacheDuration is not null)
                {
                    await SaveSimilarItemsCacheAsync(cachePath, responses, cacheDuration.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return allResults
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .Take(requestedLimit)
            .ToList();
    }

    private List<(BaseItem Item, float Score)> ResolveRemoteResponse(
        SimilarItemProviderResponse response,
        int providerOrder,
        User? user,
        DtoOptions dtoOptions,
        BaseItemKind itemKind,
        HashSet<Guid> excludeIds)
    {
        if (response.Matches.Count == 0)
        {
            return [];
        }

        var resolvedById = new Dictionary<Guid, (BaseItem Item, float Score)>();
        var providerLookup = new Dictionary<(string ProviderName, string ProviderId), (float? Score, int Position)>(StringTupleComparer.Instance);

        foreach (var (position, match) in response.Matches.Index())
        {
            var lookupKey = (match.ProviderName, match.ProviderId);
            if (!providerLookup.TryGetValue(lookupKey, out var existing))
            {
                providerLookup[lookupKey] = (match.Score, position);
            }
            else if (match.Score > existing.Score || (match.Score == existing.Score && position < existing.Position))
            {
                providerLookup[lookupKey] = (match.Score, position);
            }
        }

        var allProviderIds = providerLookup
            .GroupBy(kvp => kvp.Key.ProviderName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Key.ProviderId).ToArray());

        var query = new InternalItemsQuery(user)
        {
            HasAnyProviderIds = allProviderIds,
            IncludeItemTypes = [itemKind],
            DtoOptions = dtoOptions
        };

        var items = _libraryManager.GetItemList(query);

        foreach (var item in items)
        {
            if (excludeIds.Contains(item.Id) || resolvedById.ContainsKey(item.Id))
            {
                continue;
            }

            foreach (var providerName in allProviderIds.Keys)
            {
                if (item.TryGetProviderId(providerName, out var itemProviderId) && providerLookup.TryGetValue((providerName, itemProviderId), out var matchInfo))
                {
                    var score = CalculateScore(matchInfo.Score, providerOrder, matchInfo.Position);
                    if (!resolvedById.TryGetValue(item.Id, out var existing) || existing.Score < score)
                    {
                        excludeIds.Add(item.Id);
                        resolvedById[item.Id] = (item, score);
                    }

                    break;
                }
            }
        }

        return [.. resolvedById.Values];
    }

    private static float CalculateScore(float? matchScore, int providerOrder, int position)
    {
        // Use provider-supplied score if available, otherwise derive from position
        var baseScore = matchScore ?? (1.0f - (position * 0.02f));

        // Apply small boost based on provider order (higher priority providers get small bonus)
        var priorityBoost = Math.Max(0, 10 - providerOrder) * 0.005f;

        return Math.Clamp(baseScore + priorityBoost, 0f, 1f);
    }

    private static int GetConfiguredSimilarProviderOrder(string[]? orderConfig, string providerName)
    {
        if (orderConfig is null || orderConfig.Length == 0)
        {
            return int.MaxValue;
        }

        var index = Array.FindIndex(orderConfig, name => string.Equals(name, providerName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : int.MaxValue;
    }

    private string GetSimilarItemsCachePath(string providerName, string baseItemType, Guid itemId)
    {
        var dataPath = Path.Combine(
            _appPaths.CachePath,
            $"{providerName.ToLowerInvariant()}-similar-{baseItemType.ToLowerInvariant()}");
        return Path.Combine(dataPath, $"{itemId.ToString("N", CultureInfo.InvariantCulture)}.json");
    }

    private async Task<SimilarItemsCache?> TryReadSimilarItemsCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        var fileInfo = _fileSystem.GetFileSystemInfo(cachePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(cachePath);
            await using (stream.ConfigureAwait(false))
            {
                var cache = await JsonSerializer.DeserializeAsync<SimilarItemsCache>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                if (cache?.Responses is not null && DateTime.UtcNow < cache.ExpiresAt)
                {
                    return cache;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read similar items cache from {CachePath}", cachePath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse similar items cache from {CachePath}", cachePath);
        }

        return null;
    }

    private async Task SaveSimilarItemsCacheAsync(string cachePath, List<SimilarItemProviderResponse> responses, TimeSpan cacheDuration, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new SimilarItemsCache
            {
                Responses = responses,
                ExpiresAt = DateTime.UtcNow.Add(cacheDuration)
            };

            var stream = File.Create(cachePath);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to save similar items cache to {CachePath}", cachePath);
        }
    }

    private sealed class SimilarItemsCache
    {
        public List<SimilarItemProviderResponse>? Responses { get; set; }

        public DateTime ExpiresAt { get; set; }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Key, string Value)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Key, string Value) x, (string Key, string Value) y)
            => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Value) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }
}
