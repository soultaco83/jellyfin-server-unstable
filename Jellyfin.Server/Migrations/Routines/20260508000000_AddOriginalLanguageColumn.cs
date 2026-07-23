using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.ServerSetupApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Adds the OriginalLanguage column to the BaseItems table if it does not already exist.
/// This migration bridges the gap for databases created before the OriginalLanguage property
/// was added to the BaseItemEntity model without a corresponding EF Core migration.
/// </summary>
[JellyfinMigration("2026-05-06T00:00:00", nameof(AddOriginalLanguageColumn))]
public class AddOriginalLanguageColumn : IAsyncMigrationRoutine
{
    private readonly IStartupLogger<AddOriginalLanguageColumn> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddOriginalLanguageColumn"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="logger">The startup logger.</param>
    public AddOriginalLanguageColumn(
        IDbContextFactory<JellyfinDbContext> dbContextFactory,
        IStartupLogger<AddOriginalLanguageColumn> logger)
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
                    "ALTER TABLE \"BaseItems\" ADD COLUMN \"OriginalLanguage\" TEXT NULL",
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Added OriginalLanguage column to BaseItems table.");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Column already exists — nothing to do.
                _logger.LogInformation("OriginalLanguage column already exists in BaseItems table. Skipping.");
            }
        }
    }
}
