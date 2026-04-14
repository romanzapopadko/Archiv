using Gateway;
using Gateway.Infrastructure.Resilience;
using Gateway.Middleware;
using Gateway.Models;
using Gateway.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Ocelot.Requester;
using System.Security.Cryptography;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);

// --- 1. Конфигурация (сначала файлы) ---
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
builder.Configuration.AddJsonFile("appsettings.json", false, true);
builder.Configuration.AddJsonFile("ocelot.json", false, true);
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
            ValidateIssuer = false, // Как в вашем примере
            ValidIssuer = jwtSettings.AccessTokenSettings.Issuer,
            ValidateAudience = false, // Как в вашем примере
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
builder.Services.AddCors();
builder.Services.AddControllers();

// --- 5. Регистрация Шлюза ---
var useYarp = builder.Configuration.GetValue<bool>("Gateway:UseYarp", false);

if (useYarp)
{
    // Регистрируем именованный HttpClient с вашей логикой устойчивости (Retry, Jitter и т.д.)
    builder.Services.AddHttpClient("YarpClient")
        .AddKenseResilience(builder.Configuration);

    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
        .AddTransforms<RouteRetryTransformProvider>()
        // Привязываем YARP к нашему устойчивому HttpClient
        .ConfigureHttpClient((context, handler) => { });

}
else
{
    builder.Services.AddOcelot(builder.Configuration);
    builder.Services.AddSwaggerForOcelot(builder.Configuration);
}

var app = builder.Build();

// --- 6. Настройка Pipeline ---

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    //app.UseSwaggerUI();
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

// Middleware (Логирование отключаем для k6)
app.UseMiddleware<GatewayLoggingMiddleware>();
app.UseMiddleware<YarpForwardingMiddleware>();

app.UseHttpsRedirection();
app.UseWebSockets();

// Auth строго перед проксированием
app.UseAuthentication();
app.UseAuthorization();

if (useYarp)
{
    app.MapReverseProxy();
}
else
{
    // Настройка таймаутов Ocelot
    var service = app.Services.GetService(typeof(IMessageInvokerPool)) as MessageInvokerPool;
    if (service != null)
    {
        service.RequestTimeoutSeconds = builder.Configuration.GetValue<int>("RequestTimeoutSeconds", 600);
    }
    app.UseSwaggerForOcelotUI();
    await app.UseOcelot();
}

app.MapControllers();
await app.RunAsync();