using Gateway;
using Gateway.Infrastructure.Resilience;
using Gateway.Middleware;
using Gateway.Models;
using Gateway.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Polly;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;


var builder = WebApplication.CreateBuilder(args);

// --- 1. Конфигурация (сначала файлы) ---
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
builder.Configuration.AddJsonFile("appsettings.json", false, true);
builder.Configuration.AddEnvironmentVariables();

// --- 2. Настройка Аутентификации (RSA JWT) ---
var jwtSettingsConfiguration = builder.Configuration.GetSection("JwtSettings");
builder.Services.Configure<JwtSettings>(jwtSettingsConfiguration);
var jwtSettings = jwtSettingsConfiguration.Get<JwtSettings>();

if (jwtSettings?.AccessTokenSettings?.PublicKey != null)
{
    var rsa = RSA.Create();
    rsa.ImportRSAPublicKey(
        source: Convert.FromBase64String(jwtSettings.AccessTokenSettings.PublicKey),
        bytesRead: out int _);
    var key = new RsaSecurityKey(rsa);

    builder.Services.AddAuthentication(cfg =>
    {
        cfg.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        cfg.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        cfg.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    }).AddJwtBearer(x =>
    {
        x.RequireHttpsMetadata = false;
        x.SaveToken = false;
        x.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateIssuer = false, 
            ValidIssuer = jwtSettings.AccessTokenSettings.Issuer,
            ValidateAudience = false, 
            ValidAudience = jwtSettings.AccessTokenSettings.Audience,
            ValidateLifetime = true
        };
    });
}

// --- 3. Настройка Авторизации ---
builder.Services.AddAuthorization(options => {
    options.AddPolicy("GatewayAuth", p => {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
    });

    options.AddPolicy("InternalService", p => {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireClaim("scope", "kense.internal");
    });
});

// --- 4. Стандартные сервисы ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<PolicySettings>(builder.Configuration.GetSection("PolicySettings"));
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection("Gateway:Resilience"));

var resilienceCheck = builder.Configuration.GetSection("Gateway:Resilience").Get<ResilienceOptions>();

builder.Services.AddCors();
builder.Services.AddControllers();

// 4.1.1 Настройка двухуровневого кэша

var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

// Регистрация Multiplexer для общего доступа и прогрева
var multiplexer = ConnectionMultiplexer.Connect(redisConnectionString);
builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);

builder.Services.AddSingleton<Gateway.Options.CachePolicyEngine>();

builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 100 * 1024 * 1024; // 100 MB
});

// Настройка L2 (Distributed Cache) через тот же multiplexer
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(multiplexer);
});

// Настройка FusionCache Backplane
builder.Services.AddSingleton<IFusionCacheBackplane>(sp =>
{
    return new RedisBackplane(new RedisBackplaneOptions
    {
        ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(multiplexer)
    });
});

builder.Services.AddFusionCache()
    .WithOptions(options => {
        options.DefaultEntryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromMinutes(5),
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = TimeSpan.FromHours(2)
        };
    })
    .WithSerializer(new ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson.FusionCacheSystemTextJsonSerializer())
    .WithRegisteredMemoryCache()
    .WithRegisteredDistributedCache();

// --- 5. Регистрация Шлюза ---
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // Добавляем проверку на null для Route и Metadata
        if (context.Route?.Metadata != null)
        {
            // 1. Пробрасываем флаг включения из Metadata в заголовок
            if (context.Route.Metadata.TryGetValue("RetryEnabled", out var enabled))
            {
                context.AddRequestHeader("X-Internal-Retry-Enabled", enabled);
            }

            // 2. Пробрасываем количество попыток из Metadata в заголовок
            if (context.Route.Metadata.TryGetValue("RetryCount", out var count))
            {
                context.AddRequestHeader("X-Internal-Retry-Count", count);
            }
        }
    })
    .AddTransforms<RouteRetryTransformProvider>();

// Регистрируем нашу фабрику (она подхватит SimpleRetryHandler)
builder.Services.AddSingleton<IForwarderHttpClientFactory, ResilienceHttpClientFactory>();

var app = builder.Build();

// --- 6. Настройка Pipeline ---

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        // Указываем путь ЧЕРЕЗ наш шлюз (YARP перенаправит это на нужный сервис)
        options.SwaggerEndpoint("/Auth/swagger/v1/swagger.json", "Auth API - v1");
        options.SwaggerEndpoint("/AuthV2/swagger/v2/swagger.json", "Auth API - v2");
        options.SwaggerEndpoint("/Gov/swagger/v1/swagger.json", "Gov API - v1");
        options.SwaggerEndpoint("/Broker/swagger/v1/swagger.json", "BrokerGateway API - v1");
        options.SwaggerEndpoint("/Messages/swagger/v1/swagger.json", "SendMessages API - v1");
        options.SwaggerEndpoint("/CustomerSettings/swagger/v1/swagger.json", "CustomerSettings API - v1");
        options.SwaggerEndpoint("/CustomerSettingsV2/swagger/v2/swagger.json", "CustomerSettings API - v2");
        options.SwaggerEndpoint("/DashboardsImport/swagger/v1/swagger.json", "Dashboards import API - v1");
        options.SwaggerEndpoint("/References/swagger/v1/swagger.json", "References API - v1");
        options.SwaggerEndpoint("/LogView/swagger/v1/swagger.json", "LogView API - v1");
        options.SwaggerEndpoint("/MappingControl/swagger/v1/swagger.json", "MappingControl API - v1");
        options.SwaggerEndpoint("/Calendar/swagger/v1/swagger.json", "CalendarService API - v1");
        options.SwaggerEndpoint("/ReferenceInfo/swagger/v1/swagger.json", "ReferenceInfo API - v1");
        options.SwaggerEndpoint("/CalendarBroker/swagger/v1/swagger.json", "CalendarBroker API - v1");
        options.SwaggerEndpoint("/AiChat/swagger/v1/swagger.json", "AiChatHandler API - v1");
        options.SwaggerEndpoint("/Notifications/swagger/v1/swagger.json", "Notifications API - v1");
        options.SwaggerEndpoint("/ExportExcel/swagger/v1/swagger.json", "ExportExcel API - v1");
        options.SwaggerEndpoint("/AiHelper/swagger/v1/swagger.json", "AiHelper Kenes API - v1");
    });
}

app.UseCors(b => b.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseRouting();

// Auth строго перед проксированием
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<GatewayLoggingMiddleware>();
app.UseMiddleware<FusionCacheMiddleware>();
app.UseMiddleware<YarpForwardingMiddleware>();

app.UseHttpsRedirection();
app.UseWebSockets();

app.MapReverseProxy();

app.MapControllers();

try
{
    //Console.WriteLine("[WARMUP] Начинаю прогрев систем...");

    // Прогрев Redis
    var db = multiplexer.GetDatabase();
    await db.PingAsync();
    //Console.WriteLine("[WARMUP] Redis подключен и прогрет.");

    // Прогрев Сериализатора
    var serializer = new ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson.FusionCacheSystemTextJsonSerializer();
    serializer.Serialize(new Gateway.Middleware.CachedResponse());
    //Console.WriteLine("[WARMUP] Сериализатор прогрет.");
}
catch (Exception ex)
{
    Console.WriteLine($"[WARMUP ERROR] Ошибка при прогреве: {ex.Message}");
}

await app.RunAsync();

