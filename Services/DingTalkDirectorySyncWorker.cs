using AuditIt.Api.Models;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class DingTalkDirectorySyncWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<DingTalkDirectorySyncOptions> _options;
        private readonly ILogger<DingTalkDirectorySyncWorker> _logger;

        public DingTalkDirectorySyncWorker(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<DingTalkDirectorySyncOptions> options,
            ILogger<DingTalkDirectorySyncWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var initialDelay = Math.Max(0, _options.CurrentValue.InitialDelayMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(initialDelay), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_options.CurrentValue.Enabled)
                {
                    await SyncOnceAsync(stoppingToken);
                }

                var intervalHours = Math.Max(1, _options.CurrentValue.IntervalHours);
                try
                {
                    await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task SyncOnceAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<IUserService>();
                var opts = _options.CurrentValue;
                var result = await users.SyncDingTalkUsersAsync(new SyncDingTalkUsersDto
                {
                    DeactivateMissing = opts.DeactivateMissing
                }, "dingtalk-directory-sync", ct);

                _logger.LogInformation(
                    "DingTalk directory sync completed. Pulled={Pulled}, Created={Created}, Updated={Updated}, Deactivated={Deactivated}, Skipped={Skipped}.",
                    result.Pulled,
                    result.Created,
                    result.Updated,
                    result.Deactivated,
                    result.Skipped);

                foreach (var message in result.Messages.Take(10))
                {
                    _logger.LogWarning("DingTalk directory sync note: {Message}", message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DingTalk directory sync failed.");
            }
        }
    }
}
