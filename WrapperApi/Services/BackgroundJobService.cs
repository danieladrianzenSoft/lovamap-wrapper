using WrapperApi.Data;
using WrapperApi.Models;
using Microsoft.EntityFrameworkCore;

namespace WrapperApi.Services
{
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

            await foreach (var (job, dxValue, uploadUrl, uploadToken) in _jobQueue.ReadAllAsync(stoppingToken))
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

                        // if ((job.Status != JobStatus.Pending) || (job.Status != JobStatus.Failed))
                        // {
                        //     Console.WriteLine($"[QUEUE] Job {job.Id} not ready for computation. Skipping.");
                        //     return;
                        // }

                        Console.WriteLine($"[QUEUE] Executing job {job.Id} from queue.");

                        var result = await _jobService.RunJobAsync(dbJob, dxValue, uploadUrl, uploadToken);
                        
                        // refresh the job entity from the DB so we don't work with stale tracked entity
                        var freshJob = await db.Jobs.FindAsync(dbJob.Id);
                        if (freshJob == null)
                        {
                            Console.WriteLine($"[QUEUE] Job {dbJob.Id} not found after run. Skipping updates.");
                            return;
                        }

                        if (!result.Success && result.ShouldRetry && freshJob.RetryCount < freshJob.MaxRetries)
                        {
                            freshJob.RetryCount++;

                            if (freshJob.Status == JobStatus.Completed && freshJob.JobUploadSucceeded == false)
                            {
                                Console.WriteLine($"[RETRY-UPLOAD] Upload-only re-enqueue for job {freshJob.Id} (upload retry {freshJob.RetryCount})");
                            }
                            else
                            {
                                freshJob.Status = JobStatus.Pending;
                                Console.WriteLine($"[RETRY-COMPUTE] Re-enqueuing job {freshJob.Id} for recompute (retry {freshJob.RetryCount})");
                            }
                            await db.SaveChangesAsync();

                            _jobQueue.Enqueue(freshJob, dxValue, uploadUrl, uploadToken);
                        }
                        else if (!result.Success)
                        {
                            if (freshJob.Status == JobStatus.Completed && freshJob.JobUploadSucceeded == false)
                            {
                                Console.WriteLine($"[WARN] Job {freshJob.Id} completed but upload failed; leaving status Completed.");
                            }
                            else
                            {
                                freshJob.Status = JobStatus.Failed;
                                freshJob.CompletedAt = DateTime.UtcNow;
                                freshJob.ErrorMessage = result.ErrorMessage ?? "Max retries reached";
                                await db.SaveChangesAsync();

                                Console.WriteLine($"[FAIL] Job {freshJob.Id} failed after max retries.");
                            }
                        }
                        if (result.Success)
                            Console.WriteLine($"[SUCCESS] Job {freshJob.Id} completed.");
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
}
