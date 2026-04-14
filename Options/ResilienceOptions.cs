namespace Gateway.Options
{
    public class ResilienceOptions
    {
        public bool RetryEnabled { get; set; } = false;
        public int MaxRetryAttempts { get; set; } = 2;
        public int BaseDelayMs { get; set; } = 200;
    }
}
