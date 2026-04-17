using ZiggyCreatures.Caching.Fusion;
using Gateway.Options;
using Yarp.ReverseProxy.Model;

namespace Gateway.Middleware
{
    // 1. Выносим модель данных кэша, чтобы она была видна всему пространству имен
    public class CachedResponse
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string? ContentType { get; set; }
        public Dictionary<string, string[]> Headers { get; set; } = new();
    }

    public class FusionCacheMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IFusionCache _cache;
        private readonly CachePolicyEngine _engine;

        public FusionCacheMiddleware(RequestDelegate next, IFusionCache cache, CachePolicyEngine engine)
        {
            _next = next;
            _cache = cache;
            _engine = engine;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var request = context.Request;

            if (request.Method != "GET")
            {
                await _next(context);
                return;
            }

            //Console.WriteLine($"[DEBUG] Кэш проверяет путь: {request.Path}");

            var (isCacheable, ttl) = _engine.GetPolicy(context);
            if (!isCacheable)
            {
                await _next(context);
                return;
            }

            var cacheKey = _engine.GenerateKey(request);
            var cachedResponse = await _cache.GetOrDefaultAsync<CachedResponse>(cacheKey);

            if (cachedResponse != null)
            {
                context.Response.Headers.TryAdd("X-Cache-Status", "HIT-Gateway");
                //Console.WriteLine($"[CACHE] HIT: {cacheKey}");
                await ApplyCachedResponse(context.Response, cachedResponse);
                return;
            }

            //Console.WriteLine($"[CACHE] MISS: {cacheKey}. Fetching from backend...");

            var originalBody = context.Response.Body;
            using var ms = new MemoryStream();
            context.Response.Body = ms;

            // Идем на бэкенд
            await _next(context);

            // Сначала возвращаем данные клиенту, чтобы он не ждал Redis!
            ms.Seek(0, SeekOrigin.Begin);
            await ms.CopyToAsync(originalBody);

            // Теперь, когда клиент уже получил ответ, спокойно сохраняем в кэш (в фоне)
            if (context.Response.StatusCode == 200 && ms.Length <= 4 * 1024 * 1024)
            {
                var body = ms.ToArray();
                var toCache = new CachedResponse
                {
                    Body = body,
                    ContentType = context.Response.ContentType,
                    Headers = context.Response.Headers
                        .Where(h => !h.Key.StartsWith("Set-Cookie") && !h.Key.StartsWith("X-Cache"))
                        .ToDictionary(h => h.Key, h => h.Value.ToArray())
                };

                // Используем фоновую задачу для сохранения в Redis
                // Это уберет задержку "MISS" для пользователя
                _ = Task.Run(async () => {
                    try
                    {
                        await _cache.SetAsync(cacheKey, toCache, opt => opt.SetDuration(ttl).SetSize(body.Length));
                        //Console.WriteLine($"[CACHE] Background Save OK: {cacheKey}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CACHE] Background Save Error: {ex.Message}");
                    }
                });
            }
        }

        private async Task ApplyCachedResponse(HttpResponse response, CachedResponse cached)
        {
            response.StatusCode = 200;
            response.ContentType = cached.ContentType;

            foreach (var header in cached.Headers)
            {
                response.Headers[header.Key] = header.Value;
            }

            await response.Body.WriteAsync(cached.Body);
        }
    }
}