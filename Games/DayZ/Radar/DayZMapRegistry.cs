#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace MamboDMA.Games.DayZ.Radar
{
    public sealed record MapDef(
        string Key,
        string Display,
        string ImageFile,
        int ImageSize,
        int WorldSize,
        bool FlipZ);

    public static class DayZMapRegistry
    {
        public static Action<string>? Log;

        private static readonly string _manifestPath =
            Path.Combine(AppContext.BaseDirectory, "Assets", "Maps", "DayZ", "maps.json");

        private static readonly List<MapDef> _all = new();
        private static readonly Dictionary<string, MapDef> _byKey =
            new(StringComparer.OrdinalIgnoreCase);
        // Memory-name -> manifest-key for vanilla maps whose internal id differs from the manifest entry.
        private static readonly Dictionary<string, string> _aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["enoch"] = "Livonia",
            };
        private static bool _loaded;

        public static IReadOnlyList<MapDef> All { get { EnsureLoaded(); return _all; } }

        public static MapDef? FindByKey(string key)
        {
            EnsureLoaded();
            return _byKey.TryGetValue(key, out var m) ? m : null;
        }

        public static MapDef? ResolveByWorldName(string? worldName)
        {
            if (string.IsNullOrWhiteSpace(worldName)) return null;
            EnsureLoaded();
            var key = _aliases.TryGetValue(worldName, out var alias) ? alias : worldName;
            return _all.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        public static Vector2 WorldToImagePx(Vector3 world, MapDef map)
        {
            float scale = (float)map.ImageSize / map.WorldSize;
            float x = world.X * scale;
            float y = map.FlipZ ? (map.WorldSize - world.Z) * scale : world.Z * scale;
            return new Vector2(x, y);
        }

        public static Vector3 ImagePxToWorld(Vector2 px, MapDef map)
        {
            float scale = (float)map.WorldSize / map.ImageSize;
            float x = px.X * scale;
            float z = map.FlipZ ? map.WorldSize - px.Y * scale : px.Y * scale;
            return new Vector3(x, 0f, z);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_manifestPath))
                {
                    Log?.Invoke($"[DayZ/Radar] maps.json not found at {_manifestPath}");
                    return;
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(_manifestPath));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    var v = entry.Value;
                    var def = new MapDef(
                        Key: entry.Name,
                        Display: v.GetProperty("display").GetString() ?? entry.Name,
                        ImageFile: v.GetProperty("image").GetString() ?? string.Empty,
                        ImageSize: v.GetProperty("imageSize").GetInt32(),
                        WorldSize: v.GetProperty("worldSize").GetInt32(),
                        FlipZ: v.GetProperty("flipZ").GetBoolean());
                    _all.Add(def);
                    _byKey[def.Key] = def;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[DayZ/Radar] maps.json load failed (path={_manifestPath}): {ex}");
            }
        }
    }
}
