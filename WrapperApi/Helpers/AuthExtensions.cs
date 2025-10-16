using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

public enum Actor { Client, User }

// Extension on RouteHandlerBuilder
public static class AuthExtensions
{
    public static RouteHandlerBuilder RequireAuth(
        this RouteHandlerBuilder builder,
        Actor[] allowedActors,
        IEnumerable<string>? requiredUserRoles = null)
    {
        var rolesList = requiredUserRoles?.ToList() ?? new List<string>();

        var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
            {
                bool isClient = context.User.HasClaim(c => c.Type == "tokenType" && c.Value == "client");
                bool isUser = context.User.HasClaim(c => c.Type == "tokenType" && c.Value == "user");

                // client allowed
                if (allowedActors.Contains(Actor.Client) && isClient)
                    return true;

                // user allowed without role restriction
                if (allowedActors.Contains(Actor.User) && isUser && rolesList.Count == 0)
                    return true;

                // user allowed if they have ANY of the required roles
                if (allowedActors.Contains(Actor.User) && isUser && rolesList.Count > 0)
                {
                    return rolesList.Any(role => context.User.IsInRole(role));
                }

                return false;
            })
            .Build();

        return builder.RequireAuthorization(policy);
    }

    // convenience overloads
    public static RouteHandlerBuilder AllowClient(this RouteHandlerBuilder b)
        => b.RequireAuth(new[] { Actor.Client });

    public static RouteHandlerBuilder AllowUser(this RouteHandlerBuilder b)
        => b.RequireAuth(new[] { Actor.User });

    public static RouteHandlerBuilder AllowUserRoles(this RouteHandlerBuilder b, params string[] roles)
        => b.RequireAuth(new[] { Actor.User }, roles);

    public static RouteHandlerBuilder AllowClientOrUser(this RouteHandlerBuilder b)
        => b.RequireAuth(new[] { Actor.Client, Actor.User });

    public static RouteHandlerBuilder AllowClientOrUserRoles(this RouteHandlerBuilder b, params string[] roles)
        => b.RequireAuth(new[] { Actor.Client, Actor.User }, roles);
}