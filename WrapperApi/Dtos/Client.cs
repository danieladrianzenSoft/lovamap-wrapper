public record CreateClientDto(string ClientId, string DisplayName, string? AllowedScopes);
public record ClientConnectRequest(string ClientId, string ClientSecret);

