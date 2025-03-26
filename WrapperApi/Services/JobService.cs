using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WrapperApi.Data;
using WrapperApi.Models;

public class JobService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _env;
	private readonly string _inputDir;
    private readonly string _outputDir;

    public JobService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, string inputDir, string outputDir)
    {
        _scopeFactory = scopeFactory;
        _env = env;
		_inputDir = inputDir;
        _outputDir = outputDir;
    }

    public async Task<(bool Success, string? ErrorMessage)> RunJobAsync(Job job, string dxValue)
	{
		if (_env.IsDevelopment())
		{
			Console.WriteLine($"[DEV] Skipping execution for job {job.Id} ({job.JobId})");
			return (false, "Skipped in development mode");
		}

		using var scope = _scopeFactory.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<DataContext>();

		try
		{
			var dbJob = await db.Jobs.FindAsync(job.Id);
			if (dbJob == null)
				return (false, "Job not found in DB");

			dbJob.Status = JobStatus.Running;
			dbJob.StartedAt = DateTime.UtcNow;
			await db.SaveChangesAsync();

			var fileName = dbJob.FileName;
			var containerName = $"lovamap-job-{job.Id}-{Guid.NewGuid()}";
			var targetArch = Environment.GetEnvironmentVariable("TARGETARCH") ?? "amd64";
			var platform = Environment.GetEnvironmentVariable("PLATFORM") ?? "linux/amd64";
			var imageName = $"lovamap:{targetArch}";
			var hostInputDir = Environment.GetEnvironmentVariable("HOST_INPUT_DIR") ?? _inputDir;
			var hostOutputDir = Environment.GetEnvironmentVariable("HOST_OUTPUT_DIR") ?? _outputDir;

			var inputFilePath = Path.Combine(_inputDir, fileName);
			const int maxRetries = 5;
			const int delayMs = 100;
			int attempt = 0;

			while (!File.Exists(inputFilePath) && attempt < maxRetries)
			{
				Console.WriteLine($"[WAIT] Input file not found yet: {inputFilePath}, retrying... ({attempt + 1}/{maxRetries})");
				await Task.Delay(delayMs);
				attempt++;
			}

			if (!File.Exists(inputFilePath))
			{
				var msg = $"Input file not found after {maxRetries} attempts.";
				Console.WriteLine($"[ERROR] {msg}");
				dbJob.Status = JobStatus.Failed;
				dbJob.ErrorMessage = msg;
				await db.SaveChangesAsync();
				return (false, msg);
			}

			Console.WriteLine($"[SUCCESS] Input file found: {inputFilePath}");

			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "docker",
					Arguments = $"run --rm --platform {platform} --name {containerName} " +
								$"-v {hostInputDir}:/app/input " +
								$"-v {hostOutputDir}:/app/output " +
								$"-e LOVAMAP_INPUT_DIR=/app/input " +
								$"-e LOVAMAP_OUTPUT_DIR=/app/output " +
								$"{imageName} {fileName} {dxValue}",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};

			process.Start();
			var output = await process.StandardOutput.ReadToEndAsync();
			var error = await process.StandardError.ReadToEndAsync();
			process.WaitForExit();

			Console.WriteLine($"[DEBUG] Job {job.Id} docker run exited with code {process.ExitCode}");
			Console.WriteLine($"[DEBUG] STDOUT: {output}");
			Console.WriteLine($"[DEBUG] STDERR: {error}");

			dbJob.CompletedAt = DateTime.UtcNow;

			if (process.ExitCode == 0)
			{
				dbJob.Status = JobStatus.Completed;
				await db.SaveChangesAsync();
				return (true, null);
			}
			else
			{
				dbJob.Status = JobStatus.Failed;
				dbJob.ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Unknown error occurred during execution." : error;
				await db.SaveChangesAsync();
				return (false, dbJob.ErrorMessage);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[EXCEPTION] Job {job.Id}: {ex.Message}");

			var dbJob = await db.Jobs.FindAsync(job.Id);
			if (dbJob != null)
			{
				dbJob.Status = JobStatus.Failed;
				dbJob.ErrorMessage = ex.ToString(); // Or ex.Message for short
				await db.SaveChangesAsync();
			}
			return (false, ex.Message);
		}
	}
}