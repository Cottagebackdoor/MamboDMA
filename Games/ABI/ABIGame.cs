// File: Games/ABI/ABIGame.cs
// DIAGNOSTIC BUILD
// - Uses ABILoot diagnostics and shows rich debug columns in loot tables
// - Computes zoom once per frame
// - Loot ESP consumes stable world positions from ABILoot

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using ImGuiNET;
using MamboDMA.Services;
using static MamboDMA.OverlayWindow;
using static MamboDMA.Misc;
using MamboDMA.Games;
using MamboDMA.Input;
using MamboDMA.WebRadar;
using System.Runtime.InteropServices;

namespace MamboDMA.Games.ABI
{
    public sealed class ABIGame : IGame
    {
        public string Name => "ArenaBreakoutInfinite";
        private bool _initialized, _running;

        private static readonly ABIWebRadarFrameSource _webRadarSource = new();

        private static ABIGameConfig Cfg => Config<ABIGameConfig>.Settings;

        private static int _selectedInputIndex = -1;
        private static List<Device.SerialDeviceInfo> _inputDevices = new();
        private static bool _inputInitialized = false;

        private static readonly string[] _boneNames = new[]
        {
            "Pelvis","Spine_01","Spine_02","Spine_03","Neck","Head",
            "Clavicle_L","UpperArm_L","LowerArm_L","Hand_L",
            "Clavicle_R","UpperArm_R","LowerArm_R","Hand_R",
            "Thigh_L","Calf_L","Foot_L","Thigh_R","Calf_R","Foot_R"
        };

        private static readonly int[] _boneIndices = new[]
        {
            Skeleton.IDX_Pelvis, Skeleton.IDX_Spine_01, Skeleton.IDX_Spine_02, Skeleton.IDX_Spine_03, Skeleton.IDX_Neck, Skeleton.IDX_Head,
            Skeleton.IDX_Clavicle_L, Skeleton.IDX_UpperArm_L, Skeleton.IDX_LowerArm_L, Skeleton.IDX_Hand_L,
            Skeleton.IDX_Clavicle_R, Skeleton.IDX_UpperArm_R, Skeleton.IDX_LowerArm_R, Skeleton.IDX_Hand_R,
            Skeleton.IDX_Thigh_L, Skeleton.IDX_Calf_L, Skeleton.IDX_Foot_L, Skeleton.IDX_Thigh_R, Skeleton.IDX_Calf_R, Skeleton.IDX_Foot_R
        };

        private static readonly int[] _randomPoolDefault = new[]
        {
            Skeleton.IDX_Head, Skeleton.IDX_Neck, Skeleton.IDX_Spine_03,
            Skeleton.IDX_UpperArm_R, Skeleton.IDX_UpperArm_L,
            Skeleton.IDX_Thigh_R, Skeleton.IDX_Thigh_L
        };

        public enum AimbotTargetMode : int
        {
            ClosestWorldDistanceInFov = 0,
            ClosestToCrosshairInFov   = 1,
        }
        private static readonly System.Random _rng = new();

        private static Vector2? _lastAimScreen;
        private static long     _lastAimStampTicks;
        private static uint U32(Vector4 col) => ImGui.ColorConvertFloat4ToU32(col);
        private static long NowTicks() => Stopwatch.GetTimestamp();
        private static double TicksToMs(long dtTicks) => dtTicks * 1000.0 / Stopwatch.Frequency;

        private static readonly string[] AbiActions =
        {
            "ABI_ToggleThreads",
            "ABI_StartThreads",
            "ABI_StopThreads",
            "ABI_ToggleBoxes",
            "ABI_ToggleNames",
            "ABI_ToggleDistance",
            "ABI_ToggleSkeletons",
            "ABI_ToggleDebug",
            "ABI_ToggleWebRadar",
            "ABI_Attach",
            "ABI_DisposeVmm",
        };

        public void Initialize()
        {
            if (_initialized) return;

            if (ScreenService.Current.W <= 0 || ScreenService.Current.H <= 0)
                ScreenService.UpdateFromMonitor(GameSelector.SelectedMonitor);

            Keybinds.RegisterCategory("ABI", AbiActions, HandleAbiAction);
            EnsureAbiKeybindProfile();

            _initialized = true;
        }

        public void Attach() => VmmService.Attach(Cfg.AbiExe ?? "UAGame.exe");

        public void Dispose()
        {
            Stop();
            DmaMemory.Dispose();
        }

        public void Start()
        {
            if (_running || !DmaMemory.IsAttached) return;
            _running = true;

            TimerResolution.Enable1ms();
            Players.StartCache();
            ABILoot.Start();
            Logger.Info("[ABI] cache loops started");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            Players.Stop();
            TimerResolution.Disable1ms();
            WebRadarUI.StopIfRunning();
            Logger.Info("[ABI] cache loops stopped");
        }

        public void Tick()
        {
            if (!_running) return;
            RunAimbotIfNeeded();
        }

        public void Draw(ImGuiWindowFlags flags)
        {
            // compute effective zoom once per frame
            float zoomEff = 1f;
            if (Players.TryGetZoom(out var zinfo) && zinfo.Valid)
                zoomEff = MathF.Max(1f, zinfo.Zoom);

            if (UiVisibility.MenusHidden)
            {
                if (_running && Players.ActorList.Count > 0)
                {
                    ABIESP.Render(
                        Cfg.DrawBoxes, Cfg.DrawNames, Cfg.DrawDistance, Cfg.DrawSkeletons,
                        Cfg.DrawDeathMarkers, Cfg.DeathMarkerMaxDist, Cfg.DeathMarkerBaseSize,
                        Cfg.MaxDistance, Cfg.MaxSkeletonDistance,
                        Cfg.ColorPlayer, Cfg.ColorBot,
                        Cfg.ColorBoxVisible, Cfg.ColorBoxInvisible,
                        Cfg.ColorSkelVisible, Cfg.ColorSkelInvisible,
                        Cfg.DeadFill, Cfg.DeadOutline,
                        zoomEff);

                    // Loot ESP with stable world positions from ABILoot
                    ABILootESP.Render(Cfg, Players.LocalPosition, Players.Camera, zoomEff);
                    ABILootWidget.Render(Cfg, Players.LocalPosition);
                }
                DrawAimbotOverlay();
                return;
            }

            Config<ABIGameConfig>.DrawConfigPanel(Name, cfg =>
            {
                bool vmmReady = DmaMemory.IsVmmReady;
                bool attached = DmaMemory.IsAttached;

                var statusCol = (attached && _running) ? new Vector4(0, 0.8f, 0, 1) :
                                attached ? new Vector4(0.85f, 0.75f, 0.15f, 1) :
                                           new Vector4(1, 0.3f, 0.2f, 1);
                DrawStatusInline(statusCol,
                    attached ? (_running ? "Attached �� Threads running" : "Attached �� Threads stopped")
                             : "Not attached");

                ImGui.Separator();

                if (ImGui.BeginTabBar("ABI_Tabs", ImGuiTabBarFlags.NoCloseWithMiddleMouseButton))
                {
                    if (ImGui.BeginTabItem("Main"))
                    {
                        ImGui.TextDisabled("VMM & Attach");
                        if (!vmmReady)
                        {
                            if (ImGui.Button("Init VMM")) VmmService.InitOnly();
                            ImGui.SameLine(); ImGui.TextDisabled("�� initialize before attaching");
                        }
                        else
                        {
                            ImGui.InputText("Process Name", ref cfg.AbiExe, 128);
                            if (!attached)
                            {
                                if (ImGui.Button($"Attach ({cfg.AbiExe})")) Attach();
                                ImGui.SameLine(); ImGui.TextDisabled("�� attaches without process picker");
                            }
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Dispose VMM")) Dispose();

                        ImGui.Separator();

                        if (!attached) ImGui.BeginDisabled();
                        if (ImGui.Button(_running ? "Stop Threads" : "Start Threads"))
                        { if (_running) Stop(); else Start(); }
                        if (!attached) ImGui.EndDisabled();

                        ImGui.Separator();
                        DrawInputManagerBlock(vmmReady);

                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("ESP"))
                    {
                        if (ImGui.CollapsingHeader("Basics", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            if (ImGui.Button("Open Loot List")) _showLootWindow = true;
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� browse containers & ground loot");

                            ImGui.Checkbox("Draw Boxes", ref cfg.DrawBoxes);
                            ImGui.Checkbox("Draw Names", ref cfg.DrawNames);
                            ImGui.Checkbox("Draw Distance", ref cfg.DrawDistance);
                            ImGui.Checkbox("Draw Skeletons", ref cfg.DrawSkeletons);
                            ImGui.Checkbox("Show Debug Info", ref cfg.ShowDebug);

                            ImGui.SliderFloat("Max Draw Distance (m)", ref cfg.MaxDistance, 50f, 3000f, "%.0f");
                            ImGui.SliderFloat("Skeleton Draw Distance (m)", ref cfg.MaxSkeletonDistance, 25f, 2000f, "%.0f");
                        }

                        if (ImGui.CollapsingHeader("Death Markers"))
                        {
                            ImGui.Checkbox("Enable Death Markers", ref cfg.DrawDeathMarkers);
                            ImGui.SliderFloat("Max Marker Distance (m)", ref cfg.DeathMarkerMaxDist, 50f, 5000f, "%.0f");
                            ImGui.SliderFloat("Marker Base Size (px)", ref cfg.DeathMarkerBaseSize, 4f, 24f, "%.0f");
                        }

                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Loot"))
                    {
                        if (ImGui.CollapsingHeader("Ground Loot", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.Checkbox("Draw Ground Loot", ref cfg.DrawGroundLoot);
                            ImGui.Checkbox("Show Names", ref cfg.DrawGroundLootNames);
                            ImGui.Checkbox("Show Distance", ref cfg.DrawGroundLootDistance);
                            ImGui.SliderFloat("Item Max Distance (m)", ref cfg.GroundLootMaxDistance, 10f, 500f, "%.0f");
                            ImGui.SliderFloat("Item Marker Size (px)", ref cfg.GroundLootMarkerSize, 2f, 16f, "%.0f");
                        }

                        if (ImGui.CollapsingHeader("Containers", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.Checkbox("Draw Containers", ref cfg.DrawContainers);
                            ImGui.Checkbox("Names", ref cfg.DrawContainerNames);
                            ImGui.Checkbox("Distance", ref cfg.DrawContainerDistance);
                            ImGui.Checkbox("Show Empty Containers", ref cfg.DrawEmptyContainers);
                            ImGui.SliderFloat("Max Distance (m)", ref cfg.ContainerMaxDistance, 10f, 500f, "%.0f");
                            ImGui.SliderFloat("Marker Size (px)", ref cfg.ContainerMarkerSize, 2f, 16f, "%.0f");
                            ImGui.ColorEdit4("Filled Color", ref cfg.ContainerFilledColor);
                            ImGui.ColorEdit4("Empty Color", ref cfg.ContainerEmptyColor);
                        }

                        if (ImGui.CollapsingHeader("Price Filters & Colors", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Price-based filtering:");
                            ImGui.Indent(10f);

                            ImGui.InputInt("Min Price (Regular Loot)", ref cfg.LootMinPriceRegular);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Items below this won't show");

                            ImGui.InputInt("Min Price (Important Loot)", ref cfg.LootMinPriceImportant);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Items above this get special marker/color");

                            ImGui.Unindent(10f);

                            ImGui.Separator();
                            ImGui.Checkbox("Show Prices on Labels", ref cfg.LootShowPrice);

                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Color scheme:");
                            ImGui.Indent(10f);

                            ImGui.ColorEdit4("Regular Loot Color", ref cfg.LootRegularColor);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Diamond marker");

                            ImGui.ColorEdit4("Important Loot Color", ref cfg.LootImportantColor);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Star marker");

                            ImGui.Unindent(10f);
                        }

                        if (ImGui.CollapsingHeader("Text Filtering (Advanced)"))
                        {
                            ImGui.InputText("Include Filter (comma-sep)", ref cfg.LootFilterInclude, 256);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Show only items matching these terms");

                            ImGui.InputText("Exclude Filter (comma-sep)", ref cfg.LootFilterExclude, 256);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Hide items matching these terms");
                        }

                        if (ImGui.CollapsingHeader("Loot Widget"))
                        {
                            ImGui.Checkbox("Enable Loot Widget", ref cfg.DrawLootWidget);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Real-time list of valuable loot");

                            ImGui.InputInt("Max Items in Widget", ref cfg.LootWidgetMaxItems);
                            cfg.LootWidgetMaxItems = Math.Clamp(cfg.LootWidgetMaxItems, 5, 50);

                            ImGui.InputInt("Widget Min Price", ref cfg.LootWidgetMinPrice);
                            ImGui.SameLine();
                            ImGui.TextDisabled("�� Items must be worth this much to show in widget");

                            ImGui.Checkbox("Show Distance in Widget", ref cfg.LootWidgetShowDistance);

                            ImGui.Separator();
                            ImGui.Text("Widget Position:");
                            var widgetPos = cfg.LootWidgetPosition;
                            ImGui.SliderFloat("X", ref widgetPos.X, 0f, 1920f, "%.0f");
                            ImGui.SliderFloat("Y", ref widgetPos.Y, 0f, 1080f, "%.0f");
                            cfg.LootWidgetPosition = widgetPos;

                            ImGui.ColorEdit4("Widget Background", ref cfg.LootWidgetBackground);
                            ImGui.ColorEdit4("Important Item Color", ref cfg.LootWidgetImportantColor);
                        }

                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("WebRadar"))
                    {
                        WebRadarUI.DrawPanel(_webRadarSource);
                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Colors"))
                    {
                        ImGui.Text("ESP Colors");
                        ImGui.ColorEdit4("Box Visible", ref cfg.ColorBoxVisible);
                        ImGui.ColorEdit4("Box Invisible", ref cfg.ColorBoxInvisible);
                        ImGui.ColorEdit4("Skel Visible", ref cfg.ColorSkelVisible);
                        ImGui.ColorEdit4("Skel Invisible", ref cfg.ColorSkelInvisible);

                        ImGui.Separator();
                        ImGui.Text("Base Labels");
                        ImGui.ColorEdit4("Player", ref cfg.ColorPlayer);
                        ImGui.ColorEdit4("Bot", ref cfg.ColorBot);

                        ImGui.Separator();
                        ImGui.Text("Death Marker Colors");
                        ImGui.ColorEdit4("Dead Fill", ref cfg.DeadFill);
                        ImGui.ColorEdit4("Dead Outline", ref cfg.DeadOutline);

                        ImGui.EndTabItem();
                    }

                    if (ImGui.BeginTabItem("Aimbot"))
                    {
                        DrawAimbotTab(cfg);
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }

                if (cfg.ShowDebug) DrawDebugBlock();

                if (_running && Players.ActorList.Count > 0)
                {
                    float zf = 1f;
                    if (Players.TryGetZoom(out var zi2) && zi2.Valid) zf = MathF.Max(1f, zi2.Zoom);

                    ABIESP.Render(
                        Cfg.DrawBoxes, Cfg.DrawNames, Cfg.DrawDistance, Cfg.DrawSkeletons,
                        Cfg.DrawDeathMarkers, Cfg.DeathMarkerMaxDist, Cfg.DeathMarkerBaseSize,
                        Cfg.MaxDistance, Cfg.MaxSkeletonDistance,
                        Cfg.ColorPlayer, Cfg.ColorBot,
                        Cfg.ColorBoxVisible, Cfg.ColorBoxInvisible,
                        Cfg.ColorSkelVisible, Cfg.ColorSkelInvisible,
                        Cfg.DeadFill, Cfg.DeadOutline,
                        zf);

                    ABILootESP.Render(Cfg, Players.LocalPosition, Players.Camera, zf);
                    ABILootWidget.Render(Cfg, Players.LocalPosition);
                }
                if (_showLootWindow) DrawLootListWindow();
                DrawAimbotOverlay();
            });
        }

        // ������������������ Loot UI state ������������������
        private static bool _showLootWindow = false;
        private static string _lootFilter = "";

        private static string GetActorClassName(ulong actor)
        {
            try { return actor == 0 ? "" : (ABINamePool.GetName(DmaMemory.Read<uint>(actor + 24)) ?? ""); }
            catch { return ""; }
        }

        private static Vector3 GetActorWorldPos(ulong actor)
        {
            if (actor == 0) return default;
            try
            {
                ulong root = DmaMemory.Read<ulong>(actor + ABIOffsets.AActor_RootComponent);
                if (root == 0) return default;
                ulong ctwPtr = DmaMemory.Read<ulong>(root + ABIOffsets.USceneComponent_ComponentToWorld_Ptr);
                if (ctwPtr == 0) return default;
                var t = DmaMemory.Read<FTransform>(ctwPtr);
                if (!float.IsFinite(t.Translation.X) || !float.IsFinite(t.Translation.Y) || !float.IsFinite(t.Translation.Z)) return default;
                return t.Translation; // no bias
            }
            catch { return default; }
        }

        private static void DrawLootListWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(1200, 720), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("ABI Loot List", ref _showLootWindow,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.MenuBar))
            {
                ImGui.End();
                return;
            }

            if (ImGui.BeginMenuBar())
            {
                ImGui.TextDisabled("Filter:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(240);
                ImGui.InputText("##loot_filter", ref _lootFilter, 128);
                ImGui.SameLine();
                if (ImGui.Button("Clear")) _lootFilter = "";

                ImGui.SameLine();
                if (ImGui.Button(ABILoot.EnableDebugLogging ? "Disable Debug Log" : "Enable Debug Log"))
                    ABILoot.EnableDebugLogging = !ABILoot.EnableDebugLogging;

                ImGui.EndMenuBar();
            }

            if (!ABILoot.TryGetLoot(out var frame) || frame.Items == null)
            {
                ImGui.TextDisabled("No loot data yet.");
                ImGui.End();
                return;
            }

            var snap = ABILoot.GetDebugSnapshot();
            if (ImGui.CollapsingHeader("Diagnostics", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.TextDisabled($"Actors scanned: {snap.ActorsScanned}");
                ImGui.TextDisabled($"Containers: {frame.ContainersFound}  �� Expanded: {frame.ContainersExpanded} (mgr {snap.ContainersExpandedMgr} / heu {snap.ContainersExpandedHeu})");
                ImGui.TextDisabled($"Loose items: {snap.ItemsLoose}");
                ImGui.TextDisabled($"Prices �� common: {snap.PricesFromCommon} �� provider: {snap.PricesFromProvider}");
                ImGui.TextDisabled($"Labels from common: {snap.LabelsFromCommon}");
            }

            int itemsInContainers = frame.Items.Count(x => x.InContainer);
            int itemsOnGround = frame.Items.Count - itemsInContainers;
            ImGui.TextDisabled($"Items: {itemsInContainers} in containers, {itemsOnGround} on ground");

            ImGui.Separator();

            var items = frame.Items;
            string filter = _lootFilter?.Trim() ?? "";
            bool useFilter = filter.Length >= 2;

            var ground = new List<ABILoot.Item>(256);
            var byContainer = new Dictionary<ulong, List<ABILoot.Item>>(64);

            foreach (var it in items)
            {
                if (useFilter)
                {
                    if (!(it.ClassName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
                       || it.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                        continue;
                }

                if (!it.InContainer || it.ContainerActor == 0)
                    ground.Add(it);
                else
                {
                    if (!byContainer.TryGetValue(it.ContainerActor, out var list))
                    {
                        list = new List<ABILoot.Item>(16);
                        byContainer[it.ContainerActor] = list;
                    }
                    list.Add(it);
                }
            }

            if (ImGui.CollapsingHeader($"Containers ({byContainer.Count} with items)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (byContainer.Count == 0)
                {
                    ImGui.TextDisabled("No containers with items found.");
                }
                else
                {
                    var contKeys = new List<ulong>(byContainer.Keys);
                    contKeys.Sort((a, b) =>
                    {
                        var pa = GetActorWorldPos(a);
                        var pb = GetActorWorldPos(b);
                        var loc = Players.LocalPosition;
                        float da = Vector3.DistanceSquared(loc, pa);
                        float db = Vector3.DistanceSquared(loc, pb);
                        return da.CompareTo(db);
                    });

                    foreach (var cont in contKeys)
                    {
                        string cname = GetActorClassName(cont);
                        var cpos = GetActorWorldPos(cont);
                        string cposStr = $"{cpos.X:F0}, {cpos.Y:F0}, {cpos.Z:F0}";
                        var list = byContainer[cont];

                        var containerRow = frame.Containers.Find(cc => cc.Actor == cont);
                        string disp = containerRow.ClassName ?? cname;
                        bool open = ImGui.TreeNodeEx($"0x{cont:X}  {disp}  ({list.Count})###cont_{cont}",
                                                     ImGuiTreeNodeFlags.SpanFullWidth);
                        ImGui.SameLine();
                        ImGui.TextDisabled($"pos: {cposStr}");

                        if (open)
                        {
                            if (ImGui.BeginTable($"tbl_cont_{cont}", 10,
                                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                                    new Vector2(-1, Math.Min(300, 24 * (list.Count + 2)))))
                            {
                                ImGui.TableSetupScrollFreeze(0, 1);
                                ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthStretch);
                                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch);
                                ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 56);
                                ImGui.TableSetupColumn("Mgr?", ImGuiTableColumnFlags.WidthFixed, 44);
                                ImGui.TableSetupColumn("MgrOff", ImGuiTableColumnFlags.WidthFixed, 64);
                                ImGui.TableSetupColumn("BaseCnt", ImGuiTableColumnFlags.WidthFixed, 64);
                                ImGui.TableSetupColumn("HeuTried", ImGuiTableColumnFlags.WidthFixed, 64);
                                ImGui.TableSetupColumn("HeuFound", ImGuiTableColumnFlags.WidthFixed, 64);
                                ImGui.TableSetupColumn("Common@", ImGuiTableColumnFlags.WidthFixed, 160);
                                ImGui.TableSetupColumn("Pos (X,Y,Z)", ImGuiTableColumnFlags.WidthFixed, 220);
                                ImGui.TableHeadersRow();

                                var dbg = containerRow.Debug;

                                // single row describing the container itself
                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(containerRow.ClassName ?? cname);
                                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(containerRow.Label ?? "");
                                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(list.Count.ToString());
                                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(dbg.UsedManagerPath ? "Y" : "N");
                                ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(dbg.ManagerOffsetTried != 0 ? $"0x{dbg.ManagerOffsetTried:X}" : "-");
                                ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(dbg.BaseListCount.ToString());
                                ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(dbg.HeuristicArraysTried.ToString());
                                ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(dbg.HeuristicItemsFound.ToString());
                                ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted(dbg.CommonDataPtr != 0 ? $"0x{dbg.CommonDataPtr:X} (slot 0x{dbg.CommonDataSlot:X})" : "-");
                                ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(cposStr);

                                ImGui.EndTable();
                            }

                            ImGui.TreePop();
                        }
                    }
                }
            }

            ImGui.Separator();

            if (ImGui.CollapsingHeader($"Ground Loot ({ground.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("tbl_ground_loot", 11,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                        new Vector2(-1, -1)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 50);
                    ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("PriceSrc", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupColumn("LabelSrc", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Common@", ImGuiTableColumnFlags.WidthFixed, 170);
                    ImGui.TableSetupColumn("In Cont", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Actor", ImGuiTableColumnFlags.WidthFixed, 140);
                    ImGui.TableSetupColumn("Pos (X,Y,Z)", ImGuiTableColumnFlags.WidthFixed, 220);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 1);
                    ImGui.TableHeadersRow();

                    var loc = Players.LocalPosition;
                    ground.Sort((a, b) =>
                    {
                        float da = Vector3.DistanceSquared(loc, a.Position);
                        float db = Vector3.DistanceSquared(loc, b.Position);
                        return da.CompareTo(db);
                    });

                    foreach (ref readonly var it in CollectionsMarshal.AsSpan(ground))
                    {
                        var p = it.Position;
                        string posStr = $"{p.X:F0}, {p.Y:F0}, {p.Z:F0}";
                        var dbg = it.Debug;

                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(it.ClassName ?? "");
                        ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(it.Label ?? "");
                        ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(it.Stack.ToString());
                        ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(it.ApproxPrice > 0 ? it.ApproxPrice.ToString() : "-");
                        ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(dbg.PriceSrc.ToString());
                        ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(dbg.LabelSrc.ToString());
                        ImGui.TableSetColumnIndex(6); ImGui.TextUnformatted(dbg.CommonDataPtr != 0 ? $"0x{dbg.CommonDataPtr:X} (slot 0x{dbg.CommonDataSlot:X})" : "-");
                        ImGui.TableSetColumnIndex(7); ImGui.TextUnformatted(it.InContainer ? "yes" : "no");
                        ImGui.TableSetColumnIndex(8); ImGui.TextUnformatted($"0x{it.Actor:X}");
                        ImGui.TableSetColumnIndex(9); ImGui.TextUnformatted(posStr);
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
        }

        private static void DrawDebugBlock()
        {
            ImGui.Separator();
            ImGui.Text("�� Debug Info ��");
            ImGui.Text($"UWorld: 0x{Players.UWorld:X}");
            ImGui.Text($"UGameInstance: 0x{Players.UGameInstance:X}");
            ImGui.Text($"GameState: 0x{Players.GameState:X}");
            ImGui.Text($"PersistentLevel: 0x{Players.PersistentLevel:X}");
            ImGui.Text($"ActorCount: {Players.ActorCount}");
            ImGui.Text($"ActorList.Count: {Players.ActorList.Count}");
            ImGui.Text($"LocalPawn: 0x{Players.LocalPawn:X}");
            ImGui.Text($"LocalRoot: 0x{Players.LocalRoot:X}");
            ImGui.Text($"CameraMgr: 0x{Players.LocalCameraMgr:X}");
            ImGui.Text($"CameraFov: {Players.Camera.Fov:F1}");
            ImGui.Text($"LocalPos: {Players.LocalPosition}");
            ImGui.Text($"CtrlYaw: {Players.CtrlYaw:F1}");

            if (Players.TryGetZoom(out var zi) && zi.Valid)
            {
                ImGui.Separator();
                ImGui.Text("Weapon Zoom Debug");
                ImGui.Text($"WeaponMan: 0x{zi.WeaponMan:X}");
                ImGui.Text($"CurrentWeapon: 0x{zi.CurrentWeapon:X}");
                ImGui.Text($"ZoomComp: 0x{zi.ZoomComp:X}");
                ImGui.Text($"ScopeMagnification: {zi.ScopeMag:F2}x");
                ImGui.Text($"ZoomProgress: {zi.Progress:P0}");
                ImGui.Text($"Effective Zoom: {zi.Zoom:F2}x");
            }
            else
            {
                ImGui.Separator();
                ImGui.Text("Weapon Zoom Debug");
                ImGui.TextDisabled(ABIOffsetsExt.OFF_PAWN_WEAPONMAN == 0
                    ? "OFF_PAWN_WEAPONMAN = 0 (fill this offset to enable zoom read)"
                    : "Zoom not available (component/weapon/zoomcomp null)");
            }

            DrawPlayersDebugWindow();
        }

        private static void DrawAimbotTab(ABIGameConfig cfg)
        {
            ImGui.TextDisabled("Simple Makcu aimbot (hold key/button to run)");
            ImGui.Checkbox("Enable Aimbot", ref cfg.AimbotEnabled);
            ImGui.SameLine();
            ImGui.Checkbox("Require Visible", ref cfg.AimbotRequireVisible);

            ImGui.Checkbox("Headshot AI", ref cfg.AimbotHeadshotAI);
            ImGui.SameLine();
            ImGui.Checkbox("Target only AI", ref cfg.AimbotTargetAIOnly);
            ImGui.SameLine();
            ImGui.Checkbox("Only PMC", ref cfg.AimbotTargetPMCOnly);

            ImGui.Separator();

            int selBone = Array.IndexOf(_boneIndices, cfg.AimbotTargetBone);
            if (selBone < 0) selBone = Array.IndexOf(_boneIndices, Skeleton.IDX_Head);
            if (ImGui.BeginCombo("Target Bone", _boneNames[Math.Clamp(selBone, 0, _boneNames.Length - 1)]))
            {
                for (int i = 0; i < _boneNames.Length; i++)
                {
                    bool selected = (i == selBone);
                    if (ImGui.Selectable(_boneNames[i], selected))
                    {
                        selBone = i;
                        cfg.AimbotTargetBone = _boneIndices[i];
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.Checkbox("Random Bone", ref cfg.AimbotRandomBone);

            string[] tgtModes = new[]
            {
                "Closest by distance (within FOV)",
                "Closest to crosshair (within FOV)"
            };
            int modeIdx = (int)cfg.AimbotTargetMode;
            if (ImGui.BeginCombo("Target Select Mode", tgtModes[Math.Clamp(modeIdx, 0, tgtModes.Length - 1)]))
            {
                for (int i = 0; i < tgtModes.Length; i++)
                {
                    bool sel = (i == modeIdx);
                    if (ImGui.Selectable(tgtModes[i], sel))
                    {
                        modeIdx = i;
                        cfg.AimbotTargetMode = (AimbotTargetMode)i;
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("(filters inside FOV)");

            ImGui.SliderFloat("Max Distance (m)", ref cfg.AimbotMaxMeters, 10f, 2000f, "%.0f");
            ImGui.SliderFloat("Max FOV (px)", ref cfg.AimbotFovPx, 50f, 1200f, "%.0f");
            ImGui.SliderFloat("Pixel Power (x) [<= 0.20 best]", ref cfg.AimbotPixelPower, 0.01f, 0.50f, "%.2f");
            ImGui.SliderFloat("Smooth Segments (N/A)", ref cfg.AimbotSmoothSegments, 1f, 12f, "%.0f");
            ImGui.SliderFloat("Deadzone (px)", ref cfg.AimbotDeadzonePx, 0f, 8f, "%.1f");

            ImGui.Separator();
            ImGui.TextDisabled("Trigger (hold to aim):");

            int[] keys = new[] { 0x02, 0x01, 0x12, 0x10, 0x11, 0x05, 0x06, 0x14, 0x56, 0x46, 0x51, 0x45, 0x58, 0x5A };
            string[] keyNames = new[] { "RButton", "LButton", "Alt", "Shift", "Ctrl", "Mouse4", "Mouse5", "Caps", "V", "F", "Q", "E", "X", "Z" };
            int keySel = Array.IndexOf(keys, cfg.AimbotKey);
            if (keySel < 0) keySel = 0;
            if (ImGui.BeginCombo("Aimbot Key (KB)", keyNames[keySel]))
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    bool selected = (i == keySel);
                    if (ImGui.Selectable(keyNames[i], selected))
                    {
                        keySel = i;
                        cfg.AimbotKey = keys[i];
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            var mb = cfg.AimbotMakcuHoldButton;
            var mbNames = new[] { "Left", "Right", "Middle", "Mouse4", "Mouse5" };
            var mbVals  = new[] { MakcuMouseButton.Left, MakcuMouseButton.Right, MakcuMouseButton.Middle, MakcuMouseButton.mouse4, MakcuMouseButton.mouse5 };
            int mbSel = Array.IndexOf(mbVals, mb);
            if (mbSel < 0) mbSel = 3;
            if (ImGui.BeginCombo("Makcu Hold Button", mbNames[mbSel]))
            {
                for (int i = 0; i < mbVals.Length; i++)
                {
                    bool selected = (i == mbSel);
                    if (ImGui.Selectable(mbNames[i], selected))
                    {
                        mbSel = i; cfg.AimbotMakcuHoldButton = mbVals[i];
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.Separator();
            ImGui.TextDisabled($"Makcu: {(Device.connected ? "Connected" : "Disconnected")} �� InputManager: {(InputManager.IsReady ? "Ready" : "Not Ready")}");
        }

        private static void DrawPlayersDebugWindow()
        {
            if (!Players.TryGetFrame(out var fr)) return;

            ImGui.Begin("ABI Player Debug", ImGuiWindowFlags.None);

            var actors = new List<Players.ABIPlayer>();
            lock (Players.Sync)
            {
                if (Players.ActorList.Count > 0)
                    actors = new List<Players.ABIPlayer>(Players.ActorList);
            }

            var posMap = new Dictionary<ulong, Players.ActorPos>(fr.Positions?.Count ?? 0);
            if (fr.Positions != null)
                for (int i = 0; i < fr.Positions.Count; i++)
                    posMap[fr.Positions[i].Pawn] = fr.Positions[i];

            if (ImGui.BeginTable("abi_dbg_tbl", 12, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(1160, 400)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 44);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, 88);
                ImGui.TableSetupColumn("DeathInfo.bIsDead", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Position (X,Y,Z)", ImGuiTableColumnFlags.WidthFixed, 240);
                ImGui.TableSetupColumn("Skel", ImGuiTableColumnFlags.WidthFixed, 44);
                ImGui.TableSetupColumn("Visible", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Dist(m)", ImGuiTableColumnFlags.WidthFixed, 62);
                ImGui.TableSetupColumn("Pawn", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("MeshPtr", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("DeathComp", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();

                for (int i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    posMap.TryGetValue(a.Pawn, out var ap);
                    if (ABIESP.IsBogusPos(ap.Position)) continue;
                    string type = a.IsBot ? "BOT" : "PMC";
                    string hp = (ap.HealthMax > 1f) ? $"{ap.Health:F0}/{ap.HealthMax:F0}" : "-";
                    bool hasSkel = ap.HasFreshSkeleton;
                    bool visible = ap.IsVisible;
                    float distM = fr.Local != default ? Vector3.Distance(fr.Local, ap.Position) / 100f : 0f;

                    var p = ap.Position;
                    string posStr = $"{p.X:F0}, {p.Y:F0}, {p.Z:F0}";

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);  ImGui.TextUnformatted(i.ToString());
                    ImGui.TableSetColumnIndex(1);  ImGui.TextUnformatted(type);
                    ImGui.TableSetColumnIndex(2);  ImGui.TextUnformatted(a.Name ?? "");
                    ImGui.TableSetColumnIndex(3);  ImGui.TextUnformatted(hp);
                    ImGui.TableSetColumnIndex(4);  ImGui.TextUnformatted(ap.DeadByDeathComp ? "true" : "false");
                    ImGui.TableSetColumnIndex(5);  ImGui.TextUnformatted(posStr);
                    ImGui.TableSetColumnIndex(6);  ImGui.TextUnformatted(hasSkel ? "fresh" : "no");
                    ImGui.TableSetColumnIndex(7);  ImGui.TextUnformatted(visible ? "yes" : "no");
                    ImGui.TableSetColumnIndex(8);  ImGui.TextUnformatted(distM > 0 ? distM.ToString("F1") : "-");
                    ImGui.TableSetColumnIndex(9);  ImGui.TextUnformatted($"0x{a.Pawn:X}");
                    ImGui.TableSetColumnIndex(10); ImGui.TextUnformatted(a.Mesh != 0 ? $"0x{a.Mesh:X}" : "-");
                    ImGui.TableSetColumnIndex(11); ImGui.TextUnformatted(a.DeathComp != 0 ? $"0x{a.DeathComp:X}" : "-");
                }

                ImGui.EndTable();
            }

            ImGui.End();
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

        private static void DrawInputManagerBlock(bool vmmReady)
        {
            ImGui.TextDisabled("Input Manager & Makcu");

            if (ImGui.Button("Refresh Devices"))
            {
                JobSystem.Schedule(() =>
                {
                    _inputDevices = Device.EnumerateSerialDevices();
                    if (_selectedInputIndex >= _inputDevices.Count) _selectedInputIndex = -1;
                });
            }

            ImGui.Separator();
            ImGui.Text("Serial Devices:");

            string preview = (_selectedInputIndex >= 0 && _selectedInputIndex < _inputDevices.Count)
                ? _inputDevices[_selectedInputIndex].ToString()
                : "(none)";

            ImGui.SetNextItemWidth(400);
            if (ImGui.BeginCombo("##AbiInputDevices", preview))
            {
                for (int i = 0; i < _inputDevices.Count; i++)
                {
                    bool sel = (i == _selectedInputIndex);
                    if (ImGui.Selectable(_inputDevices[i].ToString(), sel))
                        _selectedInputIndex = i;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.Separator();
            ImGui.TextDisabled("Makcu Device");

            if (ImGui.Button("Connect Makcu"))
            {
                if (_selectedInputIndex >= 0 && _selectedInputIndex < _inputDevices.Count)
                {
                    var dev = _inputDevices[_selectedInputIndex];
                    JobSystem.Schedule(() =>
                    {
                        if (Device.MakcuConnect(dev.Port))
                            Console.WriteLine($"[+] Makcu connected on {dev.Port}");
                        else
                            Console.WriteLine("[-] Makcu connect failed");
                    });
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Disconnect Makcu"))
            {
                JobSystem.Schedule(() =>
                {
                    Device.Disconnect();
                    Console.WriteLine("[*] Makcu disconnected");
                });
            }

            ImGui.Separator();
            ImGui.TextDisabled("Input Manager");

            if (!vmmReady) ImGui.BeginDisabled();
            if (ImGui.Button("Init InputManager"))
            {
                JobSystem.Schedule(() =>
                {
                    var adapter = new DmaMemory.VmmSharpExAdapter(DmaMemory.Vmm!);
                    InputManager.BeginInitializeWithRetries(
                        adapter,
                        TimeSpan.FromSeconds(1),
                        onComplete: (ok, err) =>
                        {
                            if (ok) Console.WriteLine("[+] InputManager ready.");
                            else Console.WriteLine($"[-] InputManager init failed: {err}");
                        });
                    _inputInitialized = true;
                });
            }
            if (!vmmReady)
            {
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Init blocked: VMM not initialized.");
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("Shutdown InputManager"))
            {
                JobSystem.Schedule(() =>
                {
                    InputManager.Shutdown();
                    _inputInitialized = false;
                    Console.WriteLine("[*] InputManager shut down");
                });
            }

            ImGui.Separator();
            DrawStatusLight("Makcu Connected", Device.connected);
            DrawStatusLight("InputManager Ready", InputManager.IsReady);
        }

        private static void DrawStatusLight(string label, bool state)
        {
            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float y = p.Y + ImGui.GetTextLineHeight() * 0.5f;
            Vector4 col = state ? new Vector4(0, 0.8f, 0, 1) : new Vector4(0.9f, 0.3f, 0.1f, 1);
            dl.AddCircleFilled(new Vector2(p.X + 6, y), 5, ImGui.ColorConvertFloat4ToU32(col));
            ImGui.Dummy(new Vector2(16, ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            ImGui.TextDisabled(label);
        }

        private void HandleAbiAction(string action)
        {
            switch (action)
            {
                case "ABI_ToggleThreads": if (_running) Stop(); else Start(); break;
                case "ABI_StartThreads":  if (!_running) Start(); break;
                case "ABI_StopThreads":   if (_running) Stop(); break;

                case "ABI_ToggleBoxes":      Cfg.DrawBoxes     = !Cfg.DrawBoxes; break;
                case "ABI_ToggleNames":      Cfg.DrawNames     = !Cfg.DrawNames; break;
                case "ABI_ToggleDistance":   Cfg.DrawDistance  = !Cfg.DrawDistance; break;
                case "ABI_ToggleSkeletons":  Cfg.DrawSkeletons = !Cfg.DrawSkeletons; break;
                case "ABI_ToggleDebug":      Cfg.ShowDebug     = !Cfg.ShowDebug; break;

                case "ABI_Attach": Attach(); break;
                case "ABI_DisposeVmm": Dispose(); break;
            }
        }

        private static void EnsureAbiKeybindProfile()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Configs", "ABI");
            var path = Path.Combine(root, "abi.keybinds.json");
            if (File.Exists(path)) return;

            Directory.CreateDirectory(root);

            var prof = new KeybindProfile
            {
                ProfileName = "ABI Keybinds",
                Category = "ABI",
                Binds = new List<KeybindEntry>
                {
                    new KeybindEntry { Name="ABI: Toggle Threads", Vk=0x75, Mode=KeybindMode.OnPress, Action="ABI_ToggleThreads" },
                    new KeybindEntry { Name="ABI: Toggle Boxes",   Vk=0xC0, Mode=KeybindMode.OnPress, Action="ABI_ToggleBoxes" },
                    new KeybindEntry { Name="ABI: Toggle Names",   Vk=0x4E, Mode=KeybindMode.OnPress, Action="ABI_ToggleNames" },
                    new KeybindEntry { Name="ABI: Toggle Dist",    Vk=0x44, Mode=KeybindMode.OnPress, Action="ABI_ToggleDistance" },
                    new KeybindEntry { Name="ABI: Toggle Skel",    Vk=0x53, Mode=KeybindMode.OnPress, Action="ABI_ToggleSkeletons" },
                    new KeybindEntry { Name="ABI: Toggle Debug",   Vk=0x47, Mode=KeybindMode.OnPress, Action="ABI_ToggleDebug" },
                    new KeybindEntry { Name="ABI: Web Radar",      Vk=0x52, Mode=KeybindMode.OnPress, Action="ABI_ToggleWebRadar" },
                    new KeybindEntry { Name="ABI: Attach",         Vk=0x41, Mode=KeybindMode.OnPress, Action="ABI_Attach" },
                    new KeybindEntry { Name="ABI: Dispose VMM",    Vk=0x58, Mode=KeybindMode.OnPress, Action="ABI_DisposeVmm" },
                }
            };

            KeybindRegistry.Save(path, prof);
        }

        private void RunAimbotIfNeeded()
        {
            var cfg = Cfg;
            if (!cfg.AimbotEnabled) return;

            bool keyHeld = false;
            bool makcuHeld = false;

            if (InputManager.IsReady)
            {
                keyHeld = InputManager.IsKeyDown(cfg.AimbotKey);
                makcuHeld = false;
            }
            else
            {
                makcuHeld = Device.connected &&
                            Device.bState != null &&
                            Device.button_pressed(cfg.AimbotMakcuHoldButton);
            }

            if (!(keyHeld || makcuHeld)) return;

            if (!Players.TryGetFrame(out var fr)) return;
            if (fr.Positions == null || fr.Positions.Count == 0) return;

            var best = FindBestTarget(fr, cfg, cfg.AimbotTargetMode, out _, out Vector2 aimScreen);
            if (!best.HasValue) return;

            float W = ScreenService.Current.W;
            float H = ScreenService.Current.H;
            var center = new Vector2(W * 0.5f, H * 0.5f);
            var delta = aimScreen - center;

            if (MathF.Abs(delta.X) < cfg.AimbotDeadzonePx && MathF.Abs(delta.Y) < cfg.AimbotDeadzonePx)
                return;

            _lastAimScreen     = aimScreen;
            _lastAimStampTicks = NowTicks();

            float power = Math.Clamp(cfg.AimbotPixelPower, 0.01f, 0.20f);
            int moveX = (int)MathF.Round(delta.X * power);
            int moveY = (int)MathF.Round(delta.Y * power);

            if (Device.connected) Device.move(moveX, moveY);
        }

        private Players.ActorPos? FindBestTarget(
            Players.Frame fr,
            ABIGameConfig cfg,
            AimbotTargetMode mode,
            out Vector3 aimWorld,
            out Vector2 aimScreen)
        {
            aimWorld = default;
            aimScreen = default;

            var posList = fr.Positions;
            if (posList == null || posList.Count == 0) return null;

            float W = ScreenService.Current.W;
            float H = ScreenService.Current.H;
            var center = new Vector2(W * 0.5f, H * 0.5f);
            float maxFov = Math.Max(16f, cfg.AimbotFovPx);

            float zoom = 1f;
            if (Players.TryGetZoom(out var zi) && zi.Valid) zoom = MathF.Max(1f, zi.Zoom);

            Players.ActorPos? best = null;
            float bestMetric = float.MaxValue;

            for (int i = 0; i < posList.Count; i++)
            {
                var ap = posList[i];
                if (ap.IsDead) continue;
                if (cfg.AimbotRequireVisible && !ap.IsVisible) continue;

                bool? isBot = null;
                lock (Players.Sync)
                {
                    for (int j = 0; j < Players.ActorList.Count; j++)
                        if (Players.ActorList[j].Pawn == ap.Pawn) { isBot = Players.ActorList[j].IsBot; break; }
                }
                if (cfg.AimbotTargetAIOnly && isBot != true) continue;
                if (cfg.AimbotTargetPMCOnly && isBot != false) continue;

                float distM = (fr.Local != default)
                    ? Vector3.Distance(fr.Local, ap.Position) / 100f
                    : float.MaxValue;
                if (distM > cfg.AimbotMaxMeters) continue;

                int targetBone = SelectBone(cfg, isBot == true);
                if (!TryGetBoneWorld(ap, targetBone, out var hitWorld))
                {
                    if (!TryGetBoneWorld(ap, Skeleton.IDX_Head, out hitWorld))
                        hitWorld = ap.Position;
                }

                if (!ABIMath.WorldToScreenZoom(hitWorld, fr.Cam, W, H, zoom, out var pt)) continue;

                float pixelDist = Vector2.Distance(center, pt);
                if (pixelDist > maxFov) continue;

                float metric = mode switch
                {
                    AimbotTargetMode.ClosestWorldDistanceInFov => distM,
                    AimbotTargetMode.ClosestToCrosshairInFov => pixelDist,
                    _ => pixelDist
                };

                if (metric < bestMetric)
                {
                    bestMetric = metric;
                    best = ap;
                    aimWorld = hitWorld;
                    aimScreen = pt;
                }
            }
            return best;
        }

        private static int SelectBone(ABIGameConfig cfg, bool isAI)
        {
            if (isAI && cfg.AimbotHeadshotAI) return Skeleton.IDX_Head;
            if (cfg.AimbotRandomBone)
            {
                var pool = _randomPoolDefault;
                return pool[new Random().Next(0, pool.Length)];
            }
            int idx = cfg.AimbotTargetBone;
            if (idx < 0 || idx >= _boneIndices.Length) idx = Skeleton.IDX_Head;
            return idx;
        }

        private static bool TryGetBoneWorld(in Players.ActorPos ap, int boneIdx, out Vector3 ws)
        {
            ws = default;
            if (boneIdx < 0) return false;

            if (Players.TryGetSkeleton(ap.Pawn, out var pts) && pts != null && boneIdx < pts.Length)
            {
                ws = pts[boneIdx];
                return true;
            }
            return false;
        }

        private static void DrawAimbotOverlay()
        {
            var cfg = Cfg;
            if (!cfg.AimbotEnabled) return;

            float W = ScreenService.Current.W;
            float H = ScreenService.Current.H;
            var center = new Vector2(W * 0.5f, H * 0.5f);

            var dl = ImGui.GetBackgroundDrawList();

            float r = Math.Max(4f, cfg.AimbotFovPx);
            const float fovThickness = 1.6f;
            uint fovCol = U32(new Vector4(1f, 1f, 1f, 0.33f));
            dl.AddCircle(center, r, fovCol, 64, fovThickness);

            if (Cfg.ShowDebug && Players.TryGetZoom(out var zi) && zi.Valid && zi.Zoom > 1.01f)
            {
                float rz = r / MathF.Max(1f, zi.Zoom);
                uint zCol = U32(new Vector4(0.2f, 0.9f, 0.9f, 0.85f));
                dl.AddCircle(center, rz, zCol, 64, 1.8f);

                var label = $"Zoom {zi.Zoom:F2}x (Scope {zi.ScopeMag:F1}x �� {zi.Progress:P0})";
                var size = ImGui.CalcTextSize(label);
                var at = new Vector2(center.X - size.X * 0.5f, center.Y + r + 6f);
                dl.AddText(at, U32(new Vector4(0.9f, 0.9f, 0.9f, 0.95f)), label);
            }

            const double maxAgeMs = 350.0;
            if (_lastAimScreen.HasValue && TicksToMs(NowTicks() - _lastAimStampTicks) <= maxAgeMs)
            {
                var tgt = _lastAimScreen.Value;

                uint lineCol = U32(new Vector4(0.2f, 0.9f, 0.2f, 0.90f));
                const float lineThickness = 2.0f;

                uint dotFill  = U32(new Vector4(0.2f, 0.9f, 0.2f, 0.90f));
                uint dotRing  = U32(new Vector4(0f, 0f, 0f, 0.90f));

                var dl2 = ImGui.GetBackgroundDrawList();
                dl2.AddLine(center, tgt, lineCol, lineThickness);
                dl2.AddCircleFilled(tgt, 4f, dotFill, 24);
                dl2.AddCircle(tgt, 4f, dotRing, 24, 1.6f);
            }
        }
    }
}
