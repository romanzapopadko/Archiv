using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Gateway.Infrastructure.Resilience
{
    public class RouteRetryTransformProvider : ITransformProvider
    {
        public void ValidateRoute(TransformRouteValidationContext context) { }
        public void ValidateCluster(TransformClusterValidationContext context) { }

        public void Apply(TransformBuilderContext context)
        {
            // ПРОВЕРКА НА NULL: Убеждаемся, что Route и Metadata существуют
            string headerValue = "disabled";

            if (context.Route?.Metadata != null &&
                context.Route.Metadata.TryGetValue("RetryEnabled", out var retryEnabledMetadata))
            {
                var isEnabled = string.Equals(retryEnabledMetadata, "true", StringComparison.OrdinalIgnoreCase);
                headerValue = isEnabled ? "enabled" : "disabled";
            }

            // Добавляем заголовок (теперь безопасно)
            context.AddRequestHeader("X-Gateway-Retry", headerValue, append: false);
        }
    }
}
