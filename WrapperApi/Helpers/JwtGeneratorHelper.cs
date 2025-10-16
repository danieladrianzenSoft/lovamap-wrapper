using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace WrapperApi.Helpers
{
	public interface IJwtGeneratorHelper
	{
		Task<string> GenerateJwtToken(User user);
		string GenerateJwtTokenForClient(Client client);
	}
	public class JwtGeneratorHelper : IJwtGeneratorHelper
	{
		private readonly IConfiguration _config;
		private readonly UserManager<User> _userManager;
		private readonly SymmetricSecurityKey _key;


		public JwtGeneratorHelper(IConfiguration config, UserManager<User> userManager)
		{
			_config = config;
			_userManager = userManager;
			var jwtKey = _config["Jwt:Key"] ?? throw new ArgumentNullException("Jwt:Key", "JWT Key is not configured in app settings");
			_key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
		}

		public async Task<string> GenerateJwtToken(User user)
		{
			var claims = new List<Claim>
				{
					new Claim(ClaimTypes.NameIdentifier, user.Id),
					new Claim(ClaimTypes.Name, user.Email ?? user.UserName ?? "N/A"),
					new Claim(JwtRegisteredClaimNames.Sub, user.Id),
					new Claim("tokenType", "user")
				};

			var roles = await _userManager.GetRolesAsync(user);

			roles.ToList().ForEach(role => claims.Add(new Claim(ClaimTypes.Role, role)));

			var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha512Signature);
			//HmacSha512Signature is the largest level of encryption.

			var tokenDescriptor = new SecurityTokenDescriptor
			{
				Subject = new ClaimsIdentity(claims),
				Expires = DateTime.UtcNow.AddMinutes(60),
				SigningCredentials = creds,
				Issuer = _config["Jwt:Issuer"],
				Audience = _config["Jwt:Issuer"]
			};

			var tokenHandler = new JwtSecurityTokenHandler();

			var token = tokenHandler.CreateToken(tokenDescriptor);

			return tokenHandler.WriteToken(token);
		}

		public string GenerateJwtTokenForClient(Client client)
		{
			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, client.Id.ToString()),
				new Claim(ClaimTypes.Name, client.DisplayName),
				new Claim(JwtRegisteredClaimNames.Sub, client.Id.ToString()),
				new Claim("tokenType", "client")
			};
			var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha512);
			var tokenDescriptor = new SecurityTokenDescriptor
			{
				Subject = new ClaimsIdentity(claims),
				Expires = DateTime.UtcNow.AddMinutes(60),
				SigningCredentials = creds,
				Issuer = _config["Jwt:Issuer"],
				Audience = _config["Jwt:Issuer"]
			};
			var tokenHandler = new JwtSecurityTokenHandler();
			var token = tokenHandler.CreateToken(tokenDescriptor);
			return tokenHandler.WriteToken(token);
		}
	}
}

