using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Gateway.Infrastructure.Resilience;

public class RouteRetryTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }

    public void Apply(TransformBuilderContext context)
    {
        if (context.Route.Metadata?.TryGetValue("RetryEnabled", out var enabled) == true)
        {
            context.AddRequestHeader("X-Internal-Retry-Enabled", enabled);
        }
    }
}