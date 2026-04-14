namespace Gateway.Models
{
    public class JwtSettings
    {
        public AccessTokenSettings AccessTokenSettings { get; set; } = new();
    }

    public class AccessTokenSettings
    {
        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
    }
}
