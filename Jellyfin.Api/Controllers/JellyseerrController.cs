using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Jellyseerr integration controller.
/// </summary>
[Route("jellyseerr")]
[Authorize]
public class JellyseerrController : BaseJellyfinApiController
{
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JellyseerrController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrController"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{JellyseerrController}"/> interface.</param>
    public JellyseerrController(
        IServerConfigurationManager serverConfigurationManager,
        IHttpClientFactory httpClientFactory,
        ILogger<JellyseerrController> logger)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Check Jellyseerr connection status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Jellyseerr status returned.</response>
    /// <response code="503">Unable to connect to Jellyseerr.</response>
    /// <returns>Status information.</returns>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var config = _serverConfigurationManager.Configuration;
        
        if (!config.JellyseerrEnabled)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { connected = false, message = "Jellyseerr integration is disabled" });
        }

        var workingUrl = await GetWorkingServerUrlAsync(cancellationToken).ConfigureAwait(false);
        
        if (workingUrl == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { connected = false, message = "Unable to connect to any configured Jellyseerr server" });
        }

        return Ok(new { connected = true, serverUrl = workingUrl });
    }

    /// <summary>
    /// Search for media in Jellyseerr.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="page">Page number (default 1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Search results returned.</response>
    /// <response code="400">Invalid search query.</response>
    /// <response code="503">Unable to search Jellyseerr.</response>
    /// <returns>Search results.</returns>
    [HttpGet("search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Search(
        [FromQuery] [Required] string query,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query parameter is required" });
        }

        var workingUrl = await GetWorkingServerUrlAsync(cancellationToken).ConfigureAwait(false);
        if (workingUrl == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Unable to connect to Jellyseerr" });
        }

        try
        {
            using var httpClient = CreateHttpClient();
            var searchUrl = $"{workingUrl}/api/v1/search?query={Uri.EscapeDataString(query)}&page={page}";
            
            var response = await httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Jellyseerr");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Failed to search Jellyseerr" });
        }
    }

    /// <summary>
    /// Get media details from Jellyseerr.
    /// </summary>
    /// <param name="mediaType">Media type (movie or tv).</param>
    /// <param name="mediaId">TMDB media ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Media details returned.</response>
    /// <response code="404">Media not found.</response>
    /// <returns>Media details.</returns>
    [HttpGet("{mediaType}/{mediaId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetMediaDetails(
        [FromRoute] [Required] string mediaType,
        [FromRoute] [Required] int mediaId,
        CancellationToken cancellationToken = default)
    {
        var workingUrl = await GetWorkingServerUrlAsync(cancellationToken).ConfigureAwait(false);
        if (workingUrl == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Unable to connect to Jellyseerr" });
        }

        try
        {
            using var httpClient = CreateHttpClient();
            var detailsUrl = $"{workingUrl}/api/v1/{mediaType}/{mediaId}";
            
            var response = await httpClient.GetAsync(detailsUrl, cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Jellyseerr media details");
            return NotFound();
        }
    }

    /// <summary>
    /// Request media in Jellyseerr.
    /// </summary>
    /// <param name="requestDto">Request data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Request successful.</response>
    /// <response code="400">Invalid request.</response>
    /// <returns>Request result.</returns>
    [HttpPost("request")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> RequestMedia(
        [FromBody] [Required] JellyseerrRequestDto requestDto,
        CancellationToken cancellationToken = default)
    {
        var workingUrl = await GetWorkingServerUrlAsync(cancellationToken).ConfigureAwait(false);
        if (workingUrl == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Unable to connect to Jellyseerr" });
        }

        try
        {
            // Get current user ID
            var userId = User.GetUserId();
            
            using var httpClient = CreateHttpClient();
            var requestUrl = $"{workingUrl}/api/v1/request";

            var payload = new
            {
                mediaType = requestDto.MediaType,
                mediaId = requestDto.MediaId,
                userId = userId.ToString("N")
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(requestUrl, jsonContent, cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Jellyseerr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return BadRequest(new { error = "Failed to request media in Jellyseerr" });
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting media in Jellyseerr");
            return BadRequest(new { error = "Failed to request media" });
        }
    }

    /// <summary>
    /// Get the first working Jellyseerr server URL.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Working server URL or null.</returns>
    private async Task<string?> GetWorkingServerUrlAsync(CancellationToken cancellationToken)
    {
        var config = _serverConfigurationManager.Configuration;
        
        if (!config.JellyseerrEnabled || config.JellyseerrServerUrls.Length == 0)
        {
            return null;
        }

        foreach (var url in config.JellyseerrServerUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                using var httpClient = CreateHttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var statusUrl = $"{url.TrimEnd('/')}/api/v1/status";
                var response = await httpClient.GetAsync(statusUrl, cancellationToken).ConfigureAwait(false);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Successfully connected to Jellyseerr at {Url}", url);
                    return url.TrimEnd('/');
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to connect to Jellyseerr at {Url}", url);
            }
        }

        return null;
    }

    /// <summary>
    /// Create an HTTP client with appropriate headers.
    /// </summary>
    /// <returns>Configured HttpClient.</returns>
    private HttpClient CreateHttpClient()
    {
        var config = _serverConfigurationManager.Configuration;
        var httpClient = _httpClientFactory.CreateClient();
        
        httpClient.Timeout = TimeSpan.FromSeconds(config.JellyseerrTimeoutSeconds);
        
        if (!string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);
        }

        return httpClient;
    }
}

/// <summary>
/// Jellyseerr request DTO.
/// </summary>
public class JellyseerrRequestDto
{
    /// <summary>
    /// Gets or sets the media type (movie or tv).
    /// </summary>
    [Required]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDB media ID.
    /// </summary>
    [Required]
    public int MediaId { get; set; }
}