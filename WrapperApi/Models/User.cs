using Microsoft.AspNetCore.Identity;
using WrapperApi.Models;

public class User : IdentityUser
{
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public virtual ICollection<Job> JobsSubmitted { get; set; } = [];
}