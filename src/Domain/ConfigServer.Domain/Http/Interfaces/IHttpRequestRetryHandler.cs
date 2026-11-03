using System;
using System.Net.Http;
using Polly;

namespace ConfigServer.Domain.Http.Interfaces
{
    public interface IHttpRequestRetryHandler
    {

        void OnRetry(DelegateResult<HttpResponseMessage> result,
           TimeSpan interval,
           int retryCount,
           Context context);
    }
}
