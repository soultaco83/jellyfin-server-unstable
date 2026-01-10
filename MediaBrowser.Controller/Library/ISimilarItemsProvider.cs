using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Base marker interface for similar items providers.
/// </summary>
public interface ISimilarItemsProvider
{
    /// <summary>
    /// Gets the name of the provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the type of the provider.
    /// </summary>
    MetadataPluginType Type { get; }
}
