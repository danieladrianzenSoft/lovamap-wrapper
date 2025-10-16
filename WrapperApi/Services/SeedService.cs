using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WrapperApi.Data;

public interface ISeedService
{
	Task SeedAllAsync();
}

public class SeedService : ISeedService
{
	private readonly UserManager<User> _userManager;
	private readonly RoleManager<Role> _roleManager;
	private readonly IConfiguration _configuration;
	private readonly DataContext _context;
	private readonly ILogger<SeedService> _logger;

	public SeedService(DataContext context,
		IConfiguration configuration,
		UserManager<User> userManager,
		RoleManager<Role> roleManager,
		ILogger<SeedService> logger)
	{
		_configuration = configuration;
		_context = context;
		_userManager = userManager;
		_roleManager = roleManager;
		_logger = logger;
	}

	public async Task SeedAllAsync()
	{
		await SeedRolesAsync();
		await SeedAdminUserAsync();
	}

	private async Task SeedRolesAsync()
	{
		if (await _context.Roles.AnyAsync()) return;

		var roles = new List<Role>
        {
            new Role { Name = "Admin" },
            new Role { Name = "Developer" }
        };

		foreach (var role in roles)
		{
			if (string.IsNullOrEmpty(role.Name)) continue;
			if (await _roleManager.RoleExistsAsync(role.Name)) continue;

			var result = await _roleManager.CreateAsync(role);

			if (!result.Succeeded)
			{
				_logger.LogError(null, "Failed to seed roles");
			}
		}
	}

	private async Task SeedAdminUserAsync()
	{
		if (await _context.Users.AnyAsync()) return;

		var adminEmail = _configuration["Admin:Email"];
		var adminPassword = _configuration["Admin:Password"];

		if (string.IsNullOrEmpty(adminEmail))
			throw new ApplicationException("AdminEmail environment variable is not set.");
		if (string.IsNullOrEmpty(adminPassword))
			throw new ApplicationException("AdminPassword environment variable is not set.");

		var existingUser = await _userManager.FindByEmailAsync(adminEmail);
		if (existingUser != null) return;

		var adminUser = new User
		{
			UserName = adminEmail,
			Email = adminEmail,
			EmailConfirmed = true,
			CreatedAt = DateTime.UtcNow
		};

		await _userManager.CreateAsync(adminUser, adminPassword);
		await _userManager.AddToRoleAsync(adminUser, "Admin");
	}
}