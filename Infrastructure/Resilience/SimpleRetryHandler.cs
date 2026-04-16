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
        // 1. Извлекаем настройки из заголовков (которые подложил YARP из Metadata)
        // Если заголовков нет — используем глобальные настройки из _opts

        bool isEnabledForRoute = _opts.RetryEnabled;
        if (request.Headers.TryGetValues("X-Internal-Retry-Enabled", out var enabledVals))
        {
            bool.TryParse(enabledVals.FirstOrDefault(), out isEnabledForRoute);
            request.Headers.Remove("X-Internal-Retry-Enabled");
        }

        int maxAttempts = _opts.MaxRetryAttempts;
        if (request.Headers.TryGetValues("X-Internal-Retry-Count", out var countVals))
        {
            if (int.TryParse(countVals.FirstOrDefault(), out var customCount))
                maxAttempts = customCount;

            request.Headers.Remove("X-Internal-Retry-Count");
        }

        // Добавляем отладочные заголовки (чтобы видеть в браузере/логах что выбрано)
        request.Headers.TryAddWithoutValidation("X-Gateway-Retry-Status", isEnabledForRoute ? "active" : "disabled");
        request.Headers.TryAddWithoutValidation("X-Gateway-Retry-Max", maxAttempts.ToString());

        // 2. Быстрый выход: если ретраи выключены (в Metadata или глобально)
        if (!isEnabledForRoute || (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // 3. Основной цикл повторов
        HttpResponseMessage lastResponse = null!;
        int totalAttempts = maxAttempts + 1;

        for (int i = 0; i < totalAttempts; i++)
        {
            try
            {
                var currentRequest = await CloneRequest(request);

                if (i > 0)
                {
                    currentRequest.Headers.TryAddWithoutValidation("X-Gateway-Is-Retry", "true");
                    currentRequest.Headers.TryAddWithoutValidation("X-Gateway-Attempt", (i + 1).ToString());
                }

                lastResponse = await base.SendAsync(currentRequest, cancellationToken);

                // Если код < 500 — это успех или клиентская ошибка, ретрай не нужен
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

            if (i < totalAttempts - 1)
            {
                await Task.Delay(_opts.BaseDelayMs, cancellationToken);
            }
        }

        return lastResponse;
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