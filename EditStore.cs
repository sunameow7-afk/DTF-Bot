using System.Text.Json;

namespace DTFBot
{
    // ============ Persists per-command message edits (edits.json) ============
    public static class EditStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, Dictionary<string, string>> _data = new Dictionary<string, Dictionary<string, string>>();

        private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "edits.json");

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                var j = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path));
                if (j != null) _data = j;
            }
            catch { }
        }

        public static string Get(string cmd, string part)
        {
            lock (_lock)
            {
                return _data.TryGetValue(cmd, out var d) && d.TryGetValue(part, out var v) ? v : null;
            }
        }

        public static void Set(string cmd, string part, string value)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (_data.TryGetValue(cmd, out var d))
                    {
                        d.Remove(part);
                        if (d.Count == 0) _data.Remove(cmd);
                    }
                }
                else
                {
                    if (!_data.TryGetValue(cmd, out var d))
                    {
                        d = new Dictionary<string, string>();
                        _data[cmd] = d;
                    }
                    d[part] = value.Trim();
                }
                try { File.WriteAllText(Path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true })); }
                catch { }
            }
        }
    }
}
