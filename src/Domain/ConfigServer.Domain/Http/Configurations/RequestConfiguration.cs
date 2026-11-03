using System.Collections.Generic;

namespace ConfigServer.Domain.Http.Configurations
{
    public class RequestConfiguration
    {
        public string RequestName { get; set; }
        public string BaseAddress { get; set; }
        public string HttpMethod { get; set; }
        public string RequestSufix { get; set; }
        public PollyConfiguration Polly { get; set; }
        public int TimeoutSeconds { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; } = new Dictionary<string, string>();
    }
}
