using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using System.Net.WebSockets;
using Backend.Models;
using Backend.Services;
using ProtoBuf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<WebSocketSessionManager>();
builder.Services.AddHostedService<BinanceService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5500")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var certPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    "..",
    "nginx",
    "certs",
    "localhost.pem"
));

var keyPath = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    "..",
    "nginx",
    "certs",
    "localhost-key.pem"
));

if (!File.Exists(certPath) || !File.Exists(keyPath))
{
    Console.WriteLine("Backend HTTPS certificate was not found.");
    Console.WriteLine($"Certificate path: {certPath}");
    Console.WriteLine($"Key path: {keyPath}");
    return;
}

X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(9000, listenOptions =>
    {
        listenOptions.UseHttps(certificate);
    });
});

var app = builder.Build();

app.UseCors("Frontend");

app.UseWebSockets();

app.MapGet("/", () => "Lab3 backend is running");

app.MapGet("/ws", async (
    HttpContext context,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    WebSocketSessionManager sessionManager) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        return Results.BadRequest(new { error = "WebSocket request expected" });
    }

    var token = context.Request.Cookies["auth_token"];

    if (string.IsNullOrEmpty(token))
    {
        return Results.Unauthorized();
    }

    var validationResult = await ValidateJwtAsync(token, configuration, httpClientFactory);

    if (!validationResult.IsValid)
    {
        return Results.Unauthorized();
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var sessionId = sessionManager.Add(socket);

    try
    {
        await ReceiveWebSocketLoopAsync(socket, sessionId, sessionManager);
    }
    finally
    {
        sessionManager.Remove(sessionId);
    }

    return Results.Empty;
});

app.MapGet("/login", async (
    HttpContext context,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) =>
{
    var code = context.Request.Query["code"].ToString();
    var returnedState = context.Request.Query["state"].ToString();

    if (string.IsNullOrEmpty(code))
    {
        return await RedirectToCasdoor(context, configuration, httpClientFactory);
    }

    return await GetTokenFromCasdoor(context, configuration, httpClientFactory, code, returnedState);
});

app.MapGet("/user-info", async (
    HttpContext context,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) =>
{
    var token = context.Request.Cookies["auth_token"];

    if (string.IsNullOrEmpty(token))
    {
        return Results.Unauthorized();
    }

    var validationResult = await ValidateJwtAsync(token, configuration, httpClientFactory);

    if (!validationResult.IsValid)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        message = "JWT token is valid",
        claims = validationResult.Claims
    });
});

app.Run();

static async Task<IResult> RedirectToCasdoor(
    HttpContext context,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    var discovery = await GetDiscoveryAsync(configuration, httpClientFactory);

    var state = Guid.NewGuid().ToString("N");

    context.Response.Cookies.Append("oidc_state", state, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        MaxAge = TimeSpan.FromMinutes(10)
    });

    var parameters = new Dictionary<string, string?>
    {
        ["client_id"] = configuration["Casdoor:ClientId"],
        ["response_type"] = "code",
        ["redirect_uri"] = configuration["Casdoor:RedirectUri"],
        ["scope"] = configuration["Casdoor:Scope"],
        ["state"] = state
    };

    var authorizationUrl = QueryHelpers.AddQueryString(
        discovery.AuthorizationEndpoint,
        parameters
    );

    Console.WriteLine("Redirecting to Casdoor:");
    Console.WriteLine(authorizationUrl);

    return Results.Redirect(authorizationUrl);
}

static async Task<IResult> GetTokenFromCasdoor(
    HttpContext context,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    string code,
    string returnedState)
{
    var savedState = context.Request.Cookies["oidc_state"];

    if (string.IsNullOrEmpty(savedState) || savedState != returnedState)
    {
        Console.WriteLine("Invalid state.");
        return Results.Unauthorized();
    }

    var discovery = await GetDiscoveryAsync(configuration, httpClientFactory);

    var form = new Dictionary<string, string?>
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = configuration["Casdoor:ClientId"],
        ["client_secret"] = configuration["Casdoor:ClientSecret"],
        ["code"] = code,
        ["redirect_uri"] = configuration["Casdoor:RedirectUri"]
    };

    var client = httpClientFactory.CreateClient();

    var response = await client.PostAsync(
        discovery.TokenEndpoint,
        new FormUrlEncodedContent(form!)
    );

    var responseText = await response.Content.ReadAsStringAsync();

    Console.WriteLine("Token endpoint response:");
    Console.WriteLine(responseText);

    if (!response.IsSuccessStatusCode)
    {
        return Results.Unauthorized();
    }

    using var document = JsonDocument.Parse(responseText);
    var root = document.RootElement;

    var idToken = root.TryGetProperty("id_token", out var idTokenElement)
        ? idTokenElement.GetString()
        : null;

    var accessToken = root.TryGetProperty("access_token", out var accessTokenElement)
        ? accessTokenElement.GetString()
        : null;

    var token = idToken ?? accessToken;

    if (string.IsNullOrEmpty(token))
    {
        return Results.Unauthorized();
    }

    context.Response.Cookies.Append("auth_token", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        MaxAge = TimeSpan.FromHours(1)
    });

    context.Response.Cookies.Delete("oidc_state");

    var frontendUrl = configuration["Frontend:Url"] ?? "http://localhost:5500/index.html";

    return Results.Redirect(frontendUrl);
}

static async Task<OidcDiscovery> GetDiscoveryAsync(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    var authority = configuration["Casdoor:Authority"]!.TrimEnd('/');
    var discoveryUrl = $"{authority}/.well-known/openid-configuration";

    var client = httpClientFactory.CreateClient();
    var json = await client.GetStringAsync(discoveryUrl);

    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;

    return new OidcDiscovery
    {
        Issuer = root.GetProperty("issuer").GetString()!,

        AuthorizationEndpoint = root.GetProperty("authorization_endpoint").GetString()!
            .Replace("http://localhost:8443", "https://localhost:8443"),

        TokenEndpoint = root.GetProperty("token_endpoint").GetString()!
            .Replace("http://localhost:8443", "https://localhost:8443"),

        JwksUri = root.GetProperty("jwks_uri").GetString()!
            .Replace("http://localhost:8443", "https://localhost:8443")
    };
}

static async Task<JwtValidationResult> ValidateJwtAsync(
    string jwt,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    try
    {
        var discovery = await GetDiscoveryAsync(configuration, httpClientFactory);
        var clientId = configuration["Casdoor:ClientId"]!;

        var client = httpClientFactory.CreateClient();
        var jwksJson = await client.GetStringAsync(discovery.JwksUri);

        var keySet = new JsonWebKeySet(jwksJson);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = discovery.Issuer,

            ValidateAudience = true,
            ValidAudience = clientId,

            ValidateLifetime = true,

            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keySet.Keys
        };

        var handler = new JwtSecurityTokenHandler();

        ClaimsPrincipal principal = handler.ValidateToken(
            jwt,
            validationParameters,
            out SecurityToken validatedToken
        );

        var claims = principal.Claims.ToDictionary(
            claim => claim.Type,
            claim => claim.Value
        );

        return JwtValidationResult.Success(claims);
    }
    catch (Exception ex)
    {
        Console.WriteLine("JWT validation error:");
        Console.WriteLine(ex.Message);

        return JwtValidationResult.Fail(ex.Message);
    }
}

static async Task ReceiveWebSocketLoopAsync(
    WebSocket socket,
    Guid sessionId,
    WebSocketSessionManager sessionManager)
{
    while (socket.State == WebSocketState.Open)
    {
        var messageBytes = await ReceiveBinaryMessageAsync(socket);

        if (messageBytes is null)
        {
            break;
        }

        try
        {
            using var memoryStream = new MemoryStream(messageBytes);

            var subscription = Serializer.Deserialize<SubscriptionRequest>(memoryStream);

            var symbols = subscription.Symbols
                .Select(symbol => symbol.Trim().ToLower())
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .ToList();

            sessionManager.Subscribe(sessionId, symbols);
        }
        catch (Exception ex)
        {
            Console.WriteLine("WebSocket subscription parse error:");
            Console.WriteLine(ex.Message);
        }
    }
}

static async Task<byte[]?> ReceiveBinaryMessageAsync(WebSocket socket)
{
    var buffer = new byte[4096];

    using var memoryStream = new MemoryStream();

    WebSocketReceiveResult result;

    do
    {
        result = await socket.ReceiveAsync(
            new ArraySegment<byte>(buffer),
            CancellationToken.None
        );

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closed",
                CancellationToken.None
            );

            return null;
        }

        memoryStream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return memoryStream.ToArray();
}

class OidcDiscovery
{
    public string Issuer { get; set; } = "";
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string JwksUri { get; set; } = "";
}

class JwtValidationResult
{
    public bool IsValid { get; set; }
    public Dictionary<string, string> Claims { get; set; } = new();
    public string? Error { get; set; }

    public static JwtValidationResult Success(Dictionary<string, string> claims)
    {
        return new JwtValidationResult
        {
            IsValid = true,
            Claims = claims
        };
    }

    public static JwtValidationResult Fail(string error)
    {
        return new JwtValidationResult
        {
            IsValid = false,
            Error = error
        };
    }
}