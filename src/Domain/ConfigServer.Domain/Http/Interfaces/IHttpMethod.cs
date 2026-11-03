using System.Net.Http;

namespace ConfigServer.Domain.Http.Interfaces
{
    public interface IHttpMethod<in TParameters>
    {
        HttpResponseMessage Execute(TParameters parameters);
    }
}
