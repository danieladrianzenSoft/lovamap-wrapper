using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WrapperApi.Data;
using WrapperApi.Models;
using WrapperApi.Services;
using WrapperApi.Helpers;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration;

var connectionString = Environment.GetEnvironmentVariable("LOVAMAP_CORE_DB") 
    ?? builder.Configuration.GetConnectionString("LOVAMAP_CORE_DB")
    ?? throw new Exception("LOVAMAP_CORE_DB connection string not found");

builder.Services.AddDbContext<DataContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddIdentity<User, Role>()
    .AddEntityFrameworkStores<DataContext>()
    .AddDefaultTokenProviders();

var jwtIssuer = builder.Configuration.GetSection("Jwt:Issuer").Get<string>();
var jwtKey = builder.Configuration.GetSection("Jwt:Key").Get<string>();

if (string.IsNullOrEmpty(jwtIssuer) || string.IsNullOrEmpty(jwtKey))
    throw new Exception("JWT configuration must include issuer and key");

builder.Services.AddAuthentication(cfg => {
    cfg.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    cfg.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtIssuer,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

// --- CORS configuration START ---
var env = config["ENVIRONMENT"] ?? string.Empty;

var envNormalized = env.Trim().ToLowerInvariant();
var isProduction = env == "production" || builder.Environment.IsProduction();
var isDevOrTest = env == "development" || envNormalized == "test" || builder.Environment.IsDevelopment();

const string lovamapOrigin = "https://lovamap.com";
string[] allowedOrigins;

if (isProduction)
{
    // Production: only allow the lovamap domain
    allowedOrigins = new[] { lovamapOrigin };
}
else if (isDevOrTest)
{
    // Development / Test: allow lovamap and local dev origin
    allowedOrigins = new[] { lovamapOrigin, "https://localhost:44381" };
}
else
{
    // Fallback: be conservative and allow both (you can change this as you prefer)
    allowedOrigins = new[] { lovamapOrigin, "https://localhost:44381" };
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("LovamapCors", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});
// --- CORS configuration END ---

var inputDir = config["LOVAMAP_INPUT_DIR"] ?? "/app/input";
var outputDir = config["LOVAMAP_OUTPUT_DIR"] ?? "/app/output";

if (env == "Development") {
	inputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../input"));
	outputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../output"));
}

Directory.CreateDirectory(inputDir);
Directory.CreateDirectory(outputDir);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<HeartbeatService>();
builder.Services.AddSingleton<JobService>(provider =>
{
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
    var env = provider.GetRequiredService<IWebHostEnvironment>();
    var cache = provider.GetRequiredService<HeartbeatCache>();
    var heartbeatService = provider.GetRequiredService<HeartbeatService>();
    var httpFactory = provider.GetRequiredService<IHttpClientFactory>();

    return new JobService(scopeFactory, env, cache, heartbeatService, httpFactory, inputDir, outputDir);
});
builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddScoped<IJwtGeneratorHelper, JwtGeneratorHelper>();
builder.Services.AddSingleton<HeartbeatCache>();
builder.Services.AddScoped<ISeedService, SeedService>();

builder.Services.AddHostedService<BackgroundJobService>();
builder.Services.AddHostedService<HeartbeatFlusherService>();

var app = builder.Build();

app.UseCors("LovamapCors");

// Run migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataContext>();
    db.Database.Migrate();
}

// Seed data
var scopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
using (var scope = scopeFactory.CreateScope())
{
    var seedingService = scope.ServiceProvider.GetRequiredService<ISeedService>();
    await seedingService.SeedAllAsync();
}

app.UseAuthentication();
app.UseAuthorization();

// Test endpoint
app.MapGet("/", () => "LOVAMAP wrapper is running");

// Health endpoint
app.MapGet("/health", () => { return Results.Ok("LOVAMAP Core is responsive"); });

// Create a client
app.MapPost("/clients", async (CreateClientDto dto, DataContext db) =>
{
    var client = new Client
    {
        ClientId = dto.ClientId,
        DisplayName = dto.DisplayName,
        AllowedScopes = dto.AllowedScopes,
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    var plainSecret = ClientSecretHelper.GeneratePlainSecret();
    var (hash, salt) = ClientSecretHelper.CreateSecretHash(plainSecret);

    var secret = new ClientSecret
    {
        ClientId = client.Id,
        SecretHash = hash,
        SecretSalt = salt,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = null
    };

    client.Secrets.Add(secret);

    db.Clients.Add(client);
    await db.SaveChangesAsync();

    return Results.Created($"/clients/{client.Id}", new { secretId = secret.Id, secret = plainSecret });
}).AllowUserRoles("Admin");

// Create a client secret
app.MapPost("/clients/{id}/secrets", async (int id, DataContext db) =>
{
    var client = await db.Clients.Include(c => c.Secrets).SingleOrDefaultAsync(c => c.Id == id);
    if (client == null) return Results.NotFound();

    var plainSecret = ClientSecretHelper.GeneratePlainSecret();
    var (hash, salt) = ClientSecretHelper.CreateSecretHash(plainSecret);

    var secret = new ClientSecret {
        ClientId = client.Id,
        SecretHash = hash,
        SecretSalt = salt,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = null // you can set if you want
    };

    client.Secrets.Add(secret);
    await db.SaveChangesAsync();

    // return secret plaintext only once
    return Results.Ok(new { secretId = secret.Id, secret = plainSecret });
}).AllowClientOrUserRoles("Admin");

// Client login to get JWT
app.MapPost("/clients/connect", async (ClientConnectRequest request, DataContext db, IJwtGeneratorHelper jwtGen) =>
{
    var clientId = request.ClientId;
    var clientSecret = request.ClientSecret;

    var client = await db.Clients.Include(c => c.Secrets)
                 .SingleOrDefaultAsync(c => c.ClientId == clientId && c.IsActive);
    if (client == null) return Results.Unauthorized();

    var activeSecrets = client.Secrets.Where(s => s.IsActive && (s.ExpiresAt == null || s.ExpiresAt > DateTime.UtcNow)).ToList();
    bool ok = activeSecrets.Any(s => ClientSecretHelper.VerifySecret(clientSecret, s.SecretHash, s.SecretSalt));
    if (!ok) return Results.Unauthorized();

    var token = jwtGen.GenerateJwtTokenForClient(client);
    return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
});

// User login to get JWT
app.MapPost("/users/connect",
    async (UserLoginDto dto, UserManager<User> userManager, IJwtGeneratorHelper jwtGen) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
        return Results.BadRequest("Email and password are required.");

    var user = await userManager.FindByEmailAsync(dto.Email);
    if (user == null)
        return Results.Unauthorized();

    // Validate password
    var passwordValid = await userManager.CheckPasswordAsync(user, dto.Password);
    if (!passwordValid) return Results.Unauthorized();

    var token = await jwtGen.GenerateJwtToken(user); // returns Task<string>
    return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
});

// User creation - only accessible to admin 
app.MapPost("/users",
    async (CreateUserDto dto, UserManager<User> userManager, RoleManager<Role> roleManager) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.UserName) || string.IsNullOrWhiteSpace(dto.Password) || string.IsNullOrWhiteSpace(dto.Role))
        return Results.BadRequest("Email, userName and password are required.");

    // Check uniqueness
    if (await userManager.FindByEmailAsync(dto.Email) != null)
        return Results.Conflict("A user with that email already exists.");

    if (await userManager.FindByNameAsync(dto.UserName) != null)
        return Results.Conflict("A user with that username already exists.");

    var user = new User
    {
        Email = dto.Email,
        UserName = dto.UserName,
        CreatedAt = DateTime.UtcNow,
        EmailConfirmed = true // change as needed
    };

    var createResult = await userManager.CreateAsync(user, dto.Password);
    if (!createResult.Succeeded)
    {
        // return validation errors
        var errors = createResult.Errors.Select(e => e.Description);
        return Results.BadRequest(new { errors });
    }

    // Ensure "User" role exists, create if missing
    if (!await roleManager.RoleExistsAsync(dto.Role))
        return Results.BadRequest($"Invalid role {dto.Role} indicated.");

    // Add user to role
    var addToRoleResult = await userManager.AddToRoleAsync(user, dto.Role);
    if (!addToRoleResult.Succeeded)
    {
        var errors = addToRoleResult.Errors.Select(e => e.Description);
        // Roll back created user (optional)
        await userManager.DeleteAsync(user);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    // Return Created (do not return password)
    var location = $"/users/{user.Id}";
    return Results.Created(location, new { id = user.Id, email = user.Email, userName = user.UserName });
}).AllowUserRoles("Admin");

// Get all jobs
app.MapGet("/jobs", async (HttpRequest request, DataContext db) =>
{
    // Parse query parameters (simple handling for date-only like "2024-03-01")
    DateTime? ParseAsUtcDateOrDateTime(string key, bool endOfDayIfDateOnly = false)
    {
        if (!request.Query.ContainsKey(key)) return null;
        var s = request.Query[key].ToString().Trim();
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return null;

        // If client sent a date-only string (no 'T' time separator), treat it as date-only.
        var isDateOnly = !s.Contains('T') && s.Length <= 10;

        if (isDateOnly)
        {
            var dt = parsed.Date;
            if (endOfDayIfDateOnly)
                dt = dt.AddDays(1).AddTicks(-1); // end of day (UTC)
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        // If the string included time / offset, normalize to UTC.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.UtcDateTime;

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    // Usage
    DateTime? start = ParseAsUtcDateOrDateTime("start", endOfDayIfDateOnly: false);
    DateTime? end   = ParseAsUtcDateOrDateTime("end",   endOfDayIfDateOnly: true);

    // Determine final start and end values (UTC)
    var now = DateTime.UtcNow;
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

    // Make non-null local values for EF comparisons
    var startUtc = DateTime.SpecifyKind(start!.Value, DateTimeKind.Utc);
    var endUtc   = DateTime.SpecifyKind(end!.Value,   DateTimeKind.Utc);

    var jobs = await db.Jobs
        .Where(j => j.SubmittedAt >= startUtc && j.SubmittedAt <= endUtc)
        .OrderByDescending(j => j.SubmittedAt)
        .Select(j => new JobDto
        {
            Id = j.Id,
            JobId = j.JobId,
            FileName = j.FileName,
            JobType = j.JobType,
            Status = j.Status,
            SubmittedAt = j.SubmittedAt,
            StartedAt = j.StartedAt,
            CompletedAt = j.CompletedAt,
            UserId = j.UserId,
            ClientId = j.ClientId,
            Priority = j.Priority,
            StdOut = j.StdOut,
            StdErr = j.StdErr,
            JobUploadSucceeded = j.JobUploadSucceeded,
            HeartbeatPostedAt = j.HeartbeatPostedAt,
            HeartbeatMessage = j.HeartbeatMessage,
            DxValue = j.DxValue,
            RetryCount = j.RetryCount,
            MaxRetries = j.MaxRetries
        })
        .ToListAsync();

    return Results.Ok(jobs);
}).AllowUserRoles("Admin", "Developer");

// Get job by ID
app.MapGet("/jobs/{id}", async (int id, DataContext db) =>
    await db.Jobs.FindAsync(id) is Job job ? Results.Ok(job) : Results.NotFound())
    .AllowClientOrUser();

// Get job by JobID
app.MapGet("/jobs/by-jobid/{jobId}", async (string jobId, DataContext db) =>
    await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId) is Job job ? Results.Ok(job) : Results.NotFound())
    .AllowClientOrUser();

// Upload file and start job
app.MapPost("/jobs", async (
	HttpRequest request, DataContext db, ClaimsPrincipal user,
	JobService jobService, IBackgroundJobQueue jobQueue) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("Content-Type must be multipart/form-data");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");

    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");

	var jobId = JobRequestParser.ParseJobId(form);
    var dxValue = JobRequestParser.ParseDxValue(form);

    if (string.IsNullOrEmpty(jobId)) return Results.BadRequest("jobId is required");
    if (await db.Jobs.AnyAsync(j => j.JobId == jobId))
        return Results.Conflict("A job with this JobId already exists.");

	string fileName;
    try
    {
        fileName = await FileService.SaveUploadedFileAsync(file, inputDir);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    var tokenType = user?.FindFirst("tokenType")?.Value; // "user" or "client"
    var idClaim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var initiatorType = string.Equals(tokenType, "client", StringComparison.OrdinalIgnoreCase)
            ? InitiatorType.Client
            : string.Equals(tokenType, "user", StringComparison.OrdinalIgnoreCase)
                ? InitiatorType.User
                : InitiatorType.Unknown;

    Console.WriteLine($"[DEBUG CLAIM] tokenType: {tokenType}, subClaim: {idClaim}, initiatorType: {initiatorType}");
                
    // Save job to database
    var job = new Job
    {
		JobId = jobId,
        FileName = fileName,
        JobType = JobType.Lovamap,
        Status = JobStatus.Pending,
        SubmittedAt = DateTime.UtcNow,
        InitiatorType = initiatorType,
        DxValue = dxValue,
    };

    if (job.InitiatorType == InitiatorType.Client)
    {
        if (!int.TryParse(idClaim, out var clientIdParsed))
            return Results.Forbid();

        var client = await db.Clients.FindAsync(clientIdParsed);
        if (client == null)
        {
            Console.WriteLine($"[WARN] Auth token claimed client id='{idClaim}' but no client exists.");
            return Results.Forbid();
        }

        job.ClientId = clientIdParsed;
        job.UserId = null;
    }
    else if (job.InitiatorType == InitiatorType.User)
    {
        if (string.IsNullOrEmpty(idClaim))
            return Results.Forbid();

        var dbUser = await db.Users.FindAsync(idClaim);
        if (dbUser == null)
        {
            Console.WriteLine($"[WARN] Auth token claimed user id='{idClaim}' but no user exists.");
            return Results.Forbid();
        }
        job.UserId = dbUser.Id;
        job.ClientId = null;
    }
    else
    {
        return Results.Forbid();
    }

    db.Jobs.Add(job);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException dbEx)
    {
        Console.WriteLine($"[ERROR] DbUpdateException saving job: {dbEx}");
        return Results.Problem("Failed to save job. Please contact support.", statusCode: 400);
    }
    
    var uploadUrl = form["uploadUrl"].ToString();
    var uploadToken = form["uploadToken"].ToString();

    // await jobService.RunJobAsync(job, dxValue);
    jobQueue.Enqueue(job, dxValue, uploadUrl, uploadToken);
    
    var jobDto = new JobDto {
        Id = job.Id,
        JobId = job.JobId,
        FileName = job.FileName,
        JobType = job.JobType,
        Status = job.Status,
        SubmittedAt = job.SubmittedAt,
        StartedAt = job.StartedAt,
        UserId = job.UserId,
        ClientId = job.ClientId
    };

    return Results.Created($"/jobs/by-jobid/{job.JobId ?? job.Id.ToString()}", jobDto);
}).AllowClientOrUser();

// Trigger mesh-processing (unite_meshes) for an existing job by JobId
app.MapPost("/jobs/{jobId}/mesh-processing", async (
    string jobId, DataContext db, ClaimsPrincipal user,
    IBackgroundJobQueue jobQueue) =>
{
    var sourceJob = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId);
    if (sourceJob == null)
        return Results.NotFound($"No job found with JobId '{jobId}'.");

    if (sourceJob.JobType != JobType.Lovamap)
        return Results.BadRequest("Mesh processing can only be triggered from a Lovamap job.");

    if (sourceJob.Status != JobStatus.Completed)
        return Results.BadRequest("Source job must be Completed before mesh processing can run.");

    var baseJobId = sourceJob.JobId ?? sourceJob.Id.ToString();
    var meshJobId = $"{baseJobId}-mesh";
    if (await db.Jobs.AnyAsync(j => j.JobId == meshJobId))
        meshJobId = $"{baseJobId}-mesh-{Guid.NewGuid():N}";

    var meshJob = new Job
    {
        JobId = meshJobId,
        FileName = sourceJob.FileName,
        JobType = JobType.MeshProcessing,
        Status = JobStatus.Pending,
        SubmittedAt = DateTime.UtcNow,
        InitiatorType = sourceJob.InitiatorType,
        UserId = sourceJob.UserId,
        ClientId = sourceJob.ClientId,
        DxValue = sourceJob.DxValue
    };

    db.Jobs.Add(meshJob);
    await db.SaveChangesAsync();

    var dxValue = meshJob.DxValue ?? "4.0";
    jobQueue.Enqueue(meshJob, dxValue, uploadUrl: null, uploadToken: null);

    var meshJobDto = new JobDto {
        Id = meshJob.Id,
        JobId = meshJob.JobId,
        FileName = meshJob.FileName,
        JobType = meshJob.JobType,
        Status = meshJob.Status,
        SubmittedAt = meshJob.SubmittedAt,
        StartedAt = meshJob.StartedAt,
        UserId = meshJob.UserId,
        ClientId = meshJob.ClientId
    };

    return Results.Accepted($"/jobs/by-jobid/{meshJob.JobId}", meshJobDto);
}).AllowClientOrUser();

app.MapDelete("/jobs", async (HttpRequest request, DataContext db) =>
{
    var query = request.Query;

    if (!query.ContainsKey("start") || !DateTime.TryParse(query["start"], out var start))
    {
        return Results.BadRequest("Missing or invalid 'start' query parameter (must be ISO date string).");
    }

    // Force flag deletes jobs regardless of status, not just non-running jobs
    var force = query.ContainsKey("force") &&
                bool.TryParse(query["force"], out var forceParsed) && forceParsed;

    var jobQuery = db.Jobs.AsQueryable()
        .Where(j => j.SubmittedAt <= start);

    if (!force)
    {
        jobQuery = jobQuery.Where(j => j.Status != JobStatus.Running);
    }

    var jobsToDelete = await jobQuery.ToListAsync();

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
}).AllowUserRoles("Admin");

app.MapGet("/jobs/{jobId}/raw-results", async (string jobId, DataContext db, ILogger<Program> logger) =>
{
	if (string.IsNullOrWhiteSpace(jobId))
    {
        return Results.BadRequest("jobId is required.");
    }

    // Look up job by JobId (string GUID), not int Id
    var job = await db.Jobs
        .AsNoTracking()
        .FirstOrDefaultAsync(j => j.JobId == jobId);

    if (job == null)
    {
        logger.LogWarning("Raw result requested for non-existent job {JobId}", jobId);
        return Results.NotFound($"Job with JobId '{jobId}' not found.");
    }

    if (string.IsNullOrWhiteSpace(job.ResultPath))
    {
        logger.LogWarning("Job {JobId} has no ResultPath stored.", jobId);
        return Results.NotFound($"Job '{jobId}' does not have a result path stored.");
    }

    var path = job.ResultPath;

    if (!File.Exists(path))
    {
        logger.LogWarning("Result file for job {JobId} not found at {Path}", jobId, path);
        return Results.NotFound($"Result file not found at '{path}'.");
    }

    try
    {
        // Stream the file (raw bytes); GW will interpret as protobuf
        var stream = File.OpenRead(path);

        // Use a generic content type; you can change this to application/x-protobuf if you like
        const string contentType = "application/octet-stream";

        // Optional: send a nice filename, but not required since GW only cares about bytes
        var fileName = Path.GetFileName(path);

        return Results.File(stream, contentType, fileName, enableRangeProcessing: false);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error reading result file for job {JobId} at {Path}", jobId, path);
        return Results.StatusCode(500);
    }
}).AllowAnonymous();

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
        Console.WriteLine($"[HEARTBEAT] Error - form could not be parsed: {ex}");
        return Results.BadRequest("Invalid or missing form data.");
    }

    var jobId = form["jobId"].ToString();
    if (string.IsNullOrEmpty(jobId)) jobId = form["jobid"].ToString();

    var description = form["desc"].ToString();

    if (string.IsNullOrEmpty(jobId))
    {
        Console.WriteLine($"[HEARTBEAT-DEBUG] Missing jobId ${jobId}");
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
