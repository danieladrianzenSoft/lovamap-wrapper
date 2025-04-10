using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WrapperApi.Data;
using WrapperApi.Models;

namespace WrapperApi.Services
{
	public class HeartbeatService
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<HeartbeatService> _logger;
		private readonly HeartbeatCache _cache;

		public HeartbeatService(IServiceScopeFactory scopeFactory, ILogger<HeartbeatService> logger, HeartbeatCache cache)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
			_cache = cache;
		}

		public async Task FlushNowAsync(CancellationToken cancellationToken = default)
		{
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<DataContext>();

			foreach (var (jobId, (message, postedAt)) in _cache.GetAll())
			{
				var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id.ToString() == jobId, cancellationToken);
				if (job != null)
				{
					job.HeartbeatMessage = message;
					job.HeartbeatPostedAt = postedAt;
				}
			}

			await db.SaveChangesAsync(cancellationToken);
			_logger.LogInformation($"[HEARTBEAT] Flushed heartbeat updates to DB at {DateTime.UtcNow}");
		}
	}
}
