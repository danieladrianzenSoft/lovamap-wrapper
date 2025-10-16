namespace WrapperApi.Models;

public class Job
{
    public int Id { get; set; }
    public string? JobId { get; set; } = null;
    public string? UserId { get; set; } = null;
    public User? User { get; set; }
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public InitiatorType InitiatorType { get; set; } = InitiatorType.Unknown;
    public string FileName { get; set; } = null!;
    public JobStatus Status { get; set; } = 0;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultPath { get; set; }
    public bool JobUploadSucceeded { get; set; }
    public int Priority { get; set; } = 0; // Lower number = higher priority
    public string? ErrorMessage { get; set; }
    public string? HeartbeatMessage { get; set; }
    public DateTime? HeartbeatPostedAt { get; set; }
    public string? DxValue { get; set; } // Store the dx value if it varies per job
    public int RetryCount { get; set; } = 0;
    public int MaxRetries { get; set; } = 3;

}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Stopped
}

public enum InitiatorType
{
    Unknown = 0,
    Client = 1,
    User = 2
}