using System.Diagnostics;

namespace Gateway.Middleware
{
    public class GatewayLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GatewayLoggingMiddleware> _logger;

        public GatewayLoggingMiddleware(RequestDelegate next, ILogger<GatewayLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 1. Генерация X-Correlation-Id (Заменяет {TraceIdentifier} из Transforms)
            var correlationId = Guid.NewGuid().ToString();
            context.Request.Headers["X-Correlation-Id"] = correlationId;
            context.Response.Headers["X-Correlation-Id"] = correlationId;

            // 2. Проброс IP-адресов (Заменяет X-Forwarded-For и X-Real-IP из Transforms)
            var remoteIp = context.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(remoteIp))
            {
                // Устанавливаем реальный IP
                context.Request.Headers["X-Real-IP"] = remoteIp;

                // Добавляем в цепочку Forwarded-For
                if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var existingForwarded))
                {
                    context.Request.Headers["X-Forwarded-For"] = $"{existingForwarded}, {remoteIp}";
                }
                else
                {
                    context.Request.Headers["X-Forwarded-For"] = remoteIp;
                }
            }

            // 3. Проброс X-Tenant-Id 
            string tenantInfo = "N/A";
            if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantId))
            {
                context.Request.Headers["X-Tenant-Id"] = tenantId;
                tenantInfo = tenantId.ToString();
            }

            // 4. BeginScope для структурированных логов
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["TenantId"] = tenantInfo,
                ["RemoteIp"] = remoteIp ?? "unknown"
            }))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await _next(context);
                    sw.Stop();

                    // Лог успешного запроса
                    _logger.LogInformation(
                        "Gateway Request: {Method} {Path} | Status: {StatusCode} | {ElapsedMs}ms",
                        context.Request.Method,
                        context.Request.Path,
                        context.Response.StatusCode,
                        sw.ElapsedMilliseconds
                    );
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, "Gateway Request Failed: {Method} {Path} | {ElapsedMs}ms",
                        context.Request.Method, context.Request.Path, sw.ElapsedMilliseconds);
                    throw;
                }
            }
        }
    }
}