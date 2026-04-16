using Yarp.ReverseProxy.Model;

namespace Gateway.Options
{
    public class CachePolicyEngine
    {
        public (bool IsCacheable, TimeSpan Ttl) GetPolicy(HttpContext context)
        {
            // 1. Получаем Endpoint, который выставил app.UseRouting()
            var endpoint = context.GetEndpoint();
            if (endpoint == null) return (false, TimeSpan.Zero);

            // 2. Извлекаем модель маршрута YARP из метаданных Endpoint
            var routeModel = endpoint.Metadata.GetMetadata<RouteModel>();
            var metadata = routeModel?.Config?.Metadata;

            if (metadata == null) return (false, TimeSpan.Zero);

            // Диагностика для консоли
            Console.WriteLine($"[DEBUG-CACHE] Маршрут: {routeModel.Config.RouteId}");

            // 3. Проверка флага CacheEnabled (без учета регистра)
            var enabledEntry = metadata.FirstOrDefault(m =>
                m.Key.Equals("CacheEnabled", StringComparison.OrdinalIgnoreCase));

            if (enabledEntry.Value != null &&
                bool.TryParse(enabledEntry.Value, out var isEnabled) && isEnabled)
            {
                TimeSpan ttl = TimeSpan.FromMinutes(5);
                var ttlEntry = metadata.FirstOrDefault(m =>
                    m.Key.Equals("CacheTtl", StringComparison.OrdinalIgnoreCase));

                if (ttlEntry.Value != null) TimeSpan.TryParse(ttlEntry.Value, out ttl);

                return (true, ttl);
            }

            return (false, TimeSpan.Zero);
        }

        public string GenerateKey(HttpRequest req)
        {
            var endpoint = req.HttpContext.GetEndpoint();
            var routeModel = endpoint?.Metadata.GetMetadata<RouteModel>();
            var routeId = routeModel?.Config?.RouteId ?? "default";

            var tenant = req.Headers["X-Tenant-Id"].ToString() ?? "no-tenant";
            var rawString = $"{req.Method}|{req.Path}|{req.QueryString}|{tenant}";

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawString)));

            return $"gw:{routeId}:{hash}";
        }
    }
}