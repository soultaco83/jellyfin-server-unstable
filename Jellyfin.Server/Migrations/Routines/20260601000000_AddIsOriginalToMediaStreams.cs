using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.ServerSetupApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Adds the IsOriginal column to the MediaStreamInfos table if it does not already exist.
/// This migration bridges the gap for databases where the EF Core migration was
/// marked as applied (via the designer stub) but the column was never actually created.
/// </summary>
[JellyfinMigration("2026-06-01T00:00:00", nameof(AddIsOriginalToMediaStreams))]
public class AddIsOriginalToMediaStreams : IAsyncMigrationRoutine
{
    private readonly IStartupLogger<AddIsOriginalToMediaStreams> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddIsOriginalToMediaStreams"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The startup logger.</param>
    public AddIsOriginalToMediaStreams(
        IDbContextFactory<JellyfinDbContext> dbContextFactory,
        IStartupLogger<AddIsOriginalToMediaStreams> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"MediaStreamInfos\" ADD COLUMN \"IsOriginal\" INTEGER NOT NULL DEFAULT 0",
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Added IsOriginal column to MediaStreamInfos table.");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Column already exists — nothing to do.
                _logger.LogInformation("IsOriginal column already exists in MediaStreamInfos table. Skipping.");
            }
        }
    }
}
