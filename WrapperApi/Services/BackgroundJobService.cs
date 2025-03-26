using WrapperApi.Data;
using WrapperApi.Models;
using WrapperApi.Services;

public class BackgroundJobService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
	private readonly IWebHostEnvironment _env;
    private readonly IBackgroundJobQueue _jobQueue;
    private readonly JobService _jobService;
	private readonly SemaphoreSlim _semaphore;
	private const int MaxConcurrentJobs = 1;

    public BackgroundJobService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IBackgroundJobQueue jobQueue, JobService jobService)
    {
        _scopeFactory = scopeFactory;
        _jobQueue = jobQueue;
        _jobService = jobService;
		_env = env;
		_semaphore = new SemaphoreSlim(MaxConcurrentJobs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (_env.IsDevelopment())
		{
			Console.WriteLine("[DEV] Background job service disabled in Development mode.");
			return;
		}

		Console.WriteLine("[QUEUE] Background job service started.");

		await foreach (var (job, dxValue) in _jobQueue.ReadAllAsync(stoppingToken))
        {
            await _semaphore.WaitAsync(stoppingToken); // wait for an available slot

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                    var dbJob = await db.Jobs.FindAsync(job.Id);
                    if (dbJob == null)
                    {
                        Console.WriteLine($"[QUEUE] Job {job.Id} not found in DB. Skipping.");
                        return;
                    }

                    Console.WriteLine($"[QUEUE] Executing job {job.Id} from queue.");

                    var result = await _jobService.RunJobAsync(dbJob, dxValue);

                    if (!result.Success && dbJob.RetryCount < dbJob.MaxRetries)
                    {
                        dbJob.RetryCount++;
                        dbJob.Status = JobStatus.Pending;
                        await db.SaveChangesAsync();

                        Console.WriteLine($"[RETRY] Re-enqueuing job {dbJob.Id} for retry {dbJob.RetryCount}");
                        _jobQueue.Enqueue(dbJob, dxValue);
                    }
                    else if (!result.Success)
                    {
                        dbJob.Status = JobStatus.Failed;
                        dbJob.CompletedAt = DateTime.UtcNow;
                        dbJob.ErrorMessage = "Max retries reached";
                        await db.SaveChangesAsync();

                        Console.WriteLine($"[FAIL] Job {dbJob.Id} failed after max retries.");
                    }
                    else
                    {
                        Console.WriteLine($"[SUCCESS] Job {dbJob.Id} completed.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Exception running job {job.Id}: {ex.Message}");
                }
                finally
                {
                    _semaphore.Release(); // release the slot for the next job
                }
            }, stoppingToken);
        }
	}
}
