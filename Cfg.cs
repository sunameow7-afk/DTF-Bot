using System.Text.Json;

namespace DTFBot
{
    // ============ Config: env vars or config.json ============
    public static class Cfg
    {
        public static string BotToken = Environment.GetEnvironmentVariable("DTF_BOT_TOKEN") ?? "";
        public static ulong GuildId = ulong.TryParse(Environment.GetEnvironmentVariable("DTF_GUILD_ID"), out var g) ? g : 0;
        public static ulong SecurityChannelId = ulong.TryParse(Environment.GetEnvironmentVariable("DTF_SECURITY_CHANNEL_ID"), out var c) ? c : 0;
        public static ulong AdminRoleId = ulong.TryParse(Environment.GetEnvironmentVariable("DTF_ADMIN_ROLE_ID"), out var r) ? r : 0;
        public static string LicenseServer = Environment.GetEnvironmentVariable("DTF_LICENSE_SERVER") ?? "https://dtf-license.onrender.com";
        public static string AdminPassword = Environment.GetEnvironmentVariable("DTF_ADMIN_PASSWORD") ?? "Spring$24";

        private const string ConfigFile = "config.json";

        public static void LoadFile()
        {
            try
            {
                if (!File.Exists(ConfigFile)) return;
                var j = JsonDocument.Parse(File.ReadAllText(ConfigFile)).RootElement;
                BotToken = Get(j, "token") ?? BotToken;
                GuildId = Get(j, "guildId") is string s1 && ulong.TryParse(s1, out var g1) ? g1 : GuildId;
                SecurityChannelId = Get(j, "securityChannelId") is string s2 && ulong.TryParse(s2, out var c2) ? c2 : SecurityChannelId;
                AdminRoleId = Get(j, "adminRoleId") is string s3 && ulong.TryParse(s3, out var r3) ? r3 : AdminRoleId;
                LicenseServer = Get(j, "licenseServer") ?? LicenseServer;
                AdminPassword = Get(j, "adminPassword") ?? AdminPassword;
            }
            catch { }
        }

        private static string Get(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
