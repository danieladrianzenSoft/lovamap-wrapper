using Microsoft.EntityFrameworkCore;
using WrapperApi.Data;
using WrapperApi.Models;
using WrapperApi.Services;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration;

var dbPath = config["DB_PATH"] ?? "db/lovamap-core.db";
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// Register SQLite and DataContext
builder.Services.AddDbContext<DataContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var env = config["ENVIRONMENT"];

var inputDir = config["LOVAMAP_INPUT_DIR"] ?? "/app/input";
var outputDir = config["LOVAMAP_OUTPUT_DIR"] ?? "/app/output";

if (env == "Development") {
	inputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../input"));
	outputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../output"));
}

Directory.CreateDirectory(inputDir);
Directory.CreateDirectory(outputDir);

builder.Services.AddSingleton<JobService>(provider =>
{
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
    var env = provider.GetRequiredService<IWebHostEnvironment>();
    var cache = provider.GetRequiredService<HeartbeatCache>();
    return new JobService(scopeFactory, env, cache, inputDir, outputDir);
});

builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddSingleton<HeartbeatCache>();

builder.Services.AddHostedService<BackgroundJobService>();
builder.Services.AddHostedService<HeartbeatFlusherService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataContext>();
    db.Database.Migrate();
}

// Test endpoint
app.MapGet("/", () => "LOVAMAP wrapper is running");

// Get all jobs
app.MapGet("/jobs", async (HttpRequest request, DataContext db) =>
{
    var query = request.Query;
    var now = DateTime.UtcNow;

    // Parse query parameters
    DateTime? start = query.ContainsKey("start") && DateTime.TryParse(query["start"], out var parsedStart) ? parsedStart : null;
    DateTime? end = query.ContainsKey("end") && DateTime.TryParse(query["end"], out var parsedEnd) ? parsedEnd : null;

    // Determine final start and end values
    if (start == null && end == null)
    {
        start = now.AddDays(-7);
        end = now;
    }
    else if (start != null && end == null)
    {
        end = now;
    }
    else if (start == null && end != null)
    {
        start = end.Value.AddDays(-7);
    }

    var jobs = await db.Jobs
        .Where(j => j.SubmittedAt >= start && j.SubmittedAt <= end)
        .OrderByDescending(j => j.SubmittedAt)
        .ToListAsync();

    return Results.Ok(jobs);
});

// Get job by ID
app.MapGet("/jobs/{id}", async (int id, DataContext db) =>
    await db.Jobs.FindAsync(id) is Job job ? Results.Ok(job) : Results.NotFound());

// Get job by JobID
app.MapGet("/jobs/by-jobid/{jobId}", async (string jobId, DataContext db) =>
    await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId) is Job job ? Results.Ok(job) : Results.NotFound());

// Upload file and start job
app.MapPost("/jobs", async (
	HttpRequest request, DataContext db, 
	JobService jobService, IBackgroundJobQueue jobQueue) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");

    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

	var jobId = JobRequestParser.ParseJobId(form);
    var dxValue = JobRequestParser.ParseDxValue(form);

	if (!string.IsNullOrEmpty(jobId))
    {
        if (await db.Jobs.AnyAsync(j => j.JobId == jobId))
            return Results.Conflict("A job with this JobId already exists.");
    }

	string fileName;
    try
    {
        fileName = await FileService.SaveUploadedFileAsync(file, inputDir);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    // Save job to database
    var job = new Job
    {
		JobId = jobId,
        FileName = fileName,
        Status = JobStatus.Pending,
        SubmittedAt = DateTime.UtcNow,
        DxValue = dxValue,
    };

    db.Jobs.Add(job);
    await db.SaveChangesAsync();

	// await jobService.RunJobAsync(job, dxValue);
	jobQueue.Enqueue(job, dxValue);

    return Results.Created($"/jobs/by-jobid/{job.JobId ?? job.Id.ToString()}", job);
});

app.MapDelete("/jobs", async (HttpRequest request, DataContext db) =>
{
    var query = request.Query;

    if (!query.ContainsKey("start") || !DateTime.TryParse(query["start"], out var start))
    {
        return Results.BadRequest("Missing or invalid 'start' query parameter (must be ISO date string).");
    }

    var jobsToDelete = await db.Jobs
        .Where(j => j.SubmittedAt < start && j.Status != JobStatus.Running)
        .ToListAsync();

    int fileDeleteCount = 0;
    int outputDirDeleteCount = 0;

    foreach (var job in jobsToDelete)
    {
        // Delete input file
        if (FileService.DeleteInputFile(inputDir, job.FileName))
            fileDeleteCount++;

        outputDirDeleteCount += FileService.DeleteMatchingOutputDirs(outputDir, job.FileName);
    }

    db.Jobs.RemoveRange(jobsToDelete);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        DeletedCount = jobsToDelete.Count,
        DeletedInputFiles = fileDeleteCount,
        DeletedOutputDirs = outputDirDeleteCount,
        Cutoff = start
    });
});

app.MapPost("/heartbeat", async (HttpRequest request, HeartbeatCache cache) =>
{
    var heartbeatToken = request.Headers["X-Heartbeat-Token"].ToString();
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Content-Type must be application/x-www-form-urlencoded");
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest("Invalid or missing form data.");
    }

    var jobId = form["jobId"].ToString();
    var description = form["desc"].ToString();

    if (string.IsNullOrEmpty(jobId))
    {
        Console.WriteLine($"Missing jobId");
        return Results.BadRequest("Missing jobId");
    }

    if (string.IsNullOrEmpty(description))
    {
        Console.WriteLine($"Missing description");
        return Results.BadRequest("Missing 'desc'");
    }
    
    cache.Update(jobId, description);
    Console.WriteLine($"[HEARTBEAT] Job {jobId}: {description} at {DateTime.UtcNow}");

    return Results.Ok();
});

app.Run();
