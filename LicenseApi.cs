using System.Text;
using System.Text.Json;

namespace DTFBot
{
    // ============ Thin HTTP client for the DTF license server admin API ============
    public static class LicenseApi
    {
        private static readonly HttpClient _http = new HttpClient();

        private static HttpRequestMessage Req(HttpMethod m, string path, object body = null)
        {
            var r = new HttpRequestMessage(m, Cfg.LicenseServer.TrimEnd('/') + path);
            r.Headers.Add("X-Admin-Pw", Cfg.AdminPassword);
            if (body != null)
            {
                r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            return r;
        }

        public static async Task<JsonElement> GetAsync(string path)
        {
            using var r = await _http.SendAsync(Req(HttpMethod.Get, path));
            var txt = await r.Content.ReadAsStringAsync();
            return JsonDocument.Parse(txt).RootElement.Clone();
        }

        public static async Task<JsonElement> PostAsync(string path, object body)
        {
            using var r = await _http.SendAsync(Req(HttpMethod.Post, path, body));
            var txt = await r.Content.ReadAsStringAsync();
            try { return JsonDocument.Parse(txt).RootElement.Clone(); }
            catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
        }

        public static async Task<bool> DeleteAsync(string path)
        {
            using var r = await _http.SendAsync(Req(HttpMethod.Delete, path));
            return r.IsSuccessStatusCode;
        }

        // ---- high level ops ----

        public static async Task<(bool ok, string key, string error)> CreateKey(string clientName, int days, string hwid = null, string note = null)
        {
            var body = new { plan = "custom", days, prefix = "DTF", qty = 1, discord = clientName, hwid = hwid ?? "", note = note ?? "" };
            var j = await PostAsync("/admin/create", body);
            if (j.TryGetProperty("keys", out var keys) && keys.GetArrayLength() > 0)
                return (true, keys[0].GetString(), null);
            return (false, null, j.TryGetProperty("error", out var e) ? e.GetString() : "unknown error");
        }

        public static async Task<(JsonElement keys, JsonElement logs, JsonElement requests)> Snapshot()
        {
            var j = await GetAsync("/admin/list");
            return (j.GetProperty("keys"), j.TryGetProperty("logs", out var l) ? l : default, j.TryGetProperty("requests", out var rq) ? rq : default);
        }

        public static Task<JsonElement> Ban(string key) => PostAsync("/admin/ban", new { key });
        public static Task<JsonElement> Unban(string key) => PostAsync("/admin/unban", new { key });
        public static Task<JsonElement> ResetHwid(string key) => PostAsync("/admin/reset", new { key });
        public static Task<bool> DeleteKey(string key) => DeleteAsync("/admin/delete/" + key);
    }
}
