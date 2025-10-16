using Microsoft.AspNetCore.Identity;

public class Role : IdentityRole
{
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}