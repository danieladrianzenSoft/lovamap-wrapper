using System.Collections.Concurrent;
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
		private readonly ConcurrentDictionary<int, string> _runningContainers = new();
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

			var disableUpload = Environment.GetEnvironmentVariable("DISABLE_RESULT_UPLOAD")
				?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

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
					if (disableUpload || string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(uploadToken))
					{
						Console.WriteLine($"[INFO] Job {dbJob.Id} completed — upload skipped (uploads disabled or no URL).");
						dbJob.JobUploadSucceeded = true;
						await db.SaveChangesAsync();
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
				var computeResult = dbJob.JobType switch
				{
					JobType.MeshProcessing => await RunMeshProcessingAsync(dbJob, hostInputDir, hostOutputDir),
					JobType.ParticleSegmentation => await RunParticleSegmentationAsync(dbJob, hostInputDir, hostOutputDir),
					_ => await RunComputationAsync(
						dbJob,
						dxValue,
						hostInputDir,
						hostOutputDir,
						writePoreMeshes: dbJob.GenerateMesh)
				};
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
					// Check if the job was cancelled while running
					await db.Entry(dbJob).ReloadAsync();
					if (dbJob.Status == JobStatus.Stopped)
						return new JobRunResult(false, false, "Job was cancelled");

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

					// Mesh processing is a terminal job — sync everything to NFS and clean up local
					FireAndForgetNfsSync(baseName, dbJob.FileName, dbJob.Id, removeOutput: true);

					return new JobRunResult(true, false, null);
				}

				if (dbJob.JobType == JobType.ParticleSegmentation)
				{
					dbJob.StdErr = computeResult.Stderr;
					dbJob.StdOut = computeResult.Stdout;
					dbJob.Status = JobStatus.Completed;
					dbJob.CompletedAt = DateTime.UtcNow;

					// Find output JSON in output/{baseName}/ directory
					var segOutputDir = Path.Combine(_outputDir, baseName);
					string? segResultPath = null;
					if (Directory.Exists(segOutputDir))
					{
						segResultPath = Directory.EnumerateFiles(segOutputDir, "*.json", SearchOption.TopDirectoryOnly)
							.OrderByDescending(f => File.GetLastWriteTimeUtc(f))
							.FirstOrDefault();
					}

					dbJob.ResultPath = segResultPath;
					dbJob.JobUploadSucceeded = true;
					await db.SaveChangesAsync();

					await EnqueueMeshGenerationAfterSegmentationAsync(db, dbJob);

					// Don't remove output — mesh_generation needs the segmentation files
					FireAndForgetNfsSync(baseName, dbJob.FileName, dbJob.Id, removeOutput: false);

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
				await EnqueueParticleMeshGenerationIfNeededAsync(db, dbJob);

				// Sync to NFS — don't remove output if mesh processing will need it
				FireAndForgetNfsSync(baseName, dbJob.FileName, dbJob.Id, removeOutput: !dbJob.GenerateMesh && !dbJob.GenerateParticleMesh);

				if (disableUpload || string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(uploadToken))
				{
					Console.WriteLine($"[INFO] Job {dbJob.Id} completed — upload skipped (uploads disabled or no URL).");
					dbJob.JobUploadSucceeded = true;
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

		public async Task<bool> CancelJobAsync(int jobId)
		{
			// Stop the Docker container if running
			if (_runningContainers.TryGetValue(jobId, out var containerName))
			{
				Console.WriteLine($"[CANCEL] Stopping container {containerName} for job {jobId}");
				try
				{
					var stopProcess = new Process
					{
						StartInfo = new ProcessStartInfo
						{
							FileName = "docker",
							Arguments = $"stop -t 10 {containerName}",
							RedirectStandardOutput = true,
							RedirectStandardError = true,
							UseShellExecute = false,
							CreateNoWindow = true
						}
					};
					stopProcess.Start();
					await stopProcess.WaitForExitAsync();
					Console.WriteLine($"[CANCEL] Container {containerName} stopped (exit code {stopProcess.ExitCode})");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[CANCEL] Failed to stop container {containerName}: {ex.Message}");
				}
			}

			// Mark job as Stopped in DB
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<DataContext>();
			var dbJob = await db.Jobs.FindAsync(jobId);
			if (dbJob == null)
				return false;

			dbJob.Status = JobStatus.Stopped;
			dbJob.CompletedAt = DateTime.UtcNow;
			dbJob.ErrorMessage = "Job cancelled by user";
			await db.SaveChangesAsync();
			Console.WriteLine($"[CANCEL] Job {jobId} marked as Stopped");
			return true;
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
			var runAsUid = Environment.GetEnvironmentVariable("JOB_RUN_AS_UID");
			var runAsGid = Environment.GetEnvironmentVariable("JOB_RUN_AS_GID");

			var imageName = Environment.GetEnvironmentVariable("LOVAMAP_IMAGE")
						?? "ghcr.io/seguralab/lovamap:latest";

			var entrypointPath =
				Environment.GetEnvironmentVariable("LOVAMAP_ENTRYPOINT") ??
				Environment.GetEnvironmentVariable("LOVAMAP_CMD") ??
				"/app/entrypoint.sh";

			var fileName = dbJob.FileName;

			var heartbeatEndpoint = $"{wrapperApiUrl.TrimEnd('/')}/heartbeat";

			// Build the entrypoint args (always use entrypoint.sh)
			var entrypointArgs = new List<string>();

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
				$"{(string.IsNullOrWhiteSpace(runAsUid) ? "" : $"--user {Quote(runAsGid is null or "" ? runAsUid : $"{runAsUid}:{runAsGid}")} ")}" +
				$"-v {Quote(hostInputDir)}:/app/input " +
				$"-v {Quote(hostOutputDir)}:/app/output " +
				$"--entrypoint {Quote(entrypointPath)} " +
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
				_runningContainers[dbJob.Id] = containerName;

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
			finally
			{
				_runningContainers.TryRemove(dbJob.Id, out _);
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
			var runAsUid = Environment.GetEnvironmentVariable("JOB_RUN_AS_UID");
			var runAsGid = Environment.GetEnvironmentVariable("JOB_RUN_AS_GID");

			var imageName = Environment.GetEnvironmentVariable("SEGMENTATION_WORKFLOWS_IMAGE");
			if (string.IsNullOrWhiteSpace(imageName))
				return (false, string.Empty, string.Empty, "SEGMENTATION_WORKFLOWS_IMAGE is not set.");

			var workflowEntrypoint = Environment.GetEnvironmentVariable("SEG_WORKFLOW_ENTRYPOINT");
			var workflowScript = Environment.GetEnvironmentVariable("SEG_WORKFLOW_SCRIPT");
			var workflowMode = Environment.GetEnvironmentVariable("SEG_WORKFLOW_MODE");

			var baseName = Path.GetFileNameWithoutExtension(dbJob.FileName);
			var baseJobId = string.IsNullOrWhiteSpace(dbJob.JobId) ? dbJob.Id.ToString() : dbJob.JobId;

			// Determine which workflow to run based on job's MeshWorkflow field
			var workflowName = dbJob.MeshWorkflow ?? Environment.GetEnvironmentVariable("SEG_WORKFLOW_NAME") ?? "unite_meshes";

			var entrypointArgs = new List<string>();

			// If an explicit entrypoint is provided, use it and optionally pass a script path.
			// Otherwise assume the image entrypoint already runs run.py, and only pass args.
			if (!string.IsNullOrWhiteSpace(workflowEntrypoint))
			{
				if (!string.IsNullOrWhiteSpace(workflowScript))
					entrypointArgs.Add(Quote(workflowScript));
			}

			entrypointArgs.AddRange(new[] { "--workflow", Quote(workflowName) });

			if (workflowName == "mesh_generation")
			{
				// mesh_generation workflow: converts segmentation JSON to GLB meshes
				// Use wrapperapi's local mount paths for file operations
				// (hostInputDir/hostOutputDir are HOST paths, only valid for docker -v args)
				var localJobOutputDir = Path.Combine(_outputDir, baseName);
				if (!Directory.Exists(localJobOutputDir))
				{
					Directory.CreateDirectory(localJobOutputDir);
					// Make writable by the job container's appuser
					File.SetUnixFileMode(localJobOutputDir,
						UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
						UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
						UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
				}

				// If the input file is in the input dir (standalone upload), copy it
				// to the output dir so the workflow container can find it
				var localInputFile = Path.Combine(_inputDir, dbJob.FileName);
				var inputExt = Path.GetExtension(localInputFile);
				var meshGenExtensions = new[] { ".json", ".dat", ".npz", ".txt" };
				if (File.Exists(localInputFile) && meshGenExtensions.Any(e => inputExt.Equals(e, StringComparison.OrdinalIgnoreCase)))
				{
					var destFile = Path.Combine(localJobOutputDir, dbJob.FileName);
					if (!File.Exists(destFile))
						File.Copy(localInputFile, destFile);
				}

				var localConfigPath = Path.Combine(_outputDir, baseName, "mesh_generation.json");
				var containerConfigPath = $"/app/output/{baseName}/mesh_generation.json";
				var hasConfig = File.Exists(localConfigPath);

				if (hasConfig)
				{
					entrypointArgs.Add("--config");
					entrypointArgs.Add(Quote(containerConfigPath));
				}
				else
				{
					var meshInputDir = $"/app/output/{baseName}";
					var meshOutputDir = $"/app/output/{baseName}";

					var inputExtNorm = Path.GetExtension(dbJob.FileName).TrimStart('.').ToLowerInvariant();
					var fileType = inputExtNorm switch
					{
						"json" => "json",
						"dat" => "dat",
						"npz" => "npz",
						_ => "json"
					};

					entrypointArgs.AddRange(new[]
					{
						"--set",
						$"input_dir={Quote(meshInputDir)}",
						$"output_dir={Quote(meshOutputDir)}",
						$"file_type={Quote(fileType)}"
					});
				}
			}
			else
			{
				// unite_meshes workflow (default): combines pore meshes into single GLB
				var hostConfigPath = Path.Combine(hostOutputDir, baseName, "unite_meshes.json");
				var containerConfigPath = $"/app/output/{baseName}/unite_meshes.json";
				var hasConfig = File.Exists(hostConfigPath);

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
			}

			var dockerArgs =
				"run --rm " +
				$"--platform {Quote(platform)} " +
				$"--name {Quote(containerName)} " +
				$"--network {Quote(dockerNetwork)} " +
				$"{(string.IsNullOrWhiteSpace(runAsUid) ? "" : $"--user {Quote(runAsGid is null or "" ? runAsUid : $"{runAsUid}:{runAsGid}")} ")}" +
				$"-v {Quote(hostInputDir)}:/app/input " +
				$"-v {Quote(hostOutputDir)}:/app/output " +
				$"{(string.IsNullOrWhiteSpace(workflowEntrypoint) ? "" : $"--entrypoint {Quote(workflowEntrypoint)} ")}" +
				$"{(string.IsNullOrWhiteSpace(workflowMode) ? "" : $"-e SEG_WORKFLOW_MODE={Quote(workflowMode)} ")}" +
				$"{Quote(imageName)} " +
				string.Join(" ", entrypointArgs);

			Console.WriteLine($"Using segmentation image: {imageName}, workflow: {workflowName}, network: {dockerNetwork}, platform: {platform}");
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
				_runningContainers[dbJob.Id] = containerName;

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
			finally
			{
				_runningContainers.TryRemove(dbJob.Id, out _);
			}
		}

		private async Task<(bool Succeeded, string Stdout, string Stderr, string? ErrorMessage)>
			RunParticleSegmentationAsync(
				Job dbJob,
				string hostInputDir,
				string hostOutputDir)
		{
			var containerName = $"particle-seg-job-{dbJob.Id}-{Guid.NewGuid()}";
			var platform = Environment.GetEnvironmentVariable("PLATFORM") ?? "linux/amd64";
			var dockerNetwork = Environment.GetEnvironmentVariable("DOCKER_NETWORK_NAME") ?? "lovamap_core_network";
			var runAsUid = Environment.GetEnvironmentVariable("JOB_RUN_AS_UID");
			var runAsGid = Environment.GetEnvironmentVariable("JOB_RUN_AS_GID");

			var imageName = Environment.GetEnvironmentVariable("PARTICLE_SEGMENTATION_IMAGE");
			if (string.IsNullOrWhiteSpace(imageName))
				return (false, string.Empty, string.Empty, "PARTICLE_SEGMENTATION_IMAGE is not set.");

			var fileName = dbJob.FileName;

			var entrypointArgs = new List<string>
			{
				"--filename", Quote(fileName),
				"--no-plot",
				"--no-mat"
			};

			// Parse stored segmentation params and convert to CLI flags
			if (!string.IsNullOrWhiteSpace(dbJob.SegmentationParams))
			{
				try
				{
					var paramMap = new Dictionary<string, string>
					{
						["th"] = "--th",
						["radiusUm"] = "--radius-um",
						["dxyz"] = "--dxyz",
						["s2vMax"] = "--s2v-max",
						["dx"] = "--dx",
						["dy"] = "--dy",
						["dz"] = "--dz",
						["fluorescentLabel"] = "--fluorescent-label",
						["cropBool"] = "--crop-bool",
						["channelNum"] = "--channel-num"
					};

					var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(dbJob.SegmentationParams);
					if (parsed != null)
					{
						foreach (var kvp in parsed)
						{
							if (paramMap.TryGetValue(kvp.Key, out var cliFlag) && !string.IsNullOrWhiteSpace(kvp.Value))
							{
								entrypointArgs.Add(cliFlag);
								entrypointArgs.Add(Quote(kvp.Value));
							}
						}
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[WARN] Failed to parse SegmentationParams for job {dbJob.Id}: {ex.Message}");
				}
			}

			var dockerArgs =
				"run --rm " +
				$"--platform {Quote(platform)} " +
				$"--name {Quote(containerName)} " +
				$"--network {Quote(dockerNetwork)} " +
				$"{(string.IsNullOrWhiteSpace(runAsUid) ? "" : $"--user {Quote(runAsGid is null or "" ? runAsUid : $"{runAsUid}:{runAsGid}")} ")}" +
				$"-v {Quote(hostInputDir)}:/app/input " +
				$"-v {Quote(hostOutputDir)}:/app/output " +
				$"{Quote(imageName)} " +
				string.Join(" ", entrypointArgs);

			Console.WriteLine($"Using particle segmentation image: {imageName}, network: {dockerNetwork}, platform: {platform}");
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
				_runningContainers[dbJob.Id] = containerName;

				var stdoutTask = process.StandardOutput.ReadToEndAsync();
				var stderrTask = process.StandardError.ReadToEndAsync();

				await process.WaitForExitAsync();

				var stdout = await stdoutTask;
				var stderr = await stderrTask;

				Console.WriteLine($"[DEBUG] Particle segmentation job {dbJob.Id} docker run exited with code {process.ExitCode}");
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
				Console.WriteLine($"[ERROR] Exception when running particle segmentation job {dbJob.Id}: {ex}");
				return (false, string.Empty, string.Empty, ex.Message);
			}
			finally
			{
				_runningContainers.TryRemove(dbJob.Id, out _);
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
			var meshJobPrefix = $"{baseJobId}-mesh-unite";

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

		private async Task EnqueueMeshGenerationAfterSegmentationAsync(DataContext db, Job dbJob)
		{
			if (dbJob.JobType != JobType.ParticleSegmentation || dbJob.Status != JobStatus.Completed)
				return;

			var baseJobId = dbJob.JobId ?? dbJob.Id.ToString();
			var meshJobPrefix = $"{baseJobId}-mesh-gen";

			var existingMesh = await db.Jobs.AnyAsync(j =>
				j.JobType == JobType.MeshProcessing &&
				j.MeshWorkflow == "mesh_generation" &&
				j.JobId != null &&
				j.JobId.StartsWith(meshJobPrefix) &&
				(j.Status == JobStatus.Pending || j.Status == JobStatus.Running || j.Status == JobStatus.Completed));

			if (existingMesh)
			{
				Console.WriteLine($"[QUEUE] Mesh generation already exists for segmentation job {dbJob.Id}; skipping auto-enqueue.");
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
				MeshWorkflow = "mesh_generation",
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
			Console.WriteLine($"[QUEUE] Enqueued mesh generation job {meshJob.Id} for segmentation job {dbJob.Id}.");
		}

		private async Task EnqueueParticleMeshGenerationIfNeededAsync(DataContext db, Job dbJob)
		{
			if (dbJob.JobType != JobType.Lovamap || !dbJob.GenerateParticleMesh || dbJob.Status != JobStatus.Completed)
				return;

			// Skip if this job was chained from a segmentation job — mesh already exists
			if (!string.IsNullOrWhiteSpace(dbJob.SourceJobId))
			{
				Console.WriteLine($"[QUEUE] Skipping particle mesh generation for job {dbJob.Id}: has sourceJobId '{dbJob.SourceJobId}'.");
				return;
			}

			var baseJobId = dbJob.JobId ?? dbJob.Id.ToString();
			var meshJobPrefix = $"{baseJobId}-mesh-gen";

			var existingMesh = await db.Jobs.AnyAsync(j =>
				j.JobType == JobType.MeshProcessing &&
				j.MeshWorkflow == "mesh_generation" &&
				j.JobId != null &&
				j.JobId.StartsWith(meshJobPrefix) &&
				(j.Status == JobStatus.Pending || j.Status == JobStatus.Running || j.Status == JobStatus.Completed));

			if (existingMesh)
			{
				Console.WriteLine($"[QUEUE] Particle mesh generation already exists for job {dbJob.Id}; skipping auto-enqueue.");
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
				MeshWorkflow = "mesh_generation",
				Status = JobStatus.Pending,
				SubmittedAt = DateTime.UtcNow,
				InitiatorType = dbJob.InitiatorType,
				UserId = dbJob.UserId,
				ClientId = dbJob.ClientId,
				DxValue = dbJob.DxValue,
				GenerateMesh = false,
				GenerateParticleMesh = false
			};

			db.Jobs.Add(meshJob);
			await db.SaveChangesAsync();

			var meshDxValue = meshJob.DxValue ?? "4.0";
			_jobQueue.Enqueue(meshJob, meshDxValue, uploadUrl: null, uploadToken: null);
			Console.WriteLine($"[QUEUE] Enqueued particle mesh generation job {meshJob.Id} ({meshJob.JobId}) for lovamap job {dbJob.Id}.");
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

		/// <summary>
		/// Fire-and-forget: spawns an Alpine container to copy job files from local disk to NFS.
		/// Logs warnings on failure but never throws or blocks the caller.
		/// </summary>
		private void FireAndForgetNfsSync(string baseName, string? fileName, int jobId, bool removeOutput)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					var hostInputDir = Environment.GetEnvironmentVariable("HOST_INPUT_DIR") ?? "";
					var hostOutputDir = Environment.GetEnvironmentVariable("HOST_OUTPUT_DIR") ?? "";
					var nfsInputDir = Environment.GetEnvironmentVariable("NFS_INPUT_DIR");
					var nfsOutputDir = Environment.GetEnvironmentVariable("NFS_OUTPUT_DIR");
					var syncScript = Environment.GetEnvironmentVariable("NFS_SYNC_SCRIPT");

					if (string.IsNullOrWhiteSpace(nfsInputDir) || string.IsNullOrWhiteSpace(nfsOutputDir))
					{
						Console.WriteLine($"[NFS-SYNC] Skipping sync for job {jobId}: NFS dirs not configured.");
						return;
					}

					if (string.IsNullOrWhiteSpace(syncScript))
					{
						Console.WriteLine($"[NFS-SYNC] Skipping sync for job {jobId}: NFS_SYNC_SCRIPT not configured.");
						return;
					}

					var fileArg = string.IsNullOrWhiteSpace(fileName) ? "''" : Quote(fileName);
					var removeArg = removeOutput ? "true" : "false";

					var dockerArgs =
						"run --rm --network none " +
						$"-v {Quote(hostInputDir)}:/local/input " +
						$"-v {Quote(hostOutputDir)}:/local/output " +
						$"-v {Quote(nfsInputDir)}:/nfs/input " +
						$"-v {Quote(nfsOutputDir)}:/nfs/output " +
						$"-v {Quote(syncScript)}:/sync.sh:ro " +
						$"alpine sh /sync.sh {Quote(baseName)} {fileArg} {removeArg}";

					Console.WriteLine($"[NFS-SYNC] Starting sync for job {jobId}: baseName={baseName}, fileName={fileName}, removeOutput={removeOutput}");

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

					process.Start();
					var stdout = await process.StandardOutput.ReadToEndAsync();
					var stderr = await process.StandardError.ReadToEndAsync();
					await process.WaitForExitAsync();

					if (process.ExitCode != 0)
					{
						Console.WriteLine($"[NFS-SYNC] WARN: Sync failed for job {jobId} (exit {process.ExitCode}). stderr={stderr}");
					}
					else
					{
						Console.WriteLine($"[NFS-SYNC] Sync completed for job {jobId}. stdout={stdout}");
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[NFS-SYNC] ERROR: Exception syncing job {jobId}: {ex.Message}");
				}
			});
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
