using System.Net;
using Gateway.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Gateway.Infrastructure.Resilience
{
    public static class YarpResilienceExtensions
    {
        public static IHttpClientBuilder AddKenseResilience(
            this IHttpClientBuilder builder, IConfiguration config)
        {
            var opts = config.GetSection("Gateway:Resilience").Get<ResilienceOptions>() ?? new ResilienceOptions();

            if (!opts.RetryEnabled) return builder;

            // Мы вызываем метод, но чтобы вернуть IHttpClientBuilder, 
            // нам не нужно сохранять результат AddStandardResilienceHandler в переменную,
            // так как он расширяет тот же самый builder.
            builder.AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = opts.MaxRetryAttempts;
                options.Retry.Delay = TimeSpan.FromMilliseconds(opts.BaseDelayMs);
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;

                options.Retry.ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response =>
                    {
                        bool isTransientError = response.StatusCode is
                            HttpStatusCode.RequestTimeout or
                            HttpStatusCode.BadGateway or
                            HttpStatusCode.ServiceUnavailable or
                            HttpStatusCode.GatewayTimeout;

                        var method = response.RequestMessage?.Method;
                        bool isIdempotent = method == HttpMethod.Get || method == HttpMethod.Head;

                        return isTransientError && isIdempotent;
                    });
            });

            // Возвращаем исходный builder, который теперь содержит настроенный обработчик
            return builder;
        }
    }
}