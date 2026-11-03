using System;
using System.Net;
using System.Net.Http;
using ConfigServer.Domain.Http.Abstractions;
using ConfigServer.Domain.Http.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Domain.Http.Implementations
{
    internal class HttpRequestPost<TEntity> :
        BaseHttpRequestMethod<TEntity>,
        IHttpMethod<TEntity>
    {

        public HttpRequestPost(HttpClient client, string addressSuffix, ILogger logger) : base(client, addressSuffix,logger)
        {
        }
        public HttpResponseMessage Execute(TEntity parameters)
        {
            try
            {
                var result = Client.PostAsync(AddressSuffix, CreateJsonObjectContent(parameters)).Result;
                return result;
            }
            catch (Exception ex)
            {

                return CreateDefaultExceptionResponseMessage(
                    httpMethod: HttpMethod.Post,
                    exception: ex,
                    statusCode: HttpStatusCode.BadRequest);
            }
        }


    }
}
