#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using MamboDMA.Games.Common;

namespace MamboDMA.Games.ABI
{
    internal sealed class ABIWebRadarFrameSource : IWebRadarFrameSource
    {
        public string BuildFrameJson()
        {
            bool frameOk = Players.TryGetFrame(out var fr);
            if (!frameOk || fr.Positions == null)
                return "{\"ok\":false}";

            float yawDeg = Players.CtrlYaw;
            ulong sessionId = Players.PersistentLevel;
            float camFov = fr.Cam.Fov;

            List<Players.ABIPlayer> actors;
            lock (Players.Sync)
                actors = Players.ActorList.Count > 0 ? new List<Players.ABIPlayer>(Players.ActorList) : new();

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1 << 15);

            string EscapeJsonString(string str)
            {
                if (string.IsNullOrEmpty(str)) return "";
                var escaped = new StringBuilder(str.Length + 10);
                foreach (char c in str)
                {
                    switch (c)
                    {
                        case '\"': escaped.Append("\\\""); break;
                        case '\\': escaped.Append("\\\\"); break;
                        case '\b': escaped.Append("\\b"); break;
                        case '\f': escaped.Append("\\f"); break;
                        case '\n': escaped.Append("\\n"); break;
                        case '\r': escaped.Append("\\r"); break;
                        case '\t': escaped.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c > 0x7E)
                                escaped.AppendFormat("\\u{0:X4}", (int)c);
                            else
                                escaped.Append(c);
                            break;
                    }
                }
                return escaped.ToString();
            }

            string SafeFloat(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return "0";
                return value.ToString("0.###", inv);
            }

            sb.Append("{\"ok\":true");
            sb.Append(",\"session\":"); sb.Append(sessionId.ToString());
            sb.Append(",\"fov\":"); sb.Append(SafeFloat(camFov));

            sb.Append(",\"self\":{");
            sb.Append("\"x\":"); sb.Append(SafeFloat(fr.Local.X));
            sb.Append(",\"y\":"); sb.Append(SafeFloat(fr.Local.Y));
            sb.Append(",\"z\":"); sb.Append(SafeFloat(fr.Local.Z));
            sb.Append(",\"yaw\":"); sb.Append(SafeFloat(yawDeg));
            sb.Append('}');

            var posMap = new Dictionary<ulong, Players.ActorPos>(fr.Positions.Count);
            for (int i = 0; i < fr.Positions.Count; i++) posMap[fr.Positions[i].Pawn] = fr.Positions[i];

            sb.Append(",\"actors\":[");
            bool first = true;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (!posMap.TryGetValue(a.Pawn, out var ap)) continue;
                if (float.IsNaN(ap.Position.X) || float.IsNaN(ap.Position.Y) || float.IsNaN(ap.Position.Z)) continue;
                if (float.IsInfinity(ap.Position.X) || float.IsInfinity(ap.Position.Y) || float.IsInfinity(ap.Position.Z)) continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append('{');
                sb.Append("\"x\":"); sb.Append(SafeFloat(ap.Position.X));
                sb.Append(",\"y\":"); sb.Append(SafeFloat(ap.Position.Y));
                sb.Append(",\"z\":"); sb.Append(SafeFloat(ap.Position.Z));
                sb.Append(",\"dead\":"); sb.Append(ap.IsDead ? "true" : "false");
                sb.Append(",\"bot\":"); sb.Append(a.IsBot ? "true" : "false");
                sb.Append(",\"pawn\":\"0x"); sb.Append(a.Pawn.ToString("X")); sb.Append('\"');
                sb.Append('}');
            }
            sb.Append(']');

            if (ABILoot.TryGetLoot(out var lootFrame) && lootFrame.Items != null)
            {
                sb.Append(",\"loot\":[");
                first = true;
                foreach (var item in lootFrame.Items)
                {
                    if (item.InContainer) continue;
                    if (float.IsNaN(item.Position.X) || float.IsNaN(item.Position.Y) || float.IsNaN(item.Position.Z)) continue;
                    if (float.IsInfinity(item.Position.X) || float.IsInfinity(item.Position.Y) || float.IsInfinity(item.Position.Z)) continue;

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('{');
                    sb.Append("\"x\":"); sb.Append(SafeFloat(item.Position.X));
                    sb.Append(",\"y\":"); sb.Append(SafeFloat(item.Position.Y));
                    sb.Append(",\"z\":"); sb.Append(SafeFloat(item.Position.Z));

                    string itemName = item.Label ?? item.ClassName ?? "Item";
                    sb.Append(",\"name\":\"");
                    sb.Append(EscapeJsonString(itemName));
                    sb.Append('\"');

                    if (item.ApproxPrice > 0)
                    {
                        sb.Append(",\"price\":");
                        sb.Append(item.ApproxPrice);
                    }
                    if (item.Rarity > 0)
                    {
                        sb.Append(",\"rarity\":");
                        sb.Append(item.Rarity);
                    }
                    sb.Append('}');
                }
                sb.Append(']');

                var containerGroups = new Dictionary<ulong, (Vector3 pos, int count, int totalValue)>();
                foreach (var item in lootFrame.Items)
                {
                    if (!item.InContainer || item.ContainerActor == 0) continue;
                    if (float.IsNaN(item.Position.X) || float.IsNaN(item.Position.Y) || float.IsNaN(item.Position.Z)) continue;

                    if (!containerGroups.TryGetValue(item.ContainerActor, out var existing))
                        containerGroups[item.ContainerActor] = (item.Position, 1, item.ApproxPrice);
                    else
                        containerGroups[item.ContainerActor] = (
                            existing.pos,
                            existing.count + 1,
                            existing.totalValue + item.ApproxPrice);
                }

                sb.Append(",\"containers\":[");
                first = true;
                foreach (var kvp in containerGroups)
                {
                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append('{');
                    sb.Append("\"x\":"); sb.Append(SafeFloat(kvp.Value.pos.X));
                    sb.Append(",\"y\":"); sb.Append(SafeFloat(kvp.Value.pos.Y));
                    sb.Append(",\"z\":"); sb.Append(SafeFloat(kvp.Value.pos.Z));
                    sb.Append(",\"count\":"); sb.Append(kvp.Value.count);
                    if (kvp.Value.totalValue > 0)
                    {
                        sb.Append(",\"totalValue\":");
                        sb.Append(kvp.Value.totalValue);
                    }
                    sb.Append(",\"actor\":\"0x");
                    sb.Append(kvp.Key.ToString("X"));
                    sb.Append('\"');
                    sb.Append('}');
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(",\"loot\":[]");
                sb.Append(",\"containers\":[]");
            }

            sb.Append('}');
            return sb.ToString();
        }

        public IReadOnlyList<WebRadarMapInfo> ListMaps()
        {
            var list = new List<WebRadarMapInfo>();
            string root = MapsRoot;
            if (!Directory.Exists(root)) return list;

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(path)?.ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg")) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                var file = Path.GetFileName(path);
                list.Add(new WebRadarMapInfo(name, file, "/maps/" + Uri.EscapeDataString(file)));
            }

            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return list;
        }

        public string? GetMapSidecarJson(string key)
        {
            try
            {
                string root = MapsRoot;
                string exact = Path.Combine(root, SanitizeFileName(key) + ".json");
                if (File.Exists(exact)) return File.ReadAllText(exact);
                string fallback = Path.Combine(root, "Default.json");
                return File.Exists(fallback) ? File.ReadAllText(fallback) : null;
            }
            catch { return null; }
        }

        public string? GetMapImagePath(string filename)
        {
            try
            {
                string root = MapsRoot;
                string full = Path.GetFullPath(Path.Combine(root, filename));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        public bool TrySaveMapSidecar(string key, string json)
        {
            try
            {
                string root = MapsRoot;
                Directory.CreateDirectory(root);
                string target = Path.Combine(root, SanitizeFileName(key) + ".json");
                File.WriteAllText(target, json, new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        public string GameKey => "abi";

        private static string MapsRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Maps", "ABI");

        private static string SanitizeFileName(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s;
        }
    }
}
