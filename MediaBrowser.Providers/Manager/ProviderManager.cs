using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Book = MediaBrowser.Controller.Entities.Book;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using MusicAlbum = MediaBrowser.Controller.Entities.Audio.MusicAlbum;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace MediaBrowser.Providers.Manager
{
    /// <summary>
    /// Class ProviderManager.
    /// </summary>
    public class ProviderManager : IProviderManager, IDisposable
    {
        private readonly Lock _refreshQueueLock = new();
        private readonly ILogger<ProviderManager> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IFileSystem _fileSystem;
        private readonly IServerApplicationPaths _appPaths;
        private readonly ILibraryManager _libraryManager;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ILyricManager _lyricManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IBaseItemManager _baseItemManager;
        private readonly ConcurrentDictionary<Guid, double> _activeRefreshes = new();
        private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
        private readonly PriorityQueue<(Guid ItemId, MetadataRefreshOptions RefreshOptions), RefreshPriority> _refreshQueue = new();
        private readonly IMemoryCache _memoryCache;
        private readonly IMediaSegmentManager _mediaSegmentManager;
        private readonly AsyncKeyedLocker<string> _imageSaveLock = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        private IImageProvider[] _imageProviders = [];
        private IMetadataService[] _metadataServices = [];
        private IMetadataProvider[] _metadataProviders = [];
        private IMetadataSaver[] _savers = [];
        private IExternalId[] _externalIds = [];
        private IExternalUrlProvider[] _externalUrlProviders = [];
        private ISimilarItemsProvider[] _similarItemsProviders = [];
        private bool _isProcessingRefreshQueue;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderManager"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The Http client factory.</param>
        /// <param name="subtitleManager">The subtitle manager.</param>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="libraryMonitor">The library monitor.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="fileSystem">The filesystem.</param>
        /// <param name="appPaths">The server application paths.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="baseItemManager">The BaseItem manager.</param>
        /// <param name="lyricManager">The lyric manager.</param>
        /// <param name="memoryCache">The memory cache.</param>
        /// <param name="mediaSegmentManager">The media segment manager.</param>
        public ProviderManager(
            IHttpClientFactory httpClientFactory,
            ISubtitleManager subtitleManager,
            IServerConfigurationManager configurationManager,
            ILibraryMonitor libraryMonitor,
            ILogger<ProviderManager> logger,
            IFileSystem fileSystem,
            IServerApplicationPaths appPaths,
            ILibraryManager libraryManager,
            IBaseItemManager baseItemManager,
            ILyricManager lyricManager,
            IMemoryCache memoryCache,
            IMediaSegmentManager mediaSegmentManager)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configurationManager = configurationManager;
            _libraryMonitor = libraryMonitor;
            _fileSystem = fileSystem;
            _appPaths = appPaths;
            _libraryManager = libraryManager;
            _subtitleManager = subtitleManager;
            _baseItemManager = baseItemManager;
            _lyricManager = lyricManager;
            _memoryCache = memoryCache;
            _mediaSegmentManager = mediaSegmentManager;
        }

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<BaseItem>>? RefreshStarted;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<BaseItem>>? RefreshCompleted;

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<Tuple<BaseItem, double>>>? RefreshProgress;

        /// <inheritdoc/>
        public void AddParts(
            IEnumerable<IImageProvider> imageProviders,
            IEnumerable<IMetadataService> metadataServices,
            IEnumerable<IMetadataProvider> metadataProviders,
            IEnumerable<IMetadataSaver> metadataSavers,
            IEnumerable<IExternalId> externalIds,
            IEnumerable<IExternalUrlProvider> externalUrlProviders,
            IEnumerable<ISimilarItemsProvider> similarItemsProviders)
        {
            _imageProviders = imageProviders.ToArray();
            _metadataServices = metadataServices.OrderBy(i => i.Order).ToArray();
            _metadataProviders = metadataProviders.ToArray();
            _externalIds = externalIds.OrderBy(i => i.ProviderName).ToArray();
            _externalUrlProviders = externalUrlProviders.OrderBy(i => i.Name).ToArray();
            _similarItemsProviders = similarItemsProviders.ToArray();

            _savers = metadataSavers.ToArray();
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
            Jellyfin.Database.Implementations.Entities.User user,
            DtoOptions dtoOptions,
            int? limit,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(excludeArtistIds);

            var method = typeof(ProviderManager)
                .GetMethod(nameof(GetSimilarItemsInternal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(item.GetType());

            var task = (Task<IReadOnlyList<BaseItem>>)method.Invoke(this, [item, excludeArtistIds, user, dtoOptions, limit, libraryOptions, cancellationToken])!;
            return await task.ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<BaseItem>> GetSimilarItemsInternal<T>(
            T item,
            IReadOnlyList<Guid> excludeArtistIds,
            Jellyfin.Database.Implementations.Entities.User user,
            DtoOptions dtoOptions,
            int? limit,
            LibraryOptions libraryOptions,
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

                    // Check cache before querying the provider
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

                    // Fetch all pages from this provider
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

                    // Cache responses after fetching all pages
                    if (responses.Count > 0 && cacheDuration is not null)
                    {
                        await SaveSimilarItemsCacheAsync(cachePath, responses, cacheDuration.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Sort by score and return up to the requested limit
            return allResults
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .Take(requestedLimit)
                .ToList();
        }

        private List<(BaseItem Item, float Score)> ResolveRemoteResponse(
            SimilarItemProviderResponse response,
            int providerOrder,
            Jellyfin.Database.Implementations.Entities.User user,
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

            var groupedByProviderName = providerLookup
                .GroupBy(kvp => kvp.Key.ProviderName)
                .ToList();

            foreach (var providerGroup in groupedByProviderName)
            {
                var providerName = providerGroup.Key;
                var providerIds = providerGroup.Select(x => x.Key.ProviderId).ToArray();

                var query = new InternalItemsQuery(user)
                {
                    HasAnyProviderIds = new Dictionary<string, string[]>
                    {
                        { providerName, providerIds }
                    },
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

                    if (item.TryGetProviderId(providerName, out var itemProviderId) && providerLookup.TryGetValue((providerName, itemProviderId), out var matchInfo))
                    {
                        var score = CalculateScore(matchInfo.Score, providerOrder, matchInfo.Position);
                        if (!resolvedById.TryGetValue(item.Id, out var existing) || existing.Score < score)
                        {
                            excludeIds.Add(item.Id);
                            resolvedById[item.Id] = (item, score);
                        }
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

        /// <inheritdoc/>
        public Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var type = item.GetType();

            var service = _metadataServices.FirstOrDefault(current => current.CanRefreshPrimary(type))
                ?? _metadataServices.FirstOrDefault(current => current.CanRefresh(item));

            if (service is null)
            {
                _logger.LogError("Unable to find a metadata service for item of type {TypeName}", type.Name);
                return Task.FromResult(ItemUpdateType.None);
            }

            return service.RefreshMetadata(item, options, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task SaveImage(BaseItem item, string url, ImageType type, int? imageIndex, CancellationToken cancellationToken)
        {
            using (await _imageSaveLock.LockAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (_memoryCache.TryGetValue(url, out (string ContentType, byte[] ImageContents)? cachedValue)
                    && cachedValue is not null)
                {
                    var imageContents = cachedValue.Value.ImageContents;
                    var cacheStream = new MemoryStream(imageContents, 0, imageContents.Length, false);
                    await using (cacheStream.ConfigureAwait(false))
                    {
                        await SaveImage(
                            item,
                            cacheStream,
                            cachedValue.Value.ContentType,
                            type,
                            imageIndex,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;

                // Workaround for tvheadend channel icons
                // TODO: Isolate this hack into the tvh plugin
                if (string.IsNullOrEmpty(contentType))
                {
                    // Special case for imagecache
                    if (url.Contains("/imagecache/", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = MediaTypeNames.Image.Png;
                    }
                }

                // some providers don't correctly report media type, extract from url if no extension found
                if (contentType is null || contentType.Equals(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
                {
                    // Strip query parameters from url to get actual path.
                    contentType = MimeTypes.GetMimeType(new Uri(url).GetLeftPart(UriPartial.Path));
                }

                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException($"Request returned '{contentType}' instead of an image type", null, HttpStatusCode.NotFound);
                }

                var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var stream = new MemoryStream(responseBytes, 0, responseBytes.Length, false);
                await using (stream.ConfigureAwait(false))
                {
                    _memoryCache.Set(url, (contentType, responseBytes), TimeSpan.FromSeconds(10));

                    await SaveImage(
                        item,
                        stream,
                        contentType,
                        type,
                        imageIndex,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc/>
        public Task SaveImage(BaseItem item, Stream source, string mimeType, ImageType type, int? imageIndex, CancellationToken cancellationToken)
        {
            return new ImageSaver(_configurationManager, _libraryMonitor, _fileSystem, _logger).SaveImage(item, source, mimeType, type, imageIndex, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task SaveImage(BaseItem item, string source, string mimeType, ImageType type, int? imageIndex, bool? saveLocallyWithMedia, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentNullException(nameof(source));
            }

            try
            {
                var fileStream = AsyncFile.OpenRead(source);
                await new ImageSaver(_configurationManager, _libraryMonitor, _fileSystem, _logger)
                    .SaveImage(item, fileStream, mimeType, type, imageIndex, saveLocallyWithMedia, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(source);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Source file {Source} not found or in use, skip removing", source);
                }
            }
        }

        /// <inheritdoc/>
        public Task SaveImage(Stream source, string mimeType, string path)
        {
            return new ImageSaver(_configurationManager, _libraryMonitor, _fileSystem, _logger)
                .SaveImage(source, path);
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(BaseItem item, RemoteImageQuery query, CancellationToken cancellationToken)
        {
            var providers = GetRemoteImageProviders(item, query.IncludeDisabledProviders);

            if (!string.IsNullOrEmpty(query.ProviderName))
            {
                var providerName = query.ProviderName;

                providers = providers.Where(i => string.Equals(i.Name, providerName, StringComparison.OrdinalIgnoreCase));
            }

            if (query.ImageType is not null)
            {
                providers = providers.Where(i => i.GetSupportedImages(item).Contains(query.ImageType.Value));
            }

            var preferredLanguage = item.GetPreferredMetadataLanguage();

            var tasks = providers.Select(i => GetImages(item, i, preferredLanguage, query.IncludeAllLanguages, cancellationToken, query.ImageType));

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            return results.SelectMany(i => i);
        }

        /// <summary>
        /// Gets the images.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="provider">The provider.</param>
        /// <param name="preferredLanguage">The preferred language.</param>
        /// <param name="includeAllLanguages">Whether to include all languages in results.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="type">The type.</param>
        /// <returns>Task{IEnumerable{RemoteImageInfo}}.</returns>
        private async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item,
            IRemoteImageProvider provider,
            string preferredLanguage,
            bool includeAllLanguages,
            CancellationToken cancellationToken,
            ImageType? type = null)
        {
            bool hasPreferredLanguage = !string.IsNullOrWhiteSpace(preferredLanguage);

            try
            {
                var result = await provider.GetImages(item, cancellationToken).ConfigureAwait(false);

                if (type.HasValue)
                {
                    result = result.Where(i => i.Type == type.Value);
                }

                if (!includeAllLanguages && hasPreferredLanguage)
                {
                    // Filter out languages that do not match the preferred languages.
                    //
                    // TODO: should exception case of "en" (English) eventually be removed?
                    result = result.Where(i => string.IsNullOrWhiteSpace(i.Language) ||
                                               string.Equals(preferredLanguage, i.Language, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(i.Language, "en", StringComparison.OrdinalIgnoreCase));
                }

                return result.OrderByLanguageDescending(preferredLanguage);
            }
            catch (OperationCanceledException)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ProviderName} failed in GetImageInfos for type {ItemType} at {ItemPath}", provider.GetType().Name, item.GetType().Name, item.Path);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ImageProviderInfo> GetRemoteImageProviderInfo(BaseItem item)
        {
            return GetRemoteImageProviders(item, true).Select(i => new ImageProviderInfo(i.Name, i.GetSupportedImages(item).ToArray()));
        }

        private IEnumerable<IRemoteImageProvider> GetRemoteImageProviders(BaseItem item, bool includeDisabled)
        {
            var options = GetMetadataOptions(item);
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            return GetImageProvidersInternal(
                item,
                libraryOptions,
                options,
                new ImageRefreshOptions(new DirectoryService(_fileSystem)),
                includeDisabled).OfType<IRemoteImageProvider>();
        }

        /// <inheritdoc/>
        public IEnumerable<IImageProvider> GetImageProviders(BaseItem item, ImageRefreshOptions refreshOptions)
        {
            return GetImageProvidersInternal(item, _libraryManager.GetLibraryOptions(item), GetMetadataOptions(item), refreshOptions, false);
        }

        private IEnumerable<IImageProvider> GetImageProvidersInternal(BaseItem item, LibraryOptions libraryOptions, MetadataOptions options, ImageRefreshOptions refreshOptions, bool includeDisabled)
        {
            var typeOptions = libraryOptions.GetTypeOptions(item.GetType().Name);
            var fetcherOrder = typeOptions?.ImageFetcherOrder ?? options.ImageFetcherOrder;

            return _imageProviders.Where(i => CanRefreshImages(i, item, typeOptions, refreshOptions, includeDisabled))
                .OrderBy(i => GetConfiguredOrder(fetcherOrder, i.Name))
                .ThenBy(GetDefaultOrder);
        }

        private bool CanRefreshImages(
            IImageProvider provider,
            BaseItem item,
            TypeOptions? libraryTypeOptions,
            ImageRefreshOptions refreshOptions,
            bool includeDisabled)
        {
            try
            {
                if (!provider.Supports(item))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ProviderName} failed in Supports for type {ItemType} at {ItemPath}", provider.GetType().Name, item.GetType().Name, item.Path);
                return false;
            }

            if (includeDisabled || provider is ILocalImageProvider)
            {
                return true;
            }

            if (item.IsLocked && refreshOptions.ImageRefreshMode != MetadataRefreshMode.FullRefresh)
            {
                return false;
            }

            return _baseItemManager.IsImageFetcherEnabled(item, libraryTypeOptions, provider.Name);
        }

        /// <inheritdoc />
        public IEnumerable<IMetadataProvider<T>> GetMetadataProviders<T>(BaseItem item, LibraryOptions libraryOptions)
            where T : BaseItem
        {
            var globalMetadataOptions = GetMetadataOptions(item);

            return GetMetadataProvidersInternal<T>(item, libraryOptions, globalMetadataOptions, false, false);
        }

        /// <inheritdoc />
        public IEnumerable<IMetadataSaver> GetMetadataSavers(BaseItem item, LibraryOptions libraryOptions)
        {
            return _savers.Where(i => IsSaverEnabledForItem(i, item, libraryOptions, ItemUpdateType.MetadataEdit, false));
        }

        private IEnumerable<IMetadataProvider<T>> GetMetadataProvidersInternal<T>(BaseItem item, LibraryOptions libraryOptions, MetadataOptions globalMetadataOptions, bool includeDisabled, bool forceEnableInternetMetadata)
            where T : BaseItem
        {
            var localMetadataReaderOrder = libraryOptions.LocalMetadataReaderOrder ?? globalMetadataOptions.LocalMetadataReaderOrder;
            var typeOptions = libraryOptions.GetTypeOptions(item.GetType().Name);
            var metadataFetcherOrder = typeOptions?.MetadataFetcherOrder ?? globalMetadataOptions.MetadataFetcherOrder;

            return _metadataProviders.OfType<IMetadataProvider<T>>()
                .Where(i => CanRefreshMetadata(i, item, typeOptions, includeDisabled, forceEnableInternetMetadata))
                .OrderBy(i =>
                    // local and remote providers will be interleaved in the final order
                    // only relative order within a type matters: consumers of the list filter to one or the other
                    i switch
                    {
                        ILocalMetadataProvider => GetConfiguredOrder(localMetadataReaderOrder, i.Name),
                        IRemoteMetadataProvider => GetConfiguredOrder(metadataFetcherOrder, i.Name),
                        // Default to end
                        _ => int.MaxValue
                    })
                .ThenBy(GetDefaultOrder);
        }

        private bool CanRefreshMetadata(
            IMetadataProvider provider,
            BaseItem item,
            TypeOptions? libraryTypeOptions,
            bool includeDisabled,
            bool forceEnableInternetMetadata)
        {
            if (!item.SupportsLocalMetadata && provider is ILocalMetadataProvider)
            {
                return false;
            }

            if (includeDisabled)
            {
                return true;
            }

            // If locked only allow local providers
            if (item.IsLocked && provider is not ILocalMetadataProvider && provider is not IForcedProvider)
            {
                return false;
            }

            if (forceEnableInternetMetadata || provider is not IRemoteMetadataProvider)
            {
                return true;
            }

            return _baseItemManager.IsMetadataFetcherEnabled(item, libraryTypeOptions, provider.Name);
        }

        private static int GetConfiguredOrder(string[] order, string providerName)
        {
            var index = Array.IndexOf(order, providerName);

            if (index != -1)
            {
                return index;
            }

            // default to end
            return int.MaxValue;
        }

        private static int GetDefaultOrder(object provider)
        {
            if (provider is IHasOrder hasOrder)
            {
                return hasOrder.Order;
            }

            // after items that want to be first (~0) but before items that want to be last (~100)
            return 50;
        }

        /// <inheritdoc/>
        public MetadataPluginSummary[] GetAllMetadataPlugins()
        {
            return new[]
            {
                GetPluginSummary<Movie>(),
                GetPluginSummary<BoxSet>(),
                GetPluginSummary<Book>(),
                GetPluginSummary<Series>(),
                GetPluginSummary<Season>(),
                GetPluginSummary<Episode>(),
                GetPluginSummary<MusicAlbum>(),
                GetPluginSummary<MusicArtist>(),
                GetPluginSummary<Audio>(),
                GetPluginSummary<AudioBook>(),
                GetPluginSummary<Studio>(),
                GetPluginSummary<MusicVideo>(),
                GetPluginSummary<Video>()
            };
        }

        private MetadataPluginSummary GetPluginSummary<T>()
            where T : BaseItem, new()
        {
            // Give it a dummy path just so that it looks like a file system item
            var dummy = new T
            {
                Path = Path.Combine(_appPaths.InternalMetadataPath, "dummy"),
                ParentId = Guid.NewGuid()
            };

            var options = GetMetadataOptions(dummy);

            var summary = new MetadataPluginSummary
            {
                ItemType = typeof(T).Name
            };

            var libraryOptions = new LibraryOptions();

            var imageProviders = GetImageProvidersInternal(
                dummy,
                libraryOptions,
                options,
                new ImageRefreshOptions(new DirectoryService(_fileSystem)),
                true).ToList();

            var pluginList = summary.Plugins.ToList();

            AddMetadataPlugins(pluginList, dummy, libraryOptions, options);
            AddImagePlugins(pluginList, imageProviders);

            // Subtitle fetchers
            var subtitleProviders = _subtitleManager.GetSupportedProviders(dummy);
            pluginList.AddRange(subtitleProviders.Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.SubtitleFetcher
            }));

            // Lyric fetchers
            var lyricProviders = _lyricManager.GetSupportedProviders(dummy);
            pluginList.AddRange(lyricProviders.Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.LyricFetcher
            }));

            // Media segment providers
            var mediaSegmentProviders = _mediaSegmentManager.GetSupportedProviders(dummy);
            pluginList.AddRange(mediaSegmentProviders.Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.MediaSegmentProvider
            }));

            // Similar items providers
            var similarItemsProviders = GetSimilarItemsProviders<T>();
            pluginList.AddRange(similarItemsProviders.Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = i.Type
            }));

            summary.Plugins = pluginList.ToArray();

            var supportedImageTypes = imageProviders.OfType<IRemoteImageProvider>()
                .SelectMany(i => i.GetSupportedImages(dummy))
                .ToList();

            supportedImageTypes.AddRange(imageProviders.OfType<IDynamicImageProvider>()
                .SelectMany(i => i.GetSupportedImages(dummy)));

            summary.SupportedImageTypes = supportedImageTypes.Distinct().ToArray();

            return summary;
        }

        private void AddMetadataPlugins<T>(List<MetadataPlugin> list, T item, LibraryOptions libraryOptions, MetadataOptions options)
            where T : BaseItem
        {
            var providers = GetMetadataProvidersInternal<T>(item, libraryOptions, options, true, true).ToList();

            // Locals
            list.AddRange(providers.Where(i => i is ILocalMetadataProvider).Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.LocalMetadataProvider
            }));

            // Fetchers
            list.AddRange(providers.Where(i => i is IRemoteMetadataProvider).Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.MetadataFetcher
            }));

            // Savers
            list.AddRange(_savers.Where(i => IsSaverEnabledForItem(i, item, libraryOptions, ItemUpdateType.MetadataEdit, true)).OrderBy(i => i.Name).Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.MetadataSaver
            }));
        }

        private void AddImagePlugins(List<MetadataPlugin> list, List<IImageProvider> imageProviders)
        {
            // Locals
            list.AddRange(imageProviders.Where(i => i is ILocalImageProvider).Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.LocalImageProvider
            }));

            // Fetchers
            list.AddRange(imageProviders.Where(i => i is IDynamicImageProvider || (i is IRemoteImageProvider)).Select(i => new MetadataPlugin
            {
                Name = i.Name,
                Type = MetadataPluginType.ImageFetcher
            }));
        }

        /// <inheritdoc/>
        public MetadataOptions GetMetadataOptions(BaseItem item)
            => _configurationManager.GetMetadataOptionsForType(item.GetType().Name) ?? new MetadataOptions();

        /// <inheritdoc/>
        public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType)
            => SaveMetadataAsync(item, updateType, _savers);

        /// <inheritdoc/>
        public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType, IEnumerable<string> savers)
            => SaveMetadataAsync(item, updateType, _savers.Where(i => savers.Contains(i.Name, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Saves the metadata.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="updateType">Type of the update.</param>
        /// <param name="savers">The savers.</param>
        private async Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType, IEnumerable<IMetadataSaver> savers)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            var applicableSavers = savers.Where(i => IsSaverEnabledForItem(i, item, libraryOptions, updateType, false)).ToList();
            if (applicableSavers.Count == 0)
            {
                return;
            }

            foreach (var saver in applicableSavers)
            {
                _logger.LogDebug("Saving {Item} to {Saver}", item.Path ?? item.Name, saver.Name);

                if (saver is IMetadataFileSaver fileSaver)
                {
                    string path;

                    try
                    {
                        path = fileSaver.GetSavePath(item);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in {Saver} GetSavePath", saver.Name);
                        continue;
                    }

                    try
                    {
                        _libraryMonitor.ReportFileSystemChangeBeginning(path);
                        await saver.SaveAsync(item, CancellationToken.None).ConfigureAwait(false);
                        item.DateLastSaved = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in metadata saver");
                    }
                    finally
                    {
                        _libraryMonitor.ReportFileSystemChangeComplete(path, false);
                    }
                }
                else
                {
                    try
                    {
                        await saver.SaveAsync(item, CancellationToken.None).ConfigureAwait(false);
                        item.DateLastSaved = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in metadata saver");
                    }
                }
            }
        }

        /// <summary>
        /// Determines whether [is saver enabled for item] [the specified saver].
        /// </summary>
        private bool IsSaverEnabledForItem(IMetadataSaver saver, BaseItem item, LibraryOptions libraryOptions, ItemUpdateType updateType, bool includeDisabled)
        {
            var options = GetMetadataOptions(item);

            try
            {
                if (!saver.IsEnabledFor(item, updateType))
                {
                    return false;
                }

                if (!includeDisabled)
                {
                    if (libraryOptions.MetadataSavers is null)
                    {
                        if (options.DisabledMetadataSavers.Contains(saver.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (!item.IsSaveLocalMetadataEnabled())
                        {
                            if (updateType >= ItemUpdateType.MetadataEdit)
                            {
                                // Manual edit occurred
                                // Even if save local is off, save locally anyway if the metadata file already exists
                                if (saver is not IMetadataFileSaver fileSaver || !File.Exists(fileSaver.GetSavePath(item)))
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                // Manual edit did not occur
                                // Since local metadata saving is disabled, consider it disabled
                                return false;
                            }
                        }
                    }
                    else
                    {
                        if (!libraryOptions.MetadataSavers.Contains(saver.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in {Saver}.IsEnabledFor", saver.Name);
                return false;
            }
        }

        /// <inheritdoc/>
        public Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, CancellationToken cancellationToken)
            where TItemType : BaseItem, new()
            where TLookupType : ItemLookupInfo
        {
            BaseItem? referenceItem = null;

            if (!searchInfo.ItemId.IsEmpty())
            {
                referenceItem = _libraryManager.GetItemById(searchInfo.ItemId);
            }

            return GetRemoteSearchResults<TItemType, TLookupType>(searchInfo, referenceItem, cancellationToken);
        }

        private async Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, BaseItem? referenceItem, CancellationToken cancellationToken)
            where TItemType : BaseItem, new()
            where TLookupType : ItemLookupInfo
        {
            LibraryOptions libraryOptions;

            if (referenceItem is null)
            {
                // Give it a dummy path just so that it looks like a file system item
                var dummy = new TItemType
                {
                    Path = Path.Combine(_appPaths.InternalMetadataPath, "dummy"),
                    ParentId = Guid.NewGuid()
                };

                dummy.SetParent(new Folder());

                referenceItem = dummy;
                libraryOptions = new LibraryOptions();
            }
            else
            {
                libraryOptions = _libraryManager.GetLibraryOptions(referenceItem);
            }

            var options = GetMetadataOptions(referenceItem);

            var providers = GetMetadataProvidersInternal<TItemType>(referenceItem, libraryOptions, options, searchInfo.IncludeDisabledProviders, false)
                .OfType<IRemoteSearchProvider<TLookupType>>();

            if (!string.IsNullOrEmpty(searchInfo.SearchProviderName))
            {
                providers = providers.Where(i => string.Equals(i.Name, searchInfo.SearchProviderName, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrWhiteSpace(searchInfo.SearchInfo.MetadataLanguage))
            {
                searchInfo.SearchInfo.MetadataLanguage = _configurationManager.Configuration.PreferredMetadataLanguage;
            }

            if (string.IsNullOrWhiteSpace(searchInfo.SearchInfo.MetadataCountryCode))
            {
                searchInfo.SearchInfo.MetadataCountryCode = _configurationManager.Configuration.MetadataCountryCode;
            }

            var resultList = new List<RemoteSearchResult>();

            foreach (var provider in providers)
            {
                try
                {
                    var results = await provider.GetSearchResults(searchInfo.SearchInfo, cancellationToken).ConfigureAwait(false);

                    foreach (var result in results)
                    {
                        result.SearchProviderName = provider.Name;

                        var existingMatch = resultList.FirstOrDefault(i => i.ProviderIds.Any(p => string.Equals(result.GetProviderId(p.Key), p.Value, StringComparison.OrdinalIgnoreCase)));

                        if (existingMatch is null)
                        {
                            resultList.Add(result);
                        }
                        else
                        {
                            foreach (var providerId in result.ProviderIds)
                            {
                                existingMatch.ProviderIds.TryAdd(providerId.Key, providerId.Value);
                            }

                            if (string.IsNullOrWhiteSpace(existingMatch.ImageUrl))
                            {
                                existingMatch.ImageUrl = result.ImageUrl;
                            }
                        }
                    }
                }
#pragma warning disable CA1031 // do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // do not catch general exception types
                {
                    _logger.LogError(ex, "Provider {ProviderName} failed to retrieve search results", provider.Name);
                }
            }

            return resultList;
        }

        private IEnumerable<IExternalId> GetExternalIds(IHasProviderIds item)
        {
            return _externalIds.Where(i =>
            {
                try
                {
                    return i.Supports(item);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in {Type}.Supports", i.GetType().Name);
                    return false;
                }
            });
        }

        /// <inheritdoc/>
        public IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item)
        {
            return _externalUrlProviders
                .SelectMany(p => p
                    .GetExternalUrls(item)
                    .Select(externalUrl => new ExternalUrl { Name = p.Name, Url = externalUrl }));
        }

        /// <inheritdoc/>
        public IEnumerable<ExternalIdInfo> GetExternalIdInfos(IHasProviderIds item)
        {
            return GetExternalIds(item)
                .Select(i => new ExternalIdInfo(
                    name: i.ProviderName,
                    key: i.Key,
                    type: i.Type));
        }

        /// <inheritdoc/>
        public HashSet<Guid> GetRefreshQueue()
        {
            lock (_refreshQueueLock)
            {
                return _refreshQueue.UnorderedItems.Select(x => x.Element.ItemId).ToHashSet();
            }
        }

        /// <inheritdoc/>
        public void OnRefreshStart(BaseItem item)
        {
            _logger.LogDebug("OnRefreshStart {Item:N}", item.Id);
            _activeRefreshes[item.Id] = 0;
            try
            {
                RefreshStarted?.Invoke(this, new GenericEventArgs<BaseItem>(item));
            }
            catch (Exception ex)
            {
                // EventHandlers should never propagate exceptions, but we have little control over plugins...
                _logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshStarted));
            }
        }

        /// <inheritdoc/>
        public void OnRefreshComplete(BaseItem item)
        {
            _logger.LogDebug("OnRefreshComplete {Item:N}", item.Id);
            _activeRefreshes.TryRemove(item.Id, out _);

            try
            {
                RefreshCompleted?.Invoke(this, new GenericEventArgs<BaseItem>(item));
            }
            catch (Exception ex)
            {
                // EventHandlers should never propagate exceptions, but we have little control over plugins...
                _logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshCompleted));
            }
        }

        /// <inheritdoc/>
        public double? GetRefreshProgress(Guid id)
        {
            if (_activeRefreshes.TryGetValue(id, out double value))
            {
                return value;
            }

            return null;
        }

        /// <inheritdoc/>
        public void OnRefreshProgress(BaseItem item, double progress)
        {
            var id = item.Id;
            _logger.LogDebug("OnRefreshProgress {Id:N} {Progress}", id, progress);

            if (!_activeRefreshes.TryGetValue(id, out var current)
                || progress <= current
                || !_activeRefreshes.TryUpdate(id, progress, current))
            {
                // Item isn't currently refreshing, or update was received out-of-order, so don't trigger event.
                return;
            }

            try
            {
                RefreshProgress?.Invoke(this, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(item, progress)));
            }
            catch (Exception ex)
            {
                // EventHandlers should never propagate exceptions, but we have little control over plugins...
                _logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshProgress));
            }
        }

        /// <inheritdoc/>
        public void QueueRefresh(Guid itemId, MetadataRefreshOptions options, RefreshPriority priority)
        {
            if (itemId.IsEmpty())
            {
                throw new ArgumentException("Guid can't be empty", nameof(itemId));
            }

            if (_disposed)
            {
                return;
            }

            _refreshQueue.Enqueue((itemId, options), priority);

            lock (_refreshQueueLock)
            {
                if (!_isProcessingRefreshQueue)
                {
                    _isProcessingRefreshQueue = true;
                    Task.Run(StartProcessingRefreshQueue);
                }
            }
        }

        private async Task StartProcessingRefreshQueue()
        {
            var libraryManager = _libraryManager;

            if (_disposed)
            {
                return;
            }

            var cancellationToken = _disposeCancellationTokenSource.Token;

            while (_refreshQueue.TryDequeue(out var refreshItem, out _))
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    var item = libraryManager.GetItemById(refreshItem.ItemId);
                    if (item is null)
                    {
                        continue;
                    }

                    var task = item is MusicArtist artist
                        ? RefreshArtist(artist, refreshItem.RefreshOptions, cancellationToken)
                        : RefreshItem(item, refreshItem.RefreshOptions, cancellationToken);

                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing item");
                }
            }

            lock (_refreshQueueLock)
            {
                _isProcessingRefreshQueue = false;
            }
        }

        private async Task RefreshItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);

            // Collection folders don't validate their children so we'll have to simulate that here
            switch (item)
            {
                case CollectionFolder collectionFolder:
                    await RefreshCollectionFolderChildren(options, collectionFolder, cancellationToken).ConfigureAwait(false);
                    break;
                case Folder folder:
                    await folder.ValidateChildren(new Progress<double>(), options, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task RefreshCollectionFolderChildren(MetadataRefreshOptions options, CollectionFolder collectionFolder, CancellationToken cancellationToken)
        {
            foreach (var child in collectionFolder.GetPhysicalFolders())
            {
                await child.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);

                await child.ValidateChildren(new Progress<double>(), options, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RefreshArtist(MusicArtist item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var albums = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
                    ArtistIds = new[] { item.Id },
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                })
                .OfType<MusicAlbum>();

            var musicArtists = albums
                .Select(i => i.MusicArtist)
                .Where(i => i is not null)
                .Distinct();

            var musicArtistRefreshTasks = musicArtists.Select(i => i.ValidateChildren(new Progress<double>(), options, true, false, cancellationToken));

            await Task.WhenAll(musicArtistRefreshTasks).ConfigureAwait(false);

            try
            {
                await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing library");
            }
        }

        /// <inheritdoc/>
        public Task RefreshFullItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            return RefreshItem(item, options, cancellationToken);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                if (!_disposeCancellationTokenSource.IsCancellationRequested)
                {
                    _disposeCancellationTokenSource.Cancel();
                }

                _disposeCancellationTokenSource.Dispose();
                _imageSaveLock.Dispose();
            }

            _disposed = true;
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
}
