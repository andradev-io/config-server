using System;
using System.Net.Http;
using ConfigServer.Domain.Http.Configurations;
using ConfigServer.Domain.Http.Implementations;
using ConfigServer.Domain.Http.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace ConfigServer.Domain.Http
{
    public class RequestFactory
    {

        /// <summary>
        /// Creates a request based on request configuration class.
        /// Its also possible to configure httpclient as you wish and pass.
        /// Futhermore you can create your on implementation for retries.  
        /// </summary>
        /// <typeparam name="TEntity">Parameters type</typeparam>
        /// <param name="configuration"> Configuration of request</param>
        /// <param name="httpClientConfigurations">Pass your own httpclient configuration</param>
        /// <param name="retryHandler">Case you want to create a custom retry implementation</param>
        /// <returns></returns>
        public static IHttpMethod<TEntity> Build<TEntity>(RequestConfiguration configuration, Action<HttpClient> httpClientConfigurations = null, IHttpRequestRetryHandler retryHandler = null, ILogger logger=null)
        {

            var requestBuilder = new Request(configuration);
            var services = requestBuilder.ConfigureServices(httpClientConfigurations, retryHandler);
            var serviceProvider = services.BuildServiceProvider();
            var httpClientFactory = requestBuilder.GetService<IHttpClientFactory>(serviceProvider);
            return requestBuilder.Build<TEntity>(httpClientFactory,logger);
        }
        /// <summary>
        /// Creates a request based on request configuration class. Where parameters will be passed as string.<para />
        /// Its also possible to configure httpclient as you wish and pass.<para />
        /// Futhermore you can create your on implementation for retries.  <para />
        /// </summary>
        /// <param name="configuration"> Configuration of request</param>
        /// <param name="httpClientConfigurations">Pass your own httpclient configuration</param>
        /// <param name="retryHandler">Case you want to create a custom retry implementation</param>
        /// <returns>IHttpMethod</returns>
        public static IHttpMethod<string> Build(RequestConfiguration configuration,
            Action<HttpClient> httpClientConfigurations = null, IHttpRequestRetryHandler retryHandler = null)
        {
            return Build<string>(configuration, httpClientConfigurations, retryHandler);
        }

      

    }
}
