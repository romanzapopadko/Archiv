using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;
using System.Net;
using Gateway.Options;
using System.Diagnostics;

namespace Gateway.Infrastructure.Resilience;

public class ResilienceHttpClientFactory : IForwarderHttpClientFactory
{
    private readonly IOptions<ResilienceOptions> _options;

    public ResilienceHttpClientFactory(IOptions<ResilienceOptions> options)
    {
        _options = options;
    }

    public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        return new HttpMessageInvoker(new SimpleRetryHandler(handler, _options.Value));
    }
}
