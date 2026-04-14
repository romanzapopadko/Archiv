using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Yarp.ReverseProxy.Forwarder;
using Gateway.Options;

namespace Gateway.Middleware
{
    public class YarpForwardingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<YarpForwardingMiddleware> _logger;
        private readonly ResilienceOptions _options;

        public YarpForwardingMiddleware(RequestDelegate next,
            ILogger<YarpForwardingMiddleware> logger,
            IOptions<ResilienceOptions> options)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();

            await _next(context); // Здесь YARP выполняет проксирование

            stopwatch.Stop();

            // ПУНКТ 2.1.7: Перехват IProxyErrorFeature
            var errorFeature = context.GetForwarderErrorFeature();

            if (errorFeature != null)
            {
                // ПУНКТ 2.1.7: Логирование (Error, RouteId, ElapsedMs)
                var routeId = context.GetRouteModel()?.Config.RouteId ?? "unknown";

                _logger.LogError(
                    "Gateway Proxy Error: {Error}, Route: {RouteId}, Elapsed: {ElapsedMs}ms, Exception: {Exception}",
                    errorFeature.Error,
                    routeId,
                    stopwatch.ElapsedMilliseconds,
                    errorFeature.Exception?.Message);

                // ПУНКТ 2.1.7: Возвращать 502 без повторов
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }
            }
        }
    }
}
