public record CreateUserDto(string Email, string UserName, string Password, string Role);
public record UserLoginDto(string Email, string Password);