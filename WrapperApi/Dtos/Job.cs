using WrapperApi.Models;

public record JobRunResult(bool Success, bool ShouldRetry, string? ErrorMessage);
public class JobDto
{
    public int Id { get; set; }
    public string? JobId { get; set; }
    public string FileName { get; set; } = null!;
    public JobType JobType { get; set; }
    public JobStatus Status { get; set; }
    public DateTime SubmittedAt { get; set; }
	public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? UserId { get; set; }
    public int? ClientId { get; set; }
	public int Priority { get; set; } = 0; // Lower number = higher priority
    public string? ErrorMessage { get; set; }
    public string? HeartbeatMessage { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
	public bool JobUploadSucceeded { get; set; }
	public DateTime? HeartbeatPostedAt { get; set; }
    public string? DxValue { get; set; } // Store the dx value if it varies per job
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 3;
    public bool GenerateMesh { get; set; } = true;

    // add only the fields you want to expose
}
