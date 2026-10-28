using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShouTech.Application
{
    public static class Loc
    {
        private static Dictionary<string, string> _map = new();

        public static void Load(string code)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lang", $"{code}.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                }
            }
            catch { _map = new(); }
        }

        public static string T(string key) => _map.TryGetValue(key, out var val) ? val : key;
    }
}