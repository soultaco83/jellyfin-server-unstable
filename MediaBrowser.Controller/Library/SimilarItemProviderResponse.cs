using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// A response from a remote similar items provider containing references and pagination info.
/// </summary>
public class SimilarItemProviderResponse
{
    /// <summary>
    /// Gets or sets the similar item references with their similarity scores.
    /// </summary>
    public required IReadOnlyList<SimilarItemReference> Matches { get; set; }

    /// <summary>
    /// Gets or sets the name of the provider that returned this response.
    /// </summary>
    public required string ProviderName { get; set; }

    /// <summary>
    /// Gets or sets the next page to fetch for pagination.
    /// Used by the manager to continue fetching from this provider if more results are needed.
    /// </summary>
    public int? NextPage { get; set; }

    /// <summary>
    /// Gets or sets how long this response should be cached.
    /// If null, the response will not be cached.
    /// </summary>
    public TimeSpan? CacheDuration { get; set; }
}
