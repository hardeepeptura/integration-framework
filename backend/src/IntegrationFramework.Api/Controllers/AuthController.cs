using IntegrationFramework.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// SSO (corporate Microsoft Entra ID) session endpoints. Always anonymous:
/// the login endpoint CHALLENGES via OIDC; everything else is public by design.
/// </summary>
[ApiController]
[AllowAnonymous]
public class AuthController(IConfiguration configuration, CurrentUserService users) : ControllerBase
{
    private readonly bool _ssoEnabled =
        !string.IsNullOrWhiteSpace(configuration["SSO_TENANT_ID"]) &&
        !string.IsNullOrWhiteSpace(configuration["SSO_CLIENT_ID"]);

    [HttpGet("auth/login")]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        var target = SafeReturnUrl(returnUrl);
        if (!_ssoEnabled) return Redirect(target);
        return Challenge(new AuthenticationProperties { RedirectUri = target },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet("auth/me")]
    public async Task<IActionResult> Me()
    {
        if (!_ssoEnabled)
            return Ok(new { authenticated = false, ssoEnabled = false });

        if (!(User.Identity?.IsAuthenticated ?? false))
            return Ok(new { authenticated = false, ssoEnabled = true });

        var appUser = await users.ResolveAsync(User);
        return Ok(new
        {
            authenticated = true,
            ssoEnabled = true,
            name = User.Identity?.Name,
            email = User.FindFirst("preferred_username")?.Value
                    ?? User.FindFirst("email")?.Value,
            // Platform role (admin | contributor) drives the Users page link and API authorizations.
            role = appUser?.User.Role,
            isAdmin = appUser?.IsAdmin ?? false
        });
    }

    [HttpPost("auth/logout")]
    public IActionResult Logout()
    {
        if (!_ssoEnabled) return Redirect("/");
        // Clear the app cookie AND the corporate Entra ID session.
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            OpenIdConnectDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";
}
