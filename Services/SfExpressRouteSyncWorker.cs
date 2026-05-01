using AuditIt.Api.Models;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class SfExpressRouteSyncWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<SfExpressOptions> _options;
        private readonly ILogger<SfExpressRouteSyncWorker> _logger;

        public SfExpressRouteSyncWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<SfExpressOptions> options,
            ILogger<SfExpressRouteSyncWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = ResolveNextDelay(_options.CurrentValue);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var options = _options.CurrentValue;
                if (!options.DailySyncEnabled
                    || string.IsNullOrWhiteSpace(options.PartnerId)
                    || string.IsNullOrWhiteSpace(options.Secret))
                {
                    continue;
                }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var rentals = scope.ServiceProvider.GetRequiredService<IRentalService>();
                    var count = await rentals.SyncPendingSfRoutesAsync("sf-express-daily-sync", stoppingToken);
                    _logger.LogInformation("SF Express daily route sync completed. Synced shipments: {Count}", count);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SF Express daily route sync failed.");
                }
            }
        }

        private static TimeSpan ResolveNextDelay(SfExpressOptions options)
        {
            var zone = ResolveTimeZone(options.TimeZoneId);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            var targetLocal = new DateTimeOffset(
                nowLocal.Year,
                nowLocal.Month,
                nowLocal.Day,
                Math.Clamp(options.DailySyncHourLocal, 0, 23),
                Math.Clamp(options.DailySyncMinuteLocal, 0, 59),
                0,
                nowLocal.Offset);

            if (targetLocal <= nowLocal)
            {
                targetLocal = targetLocal.AddDays(1);
            }

            return targetLocal - nowLocal;
        }

        private static TimeZoneInfo ResolveTimeZone(string? configured)
        {
            foreach (var id in new[] { configured, "China Standard Time", "Asia/Shanghai" })
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch
                {
                    // Try the next well-known timezone id.
                }
            }

            return TimeZoneInfo.Local;
        }
    }
}
