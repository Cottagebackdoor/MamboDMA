#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Raylib_cs;

namespace MamboDMA.Games.DayZ.Radar
{
    public static class DayZMapTextures
    {
        public static Action<string>? Log;

        private static readonly Dictionary<string, Texture2D> _loaded =
            new(StringComparer.OrdinalIgnoreCase);
        // Persists across UnloadAll so we don't re-spam load failures on every Start.
        private static readonly HashSet<string> _failed =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGetOrLoad(MapDef map, out Texture2D tex)
        {
            if (_loaded.TryGetValue(map.Key, out tex)) return true;
            if (_failed.Contains(map.Key)) { tex = default; return false; }

            string path = Path.Combine(
                AppContext.BaseDirectory, "Assets", "Maps", "DayZ", map.ImageFile);
            if (!File.Exists(path))
            {
                _failed.Add(map.Key);
                Log?.Invoke($"[DayZ/Radar] map png missing key={map.Key} path={path}");
                tex = default;
                return false;
            }
            try
            {
                var loaded = Raylib.LoadTexture(path);
                if (loaded.Id == 0)
                {
                    _failed.Add(map.Key);
                    Log?.Invoke($"[DayZ/Radar] LoadTexture returned id=0 key={map.Key} path={path}");
                    tex = default;
                    return false;
                }
                _loaded[map.Key] = loaded;
                tex = loaded;
                return true;
            }
            catch (Exception ex)
            {
                _failed.Add(map.Key);
                Log?.Invoke($"[DayZ/Radar] LoadTexture threw key={map.Key} path={path}: {ex.Message}");
                tex = default;
                return false;
            }
        }

        public static void UnloadAllExcept(string keepKey)
        {
            var stale = new List<string>();
            foreach (var kv in _loaded)
                if (!string.Equals(kv.Key, keepKey, StringComparison.OrdinalIgnoreCase))
                    stale.Add(kv.Key);
            foreach (var k in stale)
            {
                try { Raylib.UnloadTexture(_loaded[k]); } catch { }
                _loaded.Remove(k);
            }
        }

        public static void UnloadAll()
        {
            foreach (var t in _loaded.Values)
                try { Raylib.UnloadTexture(t); } catch { }
            _loaded.Clear();
        }
    }
}
