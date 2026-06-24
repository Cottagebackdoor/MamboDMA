#nullable enable

using System;
using System.Numerics;
using ImGuiNET;
using MamboDMA.Games.DayZ;
using Raylib_cs;

namespace MamboDMA.Games.DayZ.Radar
{
    public static class DayZRadarWindow
    {
        private static Vector2 _lastPan;
        private static bool _seeded;
        private static string _lastMapKey = string.Empty;

        public static void Reset()
        {
            _lastPan = Vector2.Zero;
            _seeded = false;
            _lastMapKey = string.Empty;
        }

        public static void Draw()
        {
            var cfg = Config<DayZConfig>.Settings;
            if (!cfg.EnableMiniRadar) return;
            if (DayZMapRegistry.All.Count == 0) return;

            var map = DayZMapRegistry.FindByKey(cfg.RadarSelectedMap)
                      ?? DayZMapRegistry.All[0];
            if (!DayZMapTextures.TryGetOrLoad(map, out var tex)) return;

            // Switching maps invalidates pan seed and evicts the previous texture.
            if (!string.Equals(_lastMapKey, map.Key, StringComparison.OrdinalIgnoreCase))
            {
                _seeded = false;
                DayZMapTextures.UnloadAllExcept(map.Key);
                _lastMapKey = map.Key;
            }

            ImGui.SetNextWindowSize(new Vector2(360, 380), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("DayZ Mini Radar"))
            {
                ImGui.End();
                return;
            }

            var frame = DayZFrameSnapshots.Current;
            var snap = frame.World;
            var cam = frame.Camera;
            var ents = frame.Entities;

            Vector2 winMin = ImGui.GetCursorScreenPos();
            Vector2 avail = ImGui.GetContentRegionAvail();
            if (avail.X < 16f || avail.Y < 16f) { ImGui.End(); return; }
            Vector2 winMax = winMin + avail;
            Vector2 winCenter = (winMin + winMax) * 0.5f;

            float zoom = Math.Clamp(cfg.RadarZoom, 0.25f, 48f);
            float pxScale = MathF.Min(avail.X, avail.Y) / map.WorldSize * zoom;

            Vector2 selfPx = DayZMapRegistry.WorldToImagePx(snap.LocalPlayerPosition, map);
            bool hasSelf = snap.LocalPlayerPosition != Vector3.Zero;

            // Seed BEFORE input/lerp so first frame snaps instead of lerping from (0,0).
            if (!_seeded)
            {
                if (cfg.RadarCenterOnSelf && hasSelf)
                    _lastPan = selfPx;
                else
                    _lastPan = new Vector2(map.ImageSize * 0.5f, map.ImageSize * 0.5f);
                _seeded = true;
            }

            HandleInput(cfg, winMin, winMax, pxScale);

            Vector2 target = (cfg.RadarCenterOnSelf && hasSelf) ? selfPx : _lastPan;
            float s = Math.Clamp(cfg.RadarFollowSmoothing, 0f, 1f);
            _lastPan = _lastPan + (target - _lastPan) * s;

            Vector2 ImgToScreen(Vector2 imgPx) =>
                winCenter + (imgPx - _lastPan) * pxScale;

            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(winMin, winMax, true);

            // Map blit.
            Vector2 mapMin = ImgToScreen(Vector2.Zero);
            Vector2 mapMax = ImgToScreen(new Vector2(map.ImageSize, map.ImageSize));
            dl.AddImage((IntPtr)tex.Id, mapMin, mapMax);

            // Entity dots + aimlines.
            foreach (var e in ents)
            {
                if (snap.LocalPlayer != 0 && e.Ptr == snap.LocalPlayer) continue;

                Vector4? colorOpt = e.Category switch
                {
                    DayZUpdater.EntityType.Player => cfg.ShowPlayers ? cfg.PlayerColor : null,
                    DayZUpdater.EntityType.Zombie => cfg.ShowZombies ? cfg.ZombieColor : null,
                    DayZUpdater.EntityType.Weapon or DayZUpdater.EntityType.Ammo or DayZUpdater.EntityType.Food
                        => cfg.ShowLoot ? cfg.ItemColor : null,
                    DayZUpdater.EntityType.Car or DayZUpdater.EntityType.Boat
                        => cfg.ShowLoot ? cfg.CarColor : null,
                    _ => null,
                };
                if (colorOpt is null) continue;

                uint col = ImGui.ColorConvertFloat4ToU32(colorOpt.Value);
                Vector2 entScreen = ImgToScreen(DayZMapRegistry.WorldToImagePx(e.Position, map));
                dl.AddCircleFilled(entScreen, 3.5f, col);

                bool isHuman = e.Category == DayZUpdater.EntityType.Player
                            || e.Category == DayZUpdater.EntityType.Zombie;
                // Gate before Normalize so a zero forward doesn't NaN the endpoint.
                if (cfg.ShowAimlines && isHuman && e.HasForward
                    && e.Forward.LengthSquared() >= 1e-6f)
                {
                    Vector3 endWorld = e.Position + Vector3.Normalize(e.Forward) * cfg.AimlineLength;
                    Vector2 endScreen = ImgToScreen(DayZMapRegistry.WorldToImagePx(endWorld, map));
                    dl.AddLine(entScreen, endScreen, ImGui.ColorConvertFloat4ToU32(cfg.AimlineColor), 1.5f);
                }
            }

            // Self triangle.
            if (hasSelf)
            {
                Vector2 self = ImgToScreen(selfPx);
                float yawDeg = 0f;
                if (cam != null && cam.IsValid)
                {
                    var f = cam.InvertedViewForward;
                    yawDeg = MathF.Atan2(f.X, f.Z) * 180f / MathF.PI;
                }
                DrawSelfTriangle(dl, self, yawDeg);
            }

            dl.PopClipRect();
            ImGui.End();
        }

        private static void HandleInput(DayZConfig cfg, Vector2 winMin, Vector2 winMax, float pxScale)
        {
            var io = ImGui.GetIO();
            Vector2 mp = io.MousePos;
            bool hovered = ImGui.IsWindowHovered()
                        && mp.X >= winMin.X && mp.X <= winMax.X
                        && mp.Y >= winMin.Y && mp.Y <= winMax.Y;
            if (!hovered) return;

            if (io.MouseWheel != 0f)
            {
                float next = cfg.RadarZoom * MathF.Pow(1.15f, io.MouseWheel);
                cfg.RadarZoom = Math.Clamp(next, 0.25f, 48f);
            }

            // Right-drag = free pan; switches CenterOnSelf off so target stops chasing.
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f))
            {
                if (cfg.RadarCenterOnSelf) cfg.RadarCenterOnSelf = false;
                Vector2 delta = io.MouseDelta;
                if (pxScale > 1e-6f)
                    _lastPan -= delta / pxScale;
            }
        }

        private static void DrawSelfTriangle(ImDrawListPtr dl, Vector2 center, float yawDeg)
        {
            float rad = yawDeg * MathF.PI / 180f;
            float c = MathF.Cos(rad), s = MathF.Sin(rad);
            Vector2 Rot(Vector2 v) => new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
            Vector2 tip = center + Rot(new Vector2(0f, -9f));
            Vector2 bl  = center + Rot(new Vector2(-6f, 5f));
            Vector2 br  = center + Rot(new Vector2( 6f, 5f));
            uint fill   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
            uint border = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
            dl.AddTriangleFilled(tip, bl, br, fill);
            dl.AddTriangle(tip, bl, br, border, 1.5f);
        }
    }
}
