using System;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Domain.Http.Abstractions
{
    internal class BaseHttpRequestMethod<TEntity>
    {
        protected readonly string AddressSuffix;
        protected readonly HttpClient Client;
        protected BaseHttpRequestMethod(HttpClient client, string addressSuffix, ILogger logger)
        {
            AddressSuffix = addressSuffix;
            Client = client;
        }
        protected HttpContent CreateJsonObjectContent(TEntity entity)
        {
            var jsonSettings = new JsonSerializerOptions();

            return new StringContent(
                content: JsonSerializer.Serialize(entity),
                encoding: Encoding.UTF8,
                mediaType: MediaTypeNames.Application.Json);
        }

        protected HttpResponseMessage CreateDefaultExceptionResponseMessage(HttpMethod httpMethod, Exception exception, HttpStatusCode statusCode = HttpStatusCode.BadRequest)
        {
            return new HttpResponseMessage(statusCode)
            {
                RequestMessage = new HttpRequestMessage(httpMethod, Client.BaseAddress.AbsoluteUri)
                {
                    Content = new StringContent(exception.Message)
                }
            };
        }
    }
}
