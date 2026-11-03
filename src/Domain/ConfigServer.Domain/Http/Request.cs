using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using ConfigServer.Domain.Http.Configurations;
using ConfigServer.Domain.Http.Implementations;
using ConfigServer.Domain.Http.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace ConfigServer.Domain.Http
{
    internal class Request
    {
        private readonly RequestConfiguration _configuration;

        internal Request(RequestConfiguration configuration)
        {
            _configuration = configuration;
        }
        public IHttpMethod<TEntity> Build<TEntity>(IHttpClientFactory httpClientFactory, ILogger logger)
        {
            switch (_configuration.HttpMethod.ToUpperInvariant())
            {
                case "POST":
                    return new HttpRequestPost<TEntity>(
                        client: CreateHttpClient(httpClientFactory),
                        addressSuffix: _configuration.RequestSufix,
                        logger: logger);
                case "GET":
                    return new HttpRequestGet(
                        client: CreateHttpClient(httpClientFactory),
                        addressSuffix: _configuration.RequestSufix,
                        logger: logger) as IHttpMethod<TEntity>;
                default:
                    return null;
            }
        }
        public IServiceCollection ConfigureServices(Action<HttpClient> httpClient = null, IHttpRequestRetryHandler retryHandler = null)
        {
            var services = new ServiceCollection();
            Action<HttpClient> httpClientAction;

            if (httpClient == null)
                httpClientAction = c =>
                {
                    c.BaseAddress = new Uri(_configuration.BaseAddress);
                    foreach (var (key, value) in _configuration.RequestHeaders)
                        c.DefaultRequestHeaders.Add(key, value);
                };
            else
                httpClientAction = httpClient;
            ConfigurePolly(services, retryHandler);

            services.AddHttpClient(GetRequestName(), httpClientAction)
                .AddPolicyHandlerFromRegistry(_configuration.Polly.Name);

            return services;
        }

        private HttpClient CreateHttpClient(IHttpClientFactory httpClientFactory)
        {
            return httpClientFactory.CreateClient(GetRequestName());
        }

        internal TService GetService<TService>(IServiceProvider serviceProvider) where TService : class
        {
            return serviceProvider.GetService(typeof(TService)) as TService;
        }


       

        private void ConfigurePolly(IServiceCollection service, IHttpRequestRetryHandler retryHandler = null)
        {
            if (_configuration.Polly == null) return;


            AsyncRetryPolicy<HttpResponseMessage> policy;



            if (retryHandler == null)
            {
                policy = Policy
                   .Handle<Exception>()
                   .OrResult<HttpResponseMessage>(response => !response.IsSuccessStatusCode)
                   .WaitAndRetryAsync(
                       retryCount: _configuration.Polly.Retries,
                       sleepDurationProvider: retryAttempt =>
                           TimeSpan.FromSeconds(_configuration.Polly.IntervalInSeconds));

            }
            else
            {
                policy = Policy
                    .Handle<Exception>()
                    .OrResult<HttpResponseMessage>(response => !response.IsSuccessStatusCode)
                    .WaitAndRetryAsync(
                        retryCount: _configuration.Polly.Retries,
                        sleepDurationProvider: retryAttempt =>
                            TimeSpan.FromSeconds(_configuration.Polly.IntervalInSeconds),
                        onRetry: retryHandler.OnRetry);
            }

            service.AddPolicyRegistry().Add(_configuration.Polly.Name, policy);

        }


        private string GetRequestName()
        {
            return string.IsNullOrEmpty(_configuration.RequestName)
                ? $"DEFAULT_REQUEST"
                : _configuration.RequestName;
        }
    }
}
