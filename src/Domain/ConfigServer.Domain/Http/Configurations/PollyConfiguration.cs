namespace ConfigServer.Domain.Http.Configurations
{
    public class PollyConfiguration
    {
        private string _name;

        public string Name
        {
            get => _name;
            set => _name = string.IsNullOrEmpty(value)
                    ? "DEFAULT_POLLY_NAME"
                    : value;
        }
        public int Retries { get; set; }

        public int IntervalInSeconds { get; set; }
    }
}
