using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Wubble.Api.Auth;
using Wubble.Api.Data;
using Wubble.Api.Email;
using Wubble.Api.OpenApi;
using Wubble.Api.Users;
using System.Reflection;
using Wubble.Api.Monitoring;

var builder = WebApplication.CreateBuilder(args);

// ---------- Настройки ----------
// Валидация ключа — ValidateOnStart, а не чтение конфигурации прямо здесь:
// генерация openapi.json при сборке (и в CI) поднимает приложение без секретов,
// и проверка «на месте» ломала бы сборку документации.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var jwtOptionsBuilder = builder.Services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.Section)
    .Validate(o => Encoding.UTF8.GetByteCount(o.SigningKey) >= 32,
        "Jwt:SigningKey must be at least 32 bytes. Set it via user-secrets or environment variables.");

if (!isOpenApiGeneration)
    jwtOptionsBuilder.ValidateOnStart();

var recaptchaOptionsBuilder = builder.Services
    .AddOptions<RecaptchaOptions>()
    .BindConfiguration(RecaptchaOptions.Section)
    .Validate(
        o => !string.IsNullOrWhiteSpace(o.SecretKey),
        "Recaptcha:SecretKey is required.");

if (!isOpenApiGeneration)
    recaptchaOptionsBuilder.ValidateOnStart();

builder.Services.AddHttpClient<RecaptchaVerifier>();
builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section);
builder.Services.AddOptions<CorsSettings>().BindConfiguration(CorsSettings.Section);
builder.Services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section);

builder.Services.AddSingleton(TimeProvider.System);

// ---------- База данных ----------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ---------- Identity ----------
// AddIdentityCore, а не AddIdentity: последний регистрирует cookie-схемы входа, которые нам не нужны.
builder.Services
    .AddIdentityCore<User>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";

        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

        // Подтверждение email проверяем вручную после пароля (см. AuthService.LoginAsync).
        options.SignIn.RequireConfirmedEmail = false;

        // Чтобы UserManager.GetUserAsync(User) понимал наши JWT-claims.
        options.ClaimsIdentity.UserIdClaimType = AuthClaims.UserId;
        options.ClaimsIdentity.UserNameClaimType = AuthClaims.Name;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddOptions<DataProtectionTokenProviderOptions>()
    .Configure<IOptions<AuthOptions>>((o, auth) => o.TokenLifespan = auth.Value.EmailConfirmationTokenLifetime);

// Ключи в PostgreSQL: иначе после перезапуска контейнера все ссылки из писем станут недействительны.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Wubble");
if (isOpenApiGeneration)
    dataProtection.UseEphemeralDataProtectionProvider();
else
    dataProtection.PersistKeysToDbContext<AppDbContext>();

// ---------- Аутентификация (JWT) ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

// Настраиваем через options, чтобы ключ читался лениво (см. комментарий про openapi.json выше).
// Токен читается только из заголовка Authorization — cookie wubble_access сервер игнорирует.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Сначала пробуем получить токен из Authorization.
                // Если заголовка нет — читаем access-токен из cookie.
                if (string.IsNullOrEmpty(context.Token))
                {
                    context.Token = context.Request.Cookies["wubble_access"];
                }

                return Task.CompletedTask;
            },

            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirst(AuthClaims.UserId)?.Value, out var userId)
                    || !Guid.TryParse(context.Principal?.FindFirst(AuthClaims.SessionId)?.Value, out var sessionId)
                    || !await context.HttpContext.RequestServices.GetRequiredService<TokenService>()
                        .IsSessionActiveAsync(userId, sessionId, context.HttpContext.RequestAborted))
                {
                    context.Fail("Session has been revoked or expired.");
                }
            },
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = AuthClaims.Name,
        };
    });

builder.Services.AddAuthorization();

// ---------- Сервисы аутентификации ----------
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EmailVerificationService>();
builder.Services.AddSingleton<AuthCookies>();
builder.Services.AddSingleton<OriginGuard>();
builder.Services.AddSingleton<IMailSender, SmtpMailSender>();

// ---------- Мониторинг ----------
builder.Services.AddOptions<MonitoringOptions>().BindConfiguration("Monitoring")
    .Validate(o => o.MaxConcurrentRuns is >= 1 and <= 50 && o.PollIntervalSeconds is >= 1 and <= 60,
        "Monitoring: MaxConcurrentRuns must be 1..50; PollIntervalSeconds must be 1..60.")
    .ValidateOnStart();
builder.Services.AddHttpClient<BrevoMailSender>();

builder.Services.AddTransient<IMailSender>(sp =>
{
    var emailOptions = sp.GetRequiredService<IOptions<EmailOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(emailOptions.ApiKey))
        return sp.GetRequiredService<BrevoMailSender>();

    return ActivatorUtilities.CreateInstance<SmtpMailSender>(sp);
});
builder.Services.AddScoped<CheckRunner>();
builder.Services.AddHttpClient<ICheckExecutor, HttpCheckExecutor>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        MaxResponseHeadersLength = 32,
        ConnectCallback = TargetPolicy.ConnectAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
if (!isOpenApiGeneration)
{
    builder.Services.AddHostedService<MonitoringScheduler>();
    builder.Services.AddHostedService<IncidentNotificationWorker>();
}

// ---------- CORS ----------
// Фронт шлёт auth-запросы с credentials: 'include', поэтому нужен явный список origin.
var allowedOrigins = builder.Configuration
    .GetSection(CorsSettings.Section).Get<CorsSettings>()?.AllowedOrigins ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

// ---------- Rate limiting ----------
// За reverse proxy нужно настроить ForwardedHeaders, иначе все запросы будут с IP прокси.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("monitoring-run", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(AuthClaims.UserId)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
            }));
});

// ---------- MVC, ошибки, OpenAPI ----------
builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // Единый машиночитаемый код и для автоматических ошибок валидации [ApiController].
        if (context.ProblemDetails is ValidationProblemDetails)
            context.ProblemDetails.Extensions.TryAdd("code", "validation_error");
    });
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    options.AddOperationTransformer<BearerOperationTransformer>();
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

