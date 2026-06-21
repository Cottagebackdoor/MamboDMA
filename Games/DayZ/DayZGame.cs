#nullable enable

using System.Diagnostics;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using MamboDMA.Diagnostics;
using MamboDMA.Services;
using Raylib_cs;
using static MamboDMA.Misc;
using static MamboDMA.OverlayWindow;
using static MamboDMA.Games.DayZ.DayZUpdater;

namespace MamboDMA.Games.DayZ
{
    internal static class DayZRenderMetrics
    {
        public static readonly RollingSampleWindow DrawMs = new();
        public static long DrawnBoxes;
        public static long DrawnLabels;
        public static long ProjectionAttempts;
        public static long ProjectionFailures;
        // Latest-value field, not a counter — never reset.
        public static long SnapshotEntities;
        public static long Candidates;
        public static long Frames;
        // volatile: written from render thread, read from producer thread when composing [DayZ/Frame].
        public static volatile int LastOverlayFps;

        private static long _nextRenderLogTicks;

        public static bool ShouldLog(int intervalMs)
        {
            long now = System.Environment.TickCount64;
            long next = Volatile.Read(ref _nextRenderLogTicks);
            if (now < next)
                return false;
            return Interlocked.CompareExchange(
                ref _nextRenderLogTicks,
                now + intervalMs,
                next) == next;
        }
    }

    public sealed class DayZGame : IGame
    {
        public string Name => "DayZ";

        private bool _initialized;
        private bool _running;

        // Change this if your process name differs.
        private const string _dayzExe = "DayZ_x64.exe";

        private static DayZConfig Cfg => Config<DayZConfig>.Settings;

        public void Initialize()
        {
            if (_initialized) return;

            // initialize screen service from current monitor if needed
            if (ScreenService.Current.W <= 0 || ScreenService.Current.H <= 0)
                ScreenService.UpdateFromMonitor(GameSelector.SelectedMonitor);

            _initialized = true;
        }

        public void Start()
        {
            if (_running) return;

            // Safety: only start when attached
            if (!MamboDMA.DmaMemory.IsAttached)
            {
                // Optional: log or tooltip is handled in UI; just bail here.
                return;
            }

            DayZUpdater.Start();
            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;
            DayZUpdater.Stop();
            _running = false;
        }

        public void Tick() { }

        public void Draw(ImGuiWindowFlags winFlags)
        {
            if (UiVisibility.MenusHidden) return;
            long drawStart = Stopwatch.GetTimestamp();
            DayZRenderMetrics.LastOverlayFps = Raylib.GetFPS();
            Interlocked.Increment(ref DayZRenderMetrics.Frames);
            try
            {
            Config<DayZConfig>.DrawConfigPanel(Name, cfg =>
            {
                bool vmmReady = MamboDMA.DmaMemory.IsVmmReady;
                bool attached = MamboDMA.DmaMemory.IsAttached;

                // ───────────────────────────────
                // Quick VMM setup (no process list)
                ImGui.TextDisabled("Quick Setup");
                if (!vmmReady)
                {
                    if (ImGui.Button("Init VMM"))
                    {
                        // Snapshots.VmmReady will update on success
                        VmmService.InitOnly();
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled("← initialize before attaching");
                }
                else if (!attached)
                {
                    if (ImGui.Button($"Attach ({_dayzExe})"))
                    {
                        // Non-blocking attach; Snapshots will update on success
                        VmmService.Attach(_dayzExe);
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled("← attaches without process picker");
                }

                // Status light
                var color = (vmmReady && attached) ? new Vector4(0, 0.8f, 0, 1) : new Vector4(1f, 0.3f, 0.2f, 1);
                DrawStatusInline(color, (vmmReady && attached) ? "Attached & Ready" : "Not attached");

                // If not attached yet, stop here to prevent any crashes
                if (!attached) return;

                var frame = DayZFrameSnapshots.Current;
                var snap = frame.World;
                var cam  = frame.Camera;
                var ents = frame.Entities;
                Volatile.Write(ref DayZRenderMetrics.SnapshotEntities, ents.Length);

                // ESP drawing
                if (cfg.EnableESP && cfg.ShowPlayers)
                    foreach (var p in ents.Where(e => e.Category == EntityType.Player))
                    {
                        Interlocked.Increment(ref DayZRenderMetrics.Candidates);
                        DrawEntityEsp(p, cfg.PlayerColor, cam, snap, cfg);
                    }

                if (cfg.EnableESP && cfg.ShowZombies)
                    foreach (var z in ents.Where(e => e.Category == EntityType.Zombie))
                    {
                        Interlocked.Increment(ref DayZRenderMetrics.Candidates);
                        DrawEntityEsp(z, cfg.ZombieColor, cam, snap, cfg);
                    }

                if (cfg.EnableESP && cfg.ShowLoot)
                    foreach (var item in ents.Where(e =>
                        e.Category == EntityType.Weapon || e.Category == EntityType.Ammo || e.Category == EntityType.Food))
                    {
                        Interlocked.Increment(ref DayZRenderMetrics.Candidates);
                        DrawEntityEsp(item, cfg.ItemColor, cam, snap, cfg);
                    }

                // Options
                ImGui.Separator();
                bool showNames = cfg.ShowNames;
                if (ImGui.Checkbox("Show Names", ref showNames))
                    cfg.ShowNames = showNames;

                bool showDistance = cfg.ShowDistance;
                if (ImGui.Checkbox("Show Distance", ref showDistance))
                    cfg.ShowDistance = showDistance;

                bool showDebugOverlay = cfg.ShowDebugOverlay;
                if (ImGui.Checkbox("Show Debug Overlay", ref showDebugOverlay))
                    cfg.ShowDebugOverlay = showDebugOverlay;
                float debugDistance = cfg.DebugDistance;
                if (ImGui.SliderFloat("Debug Distance", ref debugDistance, 50f, 2000f))
                    cfg.DebugDistance = debugDistance;

                bool showRawDebug = cfg.ShowRawDebug;
                if (ImGui.Checkbox("Show Raw Debug Window", ref showRawDebug))
                    cfg.ShowRawDebug = showRawDebug;

                ImGui.Separator();

                // Start/Stop buttons with safety
                if (!attached) ImGui.BeginDisabled();
                if (ImGui.Button(_running ? "Restart Workers" : "Start Workers"))
                {
                    if (_running) { Stop(); Start(); }
                    else { Start(); }
                }
                if (!attached) ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.Button("Stop Workers")) Stop();
            });

            // ─────────────────────────────
            // Separate Debug Window — only when attached
            // ─────────────────────────────
            if (Cfg.ShowRawDebug && MamboDMA.DmaMemory.IsAttached)
            {
                ImGui.Begin("DayZ Debug", ImGuiWindowFlags.AlwaysAutoResize);

                var frame = DayZFrameSnapshots.Current;
                var snap = frame.World;
                var cam  = frame.Camera;
                var ents = frame.Entities;

                if (ImGui.CollapsingHeader("World / Manager", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"VMM ready: {snap.VmmReady}");
                    ImGui.Text($"DMA attached: {snap.DmaAttached}");
                    ImGui.Text($"World resolved: {snap.Attached}");
                    ImGui.Text($"PID: {snap.ProcessId}");
                    ImGui.Text($"Module base: 0x{snap.ModuleBase:X}");
                    ImGui.Text($"World address: 0x{snap.WorldAddress:X}");
                    ImGui.Text($"World: 0x{snap.World:X}");
                    ImGui.Text($"Network address: 0x{snap.NetworkAddress:X}");
                    ImGui.Text($"Network: 0x{snap.Network:X}");
                    ImGui.Text($"Network manager address: 0x{snap.NetworkManagerAddress:X}");
                    ImGui.Text($"Network manager: 0x{snap.NetworkManager:X}");
                    ImGui.Text($"Camera: 0x{snap.Camera:X}");
                    ImGui.Text($"Local reference (World+0x2960): 0x{snap.LocalPlayerReference:X}");
                    ImGui.Text($"Resolved local-player entity: 0x{snap.LocalPlayer:X}");
                    ImGui.TextWrapped($"Resolution: {snap.LocalPlayerResolution}");
                    ImGui.Text($"PlayerOn raw (+0x2968): 0x{snap.PlayerOn:X}");
                    ImGui.Text($"Local position: {snap.LocalPlayerPosition}");
                }

                if (ImGui.CollapsingHeader("Camera", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if (cam != null)
                    {
                        ImGui.Text($"Pointer: 0x{cam.Pointer:X}");
                        ImGui.Text($"ViewMatrix selector: 0x{cam.ViewMatrixSelector:X}");
                        ImGui.Text($"Viewport: {cam.ViewportSize}");
                        ImGui.Text($"Projection D1: {cam.ProjectionD1}");
                        ImGui.Text($"Projection D2: {cam.ProjectionD2}");
                        ImGui.Text($"ViewTranslation: {cam.InvertedViewTranslation}");
                        ImGui.Text($"Forward: {cam.InvertedViewForward}");
                        ImGui.Text($"Right: {cam.InvertedViewRight}");
                        ImGui.Text($"Up: {cam.InvertedViewUp}");
                    }
                    else
                        ImGui.TextDisabled("Camera not available");
                }

                if (ImGui.CollapsingHeader("Entity Tables", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Near: 0x{snap.NearTable:X} ({snap.NearCount})");
                    ImGui.Text($"Far: 0x{snap.FarTable:X} ({snap.FarCount})");
                    ImGui.Text(
                        $"Slow: 0x{snap.SlowTable:X} " +
                        $"(valid {FormatCandidateCount(snap.SlowCount)} / allocated {snap.SlowAllocatedCount})");
                    ImGui.Text(
                        $"Items: 0x{snap.ItemTable:X} " +
                        $"(valid {FormatCandidateCount(snap.ItemCount)} / allocated {snap.ItemAllocatedCount})");
                }

                if (ImGui.CollapsingHeader("Entities", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if (ImGui.BeginTabBar("EntityTabs"))
                    {
                        DrawEntityCategoryTab("Players", ents.Where(e => e.Category == EntityType.Player));
                        DrawEntityCategoryTab("Zombies", ents.Where(e => e.Category == EntityType.Zombie));
                        DrawEntityCategoryTab("Loot", ents.Where(e =>
                            e.Category == EntityType.Weapon || e.Category == EntityType.Ammo || e.Category == EntityType.Food));
                        DrawEntityCategoryTab("Other", ents.Where(e =>
                            e.Category != EntityType.Player && e.Category != EntityType.Zombie &&
                            e.Category != EntityType.Weapon && e.Category != EntityType.Ammo && e.Category != EntityType.Food));
                        ImGui.EndTabBar();
                    }
                }

                ImGui.End();
            }
            }
            finally
            {
                DayZRenderMetrics.DrawMs.Add(
                    Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds);
                MaybeLogRenderMetrics();
            }
        }

        private static void MaybeLogRenderMetrics()
        {
            if (!DayZRenderMetrics.ShouldLog(5_000))
                return;

            long frames           = Interlocked.Exchange(ref DayZRenderMetrics.Frames, 0);
            long candidates       = Interlocked.Exchange(ref DayZRenderMetrics.Candidates, 0);
            long drawnBoxes       = Interlocked.Exchange(ref DayZRenderMetrics.DrawnBoxes, 0);
            long drawnLabels      = Interlocked.Exchange(ref DayZRenderMetrics.DrawnLabels, 0);
            long projAttempts     = Interlocked.Exchange(ref DayZRenderMetrics.ProjectionAttempts, 0);
            long projFailures     = Interlocked.Exchange(ref DayZRenderMetrics.ProjectionFailures, 0);
            long snapshotEntities = Volatile.Read(ref DayZRenderMetrics.SnapshotEntities);
            var renderMs          = DayZRenderMetrics.DrawMs.SnapshotAndReset();
            int overlayFps        = DayZRenderMetrics.LastOverlayFps;
            double projSuccessRate = projAttempts > 0
                ? (projAttempts - projFailures) * 100.0 / projAttempts
                : 0d;

            Logger.Info(
                $"[DayZ/Render] frames={frames} overlayFps={overlayFps} " +
                $"renderMs={{{renderMs}}} " +
                $"snapshotEntities={snapshotEntities} candidates={candidates} " +
                $"drawnBoxes={drawnBoxes} drawnLabels={drawnLabels} " +
                $"projAttempts={projAttempts} projFailures={projFailures} " +
                $"projSuccessRate={projSuccessRate:F1}%");
        }

        private static void DrawEntityCategoryTab(string label, IEnumerable<Entity> ents)
        {
            if (ImGui.BeginTabItem(label))
            {
                if (ImGui.BeginChild($"{label}_Child", new Vector2(0, 300), ImGuiChildFlags.None))
                {
                    foreach (var e in ents.Take(200))
                    {
                        ImGui.Text(
                            $"{e.SourceTable}[{e.SourceIndex}] 0x{e.Ptr:X} net={e.NetworkId} | " +
                            $"{e.DisplayName} ({e.ConfigName}) " +
                            $"pos=({e.Position.X:F1},{e.Position.Y:F1},{e.Position.Z:F1}) " +
                            $"[{e.Validation}]");
                    }
                }
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
        }

        private static void DrawStatusInline(Vector4 color, string caption)
        {
            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float y = p.Y + ImGui.GetTextLineHeight() * 0.5f;
            dl.AddCircleFilled(new Vector2(p.X + 5, y), 5, ImGui.ColorConvertFloat4ToU32(color));
            ImGui.Dummy(new Vector2(14, ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            ImGui.TextDisabled(caption);
        }

        private static void DrawDebugOverlay(Entity ent, DayZCamera? cam, float maxDist)
        {
            if (cam == null) return;

            if (!WorldToScreenDayZ(cam, ent.Position,
                    new Vector2(ScreenService.Current.W, ScreenService.Current.H), out var screenPos))
                return;

            float dist = Vector3.Distance(cam.InvertedViewTranslation, ent.Position);
            if (dist > maxDist) return;

            var dl = ImGui.GetForegroundDrawList();
            float y = screenPos.Y;

            void DrawLine(string text, uint col)
            {
                dl.AddText(new Vector2(screenPos.X, y), col, text);
                y += ImGui.GetFontSize();
            }

            uint green = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 1, 0, 1));
            DrawLine($"TypeName: {ent.TypeName}", green);
            DrawLine($"ConfigName: {ent.ConfigName}", green);
            DrawLine($"CleanName: {ent.CleanName}", green);
            DrawLine($"ModelName: {ent.ModelName}", green);
            DrawLine($"Pos: {ent.Position.X:F1}, {ent.Position.Y:F1}, {ent.Position.Z:F1}", green);
        }

        private static void DrawEntityEsp(
            Entity entity,
            Vector4 color,
            DayZUpdater.DayZCamera? cam,
            DayZSnapshot snapshot,
            DayZConfig cfg,
            float size = 20f)
        {
            if (snapshot.LocalPlayer != 0 && entity.Ptr == snapshot.LocalPlayer)
                return;

            Vector3 localPosition = snapshot.LocalPlayerPosition;
            bool hasDistance = IsFinitePosition(localPosition);
            float distance = hasDistance
                ? Vector3.Distance(localPosition, entity.Position)
                : 0f;
            hasDistance = hasDistance && float.IsFinite(distance);

            if (hasDistance && distance > cfg.MaxDrawDistance)
                return;

            Interlocked.Increment(ref DayZRenderMetrics.ProjectionAttempts);
            if (!DayZUpdater.WorldToScreenDayZ(cam, entity.Position,
                    new Vector2(ScreenService.Current.W, ScreenService.Current.H), out var screenPos))
            {
                Interlocked.Increment(ref DayZRenderMetrics.ProjectionFailures);
                return;
            }

            var dl = ImGui.GetForegroundDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(color);

            float half = size * 0.5f;
            var min = new Vector2(screenPos.X - half, screenPos.Y - half);
            var max = new Vector2(screenPos.X + half, screenPos.Y + half);

            dl.AddRect(min, max, col, 0f, ImDrawFlags.None, 2f);
            Interlocked.Increment(ref DayZRenderMetrics.DrawnBoxes);

            string name = !string.IsNullOrWhiteSpace(entity.DisplayName)
                ? entity.DisplayName
                : entity.Category.ToString();

            string label = (cfg.ShowNames, cfg.ShowDistance && hasDistance) switch
            {
                (true, true) => $"{name} [{distance:F0}m]",
                (true, false) => name,
                (false, true) => $"{distance:F0}m",
                _ => string.Empty
            };

            if (label.Length == 0)
                return;

            Vector2 textSize = ImGui.CalcTextSize(label);
            var textPosition = new Vector2(
                screenPos.X - (textSize.X * 0.5f),
                min.Y - textSize.Y - 3f);

            uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));
            dl.AddText(textPosition + Vector2.One, shadow, label);
            dl.AddText(textPosition, col, label);
            Interlocked.Increment(ref DayZRenderMetrics.DrawnLabels);
        }

        private static string FormatCandidateCount(int count)
            => count >= 0 ? count.ToString() : "unknown";

        // Vector3.Zero means the local-player position has not resolved yet, so it
        // must not be used for distance filtering even though its components are finite.
        private static bool IsFinitePosition(Vector3 position)
            => float.IsFinite(position.X) &&
               float.IsFinite(position.Y) &&
               float.IsFinite(position.Z) &&
               position != Vector3.Zero;
    }

    public sealed class DayZConfig
    {
        public bool EnableESP { get; set; } = true;
        public float MaxDrawDistance { get; set; } = 1000f;
        public Vector4 PlayerColor { get; set; } = new(0f, 1f, 0f, 1f);
        public Vector4 ZombieColor { get; set; } = new(1f, 0f, 0f, 1f);
        public Vector4 CarColor { get; set; } = new(0f, 0.6f, 1f, 1f);
        public bool ShowPlayers { get; set; } = true;
        public bool ShowZombies { get; set; } = true;
        public bool ShowLoot { get; set; } = true;
        public bool ShowNames { get; set; } = true;
        public bool ShowDistance { get; set; } = true;
        public Vector4 ItemColor { get; set; } = new(1f, 1f, 0f, 1f);

        public bool ShowDebugOverlay { get; set; } = false;
        public float DebugDistance { get; set; } = 200f;

        public bool ShowRawDebug { get; set; } = false;
    }
}
