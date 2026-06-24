#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MamboDMA.Games.Common;
using MamboDMA.Games.DayZ.Radar;

namespace MamboDMA.Games.DayZ
{
    internal sealed class DayZWebRadarFrameSource : IWebRadarFrameSource
    {
        public string BuildFrameJson()
        {
            var frame = DayZFrameSnapshots.Current;
            var world = frame.World;
            var cam = frame.Camera;
            var ents = frame.Entities;

            if (!world.Attached)
                return "{\"ok\":false}";

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1 << 14);

            string SafeFloat(float v)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
                return v.ToString("0.###", inv);
            }

            float selfYawDeg = 0f;
            if (cam != null)
            {
                var f = cam.InvertedViewForward;
                if (f.LengthSquared() >= 1e-6f)
                    selfYawDeg = (float)(Math.Atan2(f.X, f.Z) * 180.0 / Math.PI);
            }

            var lp = world.LocalPlayerPosition;

            sb.Append("{\"ok\":true");
            // Colors driven server-side by DayZ Settings so in-app picker is single source of truth.
            sb.Append(",\"colors\":{\"zombie\":\"");
            sb.Append(Vec4ToHex(Config<DayZConfig>.Settings.ZombieColor));
            sb.Append("\"}");
            // Browser auto-switches its map to follow this; omitted when empty so absent == "no preference" for ABI parity.
            string selectedMap = Config<DayZConfig>.Settings.RadarSelectedMap;
            if (!string.IsNullOrEmpty(selectedMap))
            {
                sb.Append(",\"selectedMap\":\"");
                sb.Append(EscapeJsonString(selectedMap));
                sb.Append('\"');
            }
            // DayZ engine is X-east, Y-altitude, Z-north; frontend treats JSON x/y as top-down axes and z as altitude. Swap Y<->Z. Multiply by 100 because frontend inScale is cm/px.
            sb.Append(",\"self\":{");
            sb.Append("\"x\":"); sb.Append(SafeFloat(lp.X * 100f));
            sb.Append(",\"y\":"); sb.Append(SafeFloat(lp.Z * 100f));
            sb.Append(",\"z\":"); sb.Append(SafeFloat(lp.Y * 100f));
            sb.Append(",\"yaw\":"); sb.Append(SafeFloat(selfYawDeg));
            sb.Append('}');

            sb.Append(",\"actors\":[");
            bool first = true;
            foreach (var e in ents)
            {
                if (e.Category != DayZUpdater.EntityType.Player && e.Category != DayZUpdater.EntityType.Zombie)
                    continue;
                if (world.LocalPlayer != 0 && e.Ptr == world.LocalPlayer) continue;
                if (!IsFinite(e.Position)) continue;

                if (!first) sb.Append(',');
                first = false;

                bool isZombie = e.Category == DayZUpdater.EntityType.Zombie;
                sb.Append('{');
                sb.Append("\"x\":"); sb.Append(SafeFloat(e.Position.X * 100f));
                sb.Append(",\"y\":"); sb.Append(SafeFloat(e.Position.Z * 100f));
                sb.Append(",\"z\":"); sb.Append(SafeFloat(e.Position.Y * 100f));
                sb.Append(",\"dead\":"); sb.Append(e.IsDead ? "true" : "false");
                sb.Append(",\"pawn\":\"0x"); sb.Append(e.Ptr.ToString("X")); sb.Append('\"');
                sb.Append(",\"kind\":\""); sb.Append(isZombie ? "zombie" : "player"); sb.Append('\"');
                if (e.HasForward && e.Forward.LengthSquared() >= 1e-6f)
                {
                    float yaw = (float)(Math.Atan2(e.Forward.X, e.Forward.Z) * 180.0 / Math.PI);
                    sb.Append(",\"yaw\":"); sb.Append(SafeFloat(yaw));
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"loot\":[");
            first = true;
            foreach (var e in ents)
            {
                if (e.Category != DayZUpdater.EntityType.Weapon &&
                    e.Category != DayZUpdater.EntityType.Ammo &&
                    e.Category != DayZUpdater.EntityType.Food)
                    continue;
                if (!IsFinite(e.Position)) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append('{');
                sb.Append("\"x\":"); sb.Append(SafeFloat(e.Position.X * 100f));
                sb.Append(",\"y\":"); sb.Append(SafeFloat(e.Position.Z * 100f));
                sb.Append(",\"z\":"); sb.Append(SafeFloat(e.Position.Y * 100f));
                sb.Append(",\"name\":\""); sb.Append(EscapeJsonString(e.DisplayName)); sb.Append('\"');
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"containers\":[");
            first = true;
            foreach (var e in ents)
            {
                if (e.Category != DayZUpdater.EntityType.Car && e.Category != DayZUpdater.EntityType.Boat)
                    continue;
                if (!IsFinite(e.Position)) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append('{');
                sb.Append("\"x\":"); sb.Append(SafeFloat(e.Position.X * 100f));
                sb.Append(",\"y\":"); sb.Append(SafeFloat(e.Position.Z * 100f));
                sb.Append(",\"z\":"); sb.Append(SafeFloat(e.Position.Y * 100f));
                sb.Append(",\"kind\":\"vehicle\"");
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        public IReadOnlyList<WebRadarMapInfo> ListMaps()
        {
            var maps = DayZMapRegistry.All;
            var list = new List<WebRadarMapInfo>(maps.Count);
            foreach (var m in maps)
                list.Add(new WebRadarMapInfo(m.Key, m.ImageFile, "/maps/" + Uri.EscapeDataString(m.ImageFile)));
            return list;
        }

        public string? GetMapSidecarJson(string key)
        {
            var def = DayZMapRegistry.FindByKey(key);
            if (def == null) return null;
            double inScale = def.ImageSize > 0 ? (def.WorldSize * 100.0) / def.ImageSize : 100.0;
            var payload = new
            {
                file = def.ImageFile,
                inScale,
                yFlip = def.FlipZ,
                worldOffsetCm = new { x = 0, y = 0 },
                zoom = 0.5,
            };
            return JsonSerializer.Serialize(payload);
        }

        public string? GetMapImagePath(string filename)
        {
            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Maps", "DayZ");
                string full = Path.GetFullPath(Path.Combine(root, filename));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        public bool TrySaveMapSidecar(string key, string json) => false;

        public string GameKey => "dayz";

        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static string Vec4ToHex(Vector4 c)
        {
            int r = (int)Math.Clamp(c.X * 255f, 0, 255);
            int g = (int)Math.Clamp(c.Y * 255f, 0, 255);
            int b = (int)Math.Clamp(c.Z * 255f, 0, 255);
            return $"#{r:x2}{g:x2}{b:x2}";
        }

        private static string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            var sb = new StringBuilder(str.Length + 8);
            foreach (char c in str)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
