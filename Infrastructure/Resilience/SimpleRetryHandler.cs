using Gateway.Options;
using System.Net;

namespace Gateway.Infrastructure.Resilience;

public class SimpleRetryHandler : DelegatingHandler
{
    private readonly ResilienceOptions _opts;

    public SimpleRetryHandler(HttpMessageHandler innerHandler, ResilienceOptions opts)
        : base(innerHandler)
    {
        _opts = opts;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1. Определение итогового статуса политики
        bool isEnabledForRoute;

        if (!_opts.RetryEnabled)
        {
            isEnabledForRoute = false;
            if (request.Headers.Contains("X-Internal-Retry-Enabled"))
                request.Headers.Remove("X-Internal-Retry-Enabled");
        }
        else
        {
            isEnabledForRoute = true;
            if (request.Headers.TryGetValues("X-Internal-Retry-Enabled", out var values))
            {
                if (bool.TryParse(values.FirstOrDefault(), out var result))
                    isEnabledForRoute = result;

                request.Headers.Remove("X-Internal-Retry-Enabled");
            }
        }

        // --- ДОБАВЛЯЕМ ТРАССИРОВОЧНЫЙ ЗАГОЛОВОК ДЛЯ СЕРВИСА ---
        // Это позволит увидеть на стороне API, разрешены ли ретраи для этого запроса
        request.Headers.TryAddWithoutValidation("X-Gateway-Retry", isEnabledForRoute ? "enabled" : "disabled");

        // 2. Быстрый выход: если ретраи выключены ИЛИ метод не является безопасным (GET/HEAD)
        if (!isEnabledForRoute || (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // 3. Основной цикл повторов
        HttpResponseMessage lastResponse = null;
        int totalAttempts = _opts.MaxRetryAttempts + 1;

        for (int i = 0; i < totalAttempts; i++)
        {
            try
            {
                var currentRequest = await CloneRequest(request);

                // Если это не первая попытка (i > 0), добавляем заголовки ретрая
                if (i > 0)
                {
                    currentRequest.Headers.TryAddWithoutValidation("X-Gateway-Is-Retry", "true");
                    currentRequest.Headers.TryAddWithoutValidation("X-Gateway-Attempt", (i + 1).ToString());
                }

                lastResponse = await base.SendAsync(currentRequest, cancellationToken);

                // Если код ответа < 500 (успех или ошибка клиента 4xx), возвращаем ответ
                if ((int)lastResponse.StatusCode < 500)
                {
                    return lastResponse;
                }

                Console.WriteLine($"[GATEWAY] Попытка №{i + 1} для {request.RequestUri?.AbsolutePath} вернула {lastResponse.StatusCode}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] Ошибка попытки №{i + 1}: {ex.Message}");
                if (i == totalAttempts - 1) throw;
            }

            // Задержка перед следующей попыткой
            if (i < totalAttempts - 1)
            {
                await Task.Delay(_opts.BaseDelayMs, cancellationToken);
            }
        }

        return lastResponse!;
    }

    private async Task<HttpRequestMessage> CloneRequest(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };

        foreach (var prop in req.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(prop.Key), prop.Value);

        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (req.Content != null)
        {
            var ms = new MemoryStream();
            await req.Content.CopyToAsync(ms);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);

            foreach (var h in req.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }
}