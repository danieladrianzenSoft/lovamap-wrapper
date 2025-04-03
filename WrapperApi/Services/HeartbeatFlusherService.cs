using WrapperApi.Data;
using Microsoft.EntityFrameworkCore;

namespace WrapperApi.Services
{
    public class HeartbeatFlusherService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly HeartbeatCache _cache;
        private readonly ILogger<HeartbeatFlusherService> _logger;

        public HeartbeatFlusherService(IServiceProvider services, HeartbeatCache cache, ILogger<HeartbeatFlusherService> logger)
        {
            _services = services;
            _cache = cache;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_cache.HasActiveJobs())
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                    foreach (var (jobId, (message, postedAt)) in _cache.GetAll())
                    {
                        var job = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, stoppingToken);
                        if (job != null)
                        {
                            job.HeartbeatMessage = message;
                            job.HeartbeatPostedAt = postedAt;
                        }
                    }

                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"[HEARTBEAT] Flushed heartbeat updates to DB at {DateTime.UtcNow}");
                }
                else
                {
                    _logger.LogInformation("[HEARTBEAT] No active jobs, skipping flush.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); ;
            }
        }
    }
}

