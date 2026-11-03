using System;
using System.Net;
using System.Net.Http;
using ConfigServer.Domain.Http.Abstractions;
using ConfigServer.Domain.Http.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Domain.Http.Implementations
{
    internal class HttpRequestGet : BaseHttpRequestMethod<string>,
        IHttpMethod<string>
    {
        public HttpRequestGet(HttpClient client, string addressSuffix, ILogger logger) : base(client, addressSuffix,logger)
        {
        }

        public HttpResponseMessage Execute(string parameters)
        {
            try
            {
                var result = Client.GetAsync($"{AddressSuffix}/{parameters}").Result;
                return result;
            }
            catch (Exception ex)
            {

                return CreateDefaultExceptionResponseMessage(
                    httpMethod: HttpMethod.Post,
                    exception: ex,
                    statusCode: HttpStatusCode.BadGateway);
            }
        }
    }
}
