using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WrapperApi.Data;
using WrapperApi.Models;

namespace WrapperApi.Services
{
	public class JobService
	{
		private readonly HeartbeatService _heartbeatService;
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly IWebHostEnvironment _env;
		private readonly HeartbeatCache _cache;
		private readonly string _inputDir;
		private readonly string _outputDir;
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly IBackgroundJobQueue _jobQueue;
		private HttpClient CreateClient() => _httpClientFactory.CreateClient();

		public JobService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env,
			HeartbeatCache cache, HeartbeatService heartbeatService, IHttpClientFactory httpClientFactory,
			IBackgroundJobQueue jobQueue, string inputDir, string outputDir
		)
		{
			_heartbeatService = heartbeatService;
			_scopeFactory = scopeFactory;
			_httpClientFactory = httpClientFactory;
			_jobQueue = jobQueue;
			_env = env;
			_cache = cache;
			_inputDir = inputDir;
			_outputDir = outputDir;
		}

		public async Task<JobRunResult> RunJobAsync(Job job, string dxValue, string? uploadUrl, string? uploadToken)
		{
			if (_env.IsDevelopment())
			{
				Console.WriteLine($"[DEV] Skipping execution for job {job.Id} ({job.JobId})");
				return new JobRunResult(false, false, "Skipped in development mode");
			}

			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<DataContext>();
			Job? dbJob = null;

			try
			{
				dbJob = await db.Jobs.FindAsync(job.Id);
				if (dbJob == null)
					return new JobRunResult(false, false, "Job not found in DB");

				// Get input and output directories
				var hostInputDir = Environment.GetEnvironmentVariable("HOST_INPUT_DIR") ?? _inputDir;
				var hostOutputDir = Environment.GetEnvironmentVariable("HOST_OUTPUT_DIR") ?? _outputDir;
				var baseName = Path.GetFileNameWithoutExtension(dbJob.FileName);
				var inputFilePath = Path.Combine(_inputDir, dbJob.FileName);

				// Early upload-only retry if computation already completed but upload didn't
				if (dbJob.Status == JobStatus.Completed && dbJob.JobUploadSucceeded == false)
				{
					if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(uploadToken))
					{
						Console.WriteLine($"[INFO] Job {dbJob.Id} completed and upload skipped (missing uploadUrl/uploadToken).");
						return new JobRunResult(true, false, null);
					}

					Console.WriteLine($"[INFO] Job {dbJob.Id} already computed — performing upload-only retry.");
					var outputFile = FindLatestOutputFile(hostOutputDir, baseName);

					if (string.IsNullOrEmpty(outputFile) || !File.Exists(outputFile))
					{
						var msg = $"Output file not found for upload-only retry: {outputFile ?? "(null)"}";
						Console.WriteLine($"[ERROR] {msg}");
						// transient — allow background retries
						return new JobRunResult(false, true, msg);
					}

					dbJob.ResultPath = outputFile;
					await db.SaveChangesAsync();
					return await TryUploadAsync(db, dbJob, uploadUrl, uploadToken, outputFile);
				}

				// Mark running
				dbJob.Status = JobStatus.Running;
				dbJob.StartedAt = DateTime.UtcNow;
				await db.SaveChangesAsync();

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
					// Input file not found, do not retry
					var msg = $"[INPUT] Input file not found after {maxRetries} attempts.";
					Console.WriteLine($"[ERROR] {msg}");
					dbJob.Status = JobStatus.Failed;
					dbJob.ErrorMessage = msg;
					await db.SaveChangesAsync();
					return new JobRunResult(false, false, msg);
				}

				// Compute
				var computeResult = dbJob.JobType == JobType.MeshProcessing
					? await RunMeshProcessingAsync(dbJob, hostInputDir, hostOutputDir)
					: await RunComputationAsync(
						dbJob,
						dxValue,
						hostInputDir,
						hostOutputDir,
						writePoreMeshes: dbJob.GenerateMesh);
				dbJob.CompletedAt = DateTime.UtcNow;
				try
				{
					await _heartbeatService.FlushNowAsync();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[WARN] Failed to flush heartbeat for job {dbJob.Id}: {ex.Message}");
				}

				if (!computeResult.Succeeded)
				{
					// Computation error, retry
					dbJob.Status = JobStatus.Failed;
					dbJob.ErrorMessage = computeResult.ErrorMessage ?? "[COMPUTE] Unknown error during computation";
					dbJob.StdErr = computeResult.Stderr;
					dbJob.StdOut = computeResult.Stdout;
					await db.SaveChangesAsync();
					return new JobRunResult(false, true, dbJob.ErrorMessage);
				}

				if (dbJob.JobType == JobType.MeshProcessing)
				{
					dbJob.StdErr = computeResult.Stderr;
					dbJob.StdOut = computeResult.Stdout;
					dbJob.Status = JobStatus.Completed;
					dbJob.CompletedAt = DateTime.UtcNow;

					// best-effort result path (entire job output directory)
					var meshOutputDir = Path.Combine(_outputDir, baseName);
					dbJob.ResultPath = Directory.Exists(meshOutputDir) ? meshOutputDir : null;
					dbJob.JobUploadSucceeded = true;
					await db.SaveChangesAsync();

					return new JobRunResult(true, false, null);
				}

				// Get output file (Lovamap)
				var foundOutputFile = FindLatestOutputFile(_outputDir, baseName);
				if (string.IsNullOrEmpty(foundOutputFile) || !File.Exists(foundOutputFile))
				{
					// If the output file is missing, treat as failure and retry
					var outputFileRoot = Path.Combine(_outputDir, baseName);
					var msg = $"[OUTPUT] Expected output file not found in {outputFileRoot}";
					Console.WriteLine($"{msg}");
					dbJob.Status = JobStatus.Failed;
					dbJob.ErrorMessage = msg;
					dbJob.StdErr = computeResult.Stderr;
					dbJob.StdOut = computeResult.Stdout;
					await db.SaveChangesAsync();
					return new JobRunResult(false, true, msg);
				}

				dbJob.StdErr = computeResult.Stderr;
				dbJob.StdOut = computeResult.Stdout;
				dbJob.Status = JobStatus.Completed;
				dbJob.CompletedAt = DateTime.UtcNow;
				dbJob.ResultPath = foundOutputFile;
				await db.SaveChangesAsync();

				await EnqueueMeshProcessingIfNeededAsync(db, dbJob);

				if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(uploadToken))
				{
					Console.WriteLine($"[INFO] Job {dbJob.Id} completed and upload skipped (missing uploadUrl/uploadToken).");
					dbJob.JobUploadSucceeded = false;
					await db.SaveChangesAsync();
					return new JobRunResult(true, false, null);
				}

				return await TryUploadAsync(db, dbJob, uploadUrl, uploadToken, foundOutputFile);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[EXCEPTION] Job {job.Id}: {ex.Message}");

				if (dbJob != null)
				{
					dbJob.Status = JobStatus.Failed;
					dbJob.ErrorMessage = ex.ToString(); // Or ex.Message for short
					await db.SaveChangesAsync();
				}
				return new JobRunResult(false, true, ex.Message);
			}
		}

		private async Task<(bool Succeeded, string Stdout, string Stderr, string? ErrorMessage)>
			RunComputationAsync(
				Job dbJob,
				string dxValue,
				string hostInputDir,
				string hostOutputDir,
				bool writePoreMeshes = true,
				IEnumerable<string>? extraLovamapArgs = null,
				int heartbeatInterval = 5000) // milliseconds
		{
			var containerName  = $"lovamap-job-{dbJob.Id}-{Guid.NewGuid()}";
			var platform       = Environment.GetEnvironmentVariable("PLATFORM")            ?? "linux/amd64";
			var wrapperApiUrl  = Environment.GetEnvironmentVariable("WRAPPER_API_URL")     ?? "http://localhost:8080";
			var heartbeatToken = Environment.GetEnvironmentVariable("HEARTBEAT_TOKEN")     ?? "sdf923lsd";
			var dockerNetwork  = Environment.GetEnvironmentVariable("DOCKER_NETWORK_NAME") ?? "lovamap_core_network";

			var imageName = Environment.GetEnvironmentVariable("LOVAMAP_IMAGE")
						?? "ghcr.io/seguralab/lovamap:v1.0.4";

			var lovamapCmd = Environment.GetEnvironmentVariable("LOVAMAP_CMD")
						?? "/app/entrypoint.sh";
			var useEntrypoint = string.Equals(lovamapCmd, "/app/entrypoint.sh", StringComparison.OrdinalIgnoreCase);

			var fileName = dbJob.FileName;

			var heartbeatEndpoint = $"{wrapperApiUrl.TrimEnd('/')}/heartbeat";

			// Build the entrypoint args
			var entrypointArgs = new List<string>();
			if (!useEntrypoint)
				entrypointArgs.Add(Quote(lovamapCmd));

			entrypointArgs.AddRange(new[]
			{
				"--input", Quote(fileName),
				"--dx", Quote(dxValue),
				"--heartbeat-endpoint", Quote(heartbeatEndpoint),
				"--heartbeat-interval", Quote(heartbeatInterval.ToString())
			});

			if (writePoreMeshes)
				entrypointArgs.Add("--write-pore-meshes");

			// Forward extra args to lovamap after "--"
			var extras = (new[] { "--heartbeat-metadata", $"jobId={dbJob.Id}" })
				.Concat(extraLovamapArgs ?? Enumerable.Empty<string>())
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.ToList();

			if (extras.Count > 0)
			{
				entrypointArgs.Add("--");
				entrypointArgs.AddRange(extras.Select(Quote));
			}

			var dockerArgs =
				"run --rm " +
				$"--platform {Quote(platform)} " +
				$"--name {Quote(containerName)} " +
				$"--network {Quote(dockerNetwork)} " +
				$"-v {Quote(hostInputDir)}:/app/input " +
				$"-v {Quote(hostOutputDir)}:/app/output " +
				$"{(useEntrypoint ? $"--entrypoint {Quote(lovamapCmd)} " : "")}" +
				$"-e LOVAMAP_INPUT_DIR=/app/input " +
				$"-e LOVAMAP_OUTPUT_DIR=/app/output " +
				$"-e HEARTBEAT_TOKEN={Quote(heartbeatToken)} " +
				$"-e LD_LIBRARY_PATH=/usr/local/lib:/lib/x86_64-linux-gnu:/usr/lib/x86_64-linux-gnu " +
				$"{Quote(imageName)} " +
				string.Join(" ", entrypointArgs);

			Console.WriteLine($"Using image: {imageName}, network: {dockerNetwork}, platform: {platform}");
			Console.WriteLine($"docker {dockerArgs}");

			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "docker",
					Arguments = dockerArgs,
					RedirectStandardOutput = true,
					RedirectStandardError  = true,
					UseShellExecute        = false,
					CreateNoWindow         = true
				}
			};

			try
			{
				_cache.MarkJobStarted(dbJob.Id.ToString());

				process.Start();

				var stdoutTask = process.StandardOutput.ReadToEndAsync();
				var stderrTask = process.StandardError.ReadToEndAsync();

				await process.WaitForExitAsync();

				var stdout = await stdoutTask;
				var stderr = await stderrTask;

				_cache.MarkJobCompleted(dbJob.Id.ToString());

				Console.WriteLine($"[DEBUG] Job {dbJob.Id} docker run exited with code {process.ExitCode}");
				Console.WriteLine($"[DEBUG] STDOUT: {stdout}");
				Console.WriteLine($"[DEBUG] STDERR: {stderr}");

				if (process.ExitCode != 0)
				{
					var err = string.IsNullOrWhiteSpace(stderr)
						? $"Docker run failed with exit code {process.ExitCode}."
						: stderr;

					return (false, stdout, stderr, err);
				}

				return (true, stdout, stderr, null);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Exception when running docker for job {dbJob.Id}: {ex}");
				return (false, string.Empty, string.Empty, ex.Message);
			}
		}

		private async Task<(bool Succeeded, string Stdout, string Stderr, string? ErrorMessage)>
			RunMeshProcessingAsync(
				Job dbJob,
				string hostInputDir,
				string hostOutputDir)
		{
			var containerName = $"mesh-processing-job-{dbJob.Id}-{Guid.NewGuid()}";
			var platform = Environment.GetEnvironmentVariable("PLATFORM") ?? "linux/amd64";
			var dockerNetwork = Environment.GetEnvironmentVariable("DOCKER_NETWORK_NAME") ?? "lovamap_core_network";

			var imageName = Environment.GetEnvironmentVariable("SEGMENTATION_WORKFLOWS_IMAGE");
			if (string.IsNullOrWhiteSpace(imageName))
				return (false, string.Empty, string.Empty, "SEGMENTATION_WORKFLOWS_IMAGE is not set.");

			var workflowName = Environment.GetEnvironmentVariable("SEG_WORKFLOW_NAME") ?? "unite_meshes";
			var workflowEntrypoint = Environment.GetEnvironmentVariable("SEG_WORKFLOW_ENTRYPOINT");
			var workflowScript = Environment.GetEnvironmentVariable("SEG_WORKFLOW_SCRIPT");
			var workflowMode = Environment.GetEnvironmentVariable("SEG_WORKFLOW_MODE");

			var baseName = Path.GetFileNameWithoutExtension(dbJob.FileName);
			var baseJobId = string.IsNullOrWhiteSpace(dbJob.JobId) ? dbJob.Id.ToString() : dbJob.JobId;
			var hostConfigPath = Path.Combine(hostOutputDir, baseName, "unite_meshes.json");
			var containerConfigPath = $"/app/output/{baseName}/unite_meshes.json";
			var hasConfig = File.Exists(hostConfigPath);

			var entrypointArgs = new List<string>();

			// If an explicit entrypoint is provided, use it and optionally pass a script path.
			// Otherwise assume the image entrypoint already runs run.py, and only pass args.
			if (!string.IsNullOrWhiteSpace(workflowEntrypoint))
			{
				if (!string.IsNullOrWhiteSpace(workflowScript))
					entrypointArgs.Add(Quote(workflowScript));
			}

			entrypointArgs.AddRange(new[] { "--workflow", Quote(workflowName) });

			if (hasConfig)
			{
				entrypointArgs.Add("--config");
				entrypointArgs.Add(Quote(containerConfigPath));
			}
			else
			{
				var meshOutputDir = $"/app/output/{baseName}";
				var meshInputDir = $"{meshOutputDir}/pores";
				var outputName = $"{baseJobId}_pores.glb";
				entrypointArgs.AddRange(new[]
				{
					"--set",
					$"input_dir={Quote(meshInputDir)}",
					$"output_dir={Quote(meshOutputDir)}",
					$"output_name={Quote(outputName)}"
				});
			}

			var dockerArgs =
				"run --rm " +
				$"--platform {Quote(platform)} " +
				$"--name {Quote(containerName)} " +
				$"--network {Quote(dockerNetwork)} " +
				$"-v {Quote(hostInputDir)}:/app/input " +
				$"-v {Quote(hostOutputDir)}:/app/output " +
				$"{(string.IsNullOrWhiteSpace(workflowEntrypoint) ? "" : $"--entrypoint {Quote(workflowEntrypoint)} ")}" +
				$"{(string.IsNullOrWhiteSpace(workflowMode) ? "" : $"-e SEG_WORKFLOW_MODE={Quote(workflowMode)} ")}" +
				$"{Quote(imageName)} " +
				string.Join(" ", entrypointArgs);

			Console.WriteLine($"Using segmentation image: {imageName}, network: {dockerNetwork}, platform: {platform}");
			Console.WriteLine($"docker {dockerArgs}");

			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "docker",
					Arguments = dockerArgs,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				}
			};

			try
			{
				process.Start();

				var stdoutTask = process.StandardOutput.ReadToEndAsync();
				var stderrTask = process.StandardError.ReadToEndAsync();

				await process.WaitForExitAsync();

				var stdout = await stdoutTask;
				var stderr = await stderrTask;

				Console.WriteLine($"[DEBUG] Mesh job {dbJob.Id} docker run exited with code {process.ExitCode}");
				Console.WriteLine($"[DEBUG] STDOUT: {stdout}");
				Console.WriteLine($"[DEBUG] STDERR: {stderr}");

				if (process.ExitCode != 0)
				{
					var err = string.IsNullOrWhiteSpace(stderr)
						? $"Docker run failed with exit code {process.ExitCode}."
						: stderr;

					return (false, stdout, stderr, err);
				}

				return (true, stdout, stderr, null);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Exception when running mesh job {dbJob.Id}: {ex}");
				return (false, string.Empty, string.Empty, ex.Message);
			}
		}

		private async Task<JobRunResult> TryUploadAsync(DataContext db, Job dbJob, string? uploadUrl, string? uploadToken, string filePath)
		{
			// make sure file exists
			if (!File.Exists(filePath))
				return new JobRunResult(false, false, $"Output file not found: {filePath}");

			var (uploadSucceeded, uploadShouldRetry, uploadErr) = await SendResultToGatewayAsync(uploadUrl, uploadToken, filePath);

			if (uploadSucceeded)
			{
				dbJob.JobUploadSucceeded = true;
				dbJob.ErrorMessage = null;
				await db.SaveChangesAsync();
				Console.WriteLine($"[SUCCESS] Job {dbJob.Id} uploaded successfully.");
				return new JobRunResult(true, false, null);
			}
			else
			{
				// computation succeeded but upload failed: record error but leave Status as Completed
				dbJob.ErrorMessage = $"[UPLOAD] Upload error: {uploadErr}";
				dbJob.JobUploadSucceeded = false;
				await db.SaveChangesAsync();
				Console.WriteLine($"[UPLOAD] Job {dbJob.Id} upload failed. shouldRetry={uploadShouldRetry}. err={uploadErr}");
				return new JobRunResult(false, uploadShouldRetry, uploadErr);
			}
		}

		private async Task EnqueueMeshProcessingIfNeededAsync(DataContext db, Job dbJob)
		{
			if (dbJob.JobType != JobType.Lovamap || !dbJob.GenerateMesh || dbJob.Status != JobStatus.Completed)
				return;

			var baseJobId = dbJob.JobId ?? dbJob.Id.ToString();
			var meshJobPrefix = $"{baseJobId}-mesh";

			var existingMesh = await db.Jobs.AnyAsync(j =>
				j.JobType == JobType.MeshProcessing &&
				j.JobId != null &&
				j.JobId.StartsWith(meshJobPrefix) &&
				(j.Status == JobStatus.Pending || j.Status == JobStatus.Running || j.Status == JobStatus.Completed));

			if (existingMesh)
			{
				Console.WriteLine($"[QUEUE] Mesh processing already exists for job {dbJob.Id}; skipping auto-enqueue.");
				return;
			}

			var meshJobId = meshJobPrefix;
			if (await db.Jobs.AnyAsync(j => j.JobId == meshJobId))
				meshJobId = $"{meshJobPrefix}-{Guid.NewGuid():N}";

			var meshJob = new Job
			{
				JobId = meshJobId,
				FileName = dbJob.FileName,
				JobType = JobType.MeshProcessing,
				Status = JobStatus.Pending,
				SubmittedAt = DateTime.UtcNow,
				InitiatorType = dbJob.InitiatorType,
				UserId = dbJob.UserId,
				ClientId = dbJob.ClientId,
				DxValue = dbJob.DxValue,
				GenerateMesh = false
			};

			db.Jobs.Add(meshJob);
			await db.SaveChangesAsync();

			var meshDxValue = meshJob.DxValue ?? "4.0";
			_jobQueue.Enqueue(meshJob, meshDxValue, uploadUrl: null, uploadToken: null);
			Console.WriteLine($"[QUEUE] Enqueued mesh processing job {meshJob.Id} for lovamap job {dbJob.Id}.");
		}
		
		private string? FindLatestOutputFile(string outputRootDir, string baseName)
		{
			if (string.IsNullOrEmpty(outputRootDir) || string.IsNullOrEmpty(baseName))
				return null;

			// the directory where lovamap should put outputs for this job
			var jobOutputDir = Path.Combine(outputRootDir, baseName);
			if (!Directory.Exists(jobOutputDir))
				return null;

			// 1) Look for files matching output_YYYYMMDD_HHMMSS.ext
			//    Use a pattern to only consider files with "output_" prefix.
			var candidates = Directory.EnumerateFiles(jobOutputDir, "output_*.*", SearchOption.TopDirectoryOnly)
				.Where(p =>
					p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
					p.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (candidates.Count == 0)
			{
				// fallback: consider any file in the directory (rare)
				candidates = Directory.EnumerateFiles(jobOutputDir, "*.*", SearchOption.TopDirectoryOnly).ToList();
				if (candidates.Count == 0) return null;
			}

			// 2) Prefer lexicographic ordering on basename if filenames contain timestamp in format yyyyMMdd_HHmmss
			//    e.g. output_20251013_170843.json
			//    We'll try to parse timestamps from the filename and use that ordering first; if none parse, fallback to LastWriteTimeUtc.
			var parsed = new List<(string Path, DateTime? Ts)>();
			foreach (var p in candidates)
			{
				var fname = Path.GetFileName(p); // e.g., output_20251013_170843.json
				var m = System.Text.RegularExpressions.Regex.Match(fname, @"output_(\d{8}_\d{6})");
				if (m.Success)
				{
					if (DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
					{
						parsed.Add((p, dt));
						continue;
					}
				}
				parsed.Add((p, null));
			}

			// If at least one file had a parsed timestamp, pick the one with the max parsed timestamp (non-null).
			if (parsed.Any(x => x.Ts.HasValue))
			{
				var best = parsed.Where(x => x.Ts.HasValue).OrderByDescending(x => x.Ts!.Value).First();
				return best.Path;
			}

			// Otherwise fallback to last write time
			var fallback = candidates.OrderByDescending(p => File.GetLastWriteTimeUtc(p)).FirstOrDefault();
			return fallback;
		}

		private static string Quote(string s)
		{
			if (string.IsNullOrEmpty(s)) return "\"\"";
			return s.Contains(' ') || s.Contains('"')
				? "\"" + s.Replace("\"", "\\\"") + "\""
				: s;
		}
		
		private async Task<(bool Success, bool shouldRetry, string? Error)> SendResultToGatewayAsync(
			string? uploadUrl,
			string? uploadToken,
			string filePath,
			int maxRetries = 3,
			int delayMs = 1000)
		{
			if (!File.Exists(filePath))
				return (false, false, $"Output file not found: {filePath}");

			if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(uploadToken))
				return (false, false, $"Invalid values for uploadUrl ({uploadUrl}) or uploadToken ({uploadToken})");

			// Optionally compute SHA256 digest first (so gateway can verify)
			string? digestHeader = null;
			try
			{
				using var fs = File.OpenRead(filePath);
				using var sha = System.Security.Cryptography.SHA256.Create();
				var hash = await sha.ComputeHashAsync(fs);
				digestHeader = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
			}
			catch (Exception ex)
			{
				// if hash fails, we can still proceed without Digest
				Console.WriteLine($"[WARN] Failed to compute SHA256 for {filePath}: {ex.Message}");
			}

			for (int attempt = 1; attempt <= maxRetries; attempt++)
			{
				try
				{
					using var fileStream = File.OpenRead(filePath);
					using var content = new StreamContent(fileStream);
					content.Headers.ContentLength = new FileInfo(filePath).Length;

					var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
					{
						Content = content
					};
					request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", uploadToken);

					if (!string.IsNullOrEmpty(digestHeader))
					{
						// "Digest" header format: sha256=<hex>
						request.Headers.TryAddWithoutValidation("Digest", $"sha256={digestHeader}");
					}

					var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)); // tune as needed
					var client = CreateClient();
					var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

					if (response.IsSuccessStatusCode)
						return (true, false, null);

					var body = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"[WARN] Upload attempt {attempt} failed: {response.StatusCode} {body}");

					// retry only on 5xx or network-like failures, 4xx errors 
					// are permanent errors, so non-retriable
					if ((int)response.StatusCode >= 500)
						if (attempt < maxRetries)
							await Task.Delay(delayMs * attempt);
						else
							return (false, true, $"Upload Failed: {response.StatusCode} - {body}");
					else
						return (false, false, $"Upload failed: {response.StatusCode} - {body}");
				}
				catch (TaskCanceledException tce)
				{
					// treat timeouts as transient
					Console.WriteLine($"[WARN] Upload attempt {attempt} timeout: {tce.Message}");
					if (attempt < maxRetries) await Task.Delay(delayMs * attempt);
					else return (false, true, tce.Message);
				}
				catch (Exception ex)
				{
					// treat last-exception as transient by default
					Console.WriteLine($"[WARN] Upload attempt {attempt} exception: {ex.Message}");
					if (attempt < maxRetries)
						await Task.Delay(delayMs * attempt);
					else
						return (false, true, ex.Message);
				}
			}

			return (false, true, "Exceeded max retries");
		}
	}
}
