using WrapperApi.Models;

public class Client
{
	public int Id { get; set; }
	public string ClientId { get; set; } = null!;    // e.g. "lovamap-gw"
	public string DisplayName { get; set; } = null!;
	public string? AllowedScopes { get; set; }       // space-separated e.g. "core.submit core.read"
	public bool IsActive { get; set; } = true;
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public ICollection<ClientSecret> Secrets { get; set; } = [];
	public virtual ICollection<Job> JobsSubmitted { get; set; } = [];

}

public class ClientSecret
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public byte[] SecretHash { get; set; } = null!;
    public byte[] SecretSalt { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}