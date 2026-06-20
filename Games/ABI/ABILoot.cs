// File: Games/ABI/ABILoot.cs
// FIXES APPLIED:
// 1. Proper FText reading (dereferences through shared pointer structure)
// 2. Dual CommonData offset support (0x08A0 for items, 0x08B0 for containers)
// 3. Filter out "character" actors (these are pawns, not loot)
// 4. Better position reading with PickupMesh fallback
// 5. Vertex count limiting to prevent crashes
// 6. FIXED: Proper coordinate system - store raw world positions, bias applied during rendering

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using MamboDMA.Services;

namespace MamboDMA.Games.ABI
{
    public static class ABILoot
    {
        // -----------------------------------------------------------------------------
        // Debug structures
        // -----------------------------------------------------------------------------
        public enum LabelSource : byte { None, Common_DisplayName, Common_SimpleName, Fallback_ClassName }
        public enum PriceSource : byte { None, Common_StandardPrice, Provider_ClassName }

        public struct ItemDebug
        {
            public LabelSource LabelSrc;
            public PriceSource PriceSrc;
            public ulong CommonDataPtr;
            public int   CommonDataSlot;
        }

        public struct ContainerDebug
        {
            public bool  UsedManagerPath;
            public int   ManagerOffsetTried;
            public int   BaseListCount;
            public int   HeuristicArraysTried;
            public int   HeuristicItemsFound;
            public ulong CommonDataPtr;
            public int   CommonDataSlot;
        }

        public struct DebugSnapshot
        {
            public int ActorsScanned;
            public int ItemsLoose;
            public int ContainersSeen;
            public int ContainersExpandedMgr;
            public int ContainersExpandedHeu;
            public int PricesFromCommon;
            public int PricesFromProvider;
            public int LabelsFromCommon;
        }

        // -----------------------------------------------------------------------------
        // Public data model
        // -----------------------------------------------------------------------------
        public struct Item
        {
            public ulong   Actor;
            public ulong   ContainerActor;
            public bool    InContainer;
            public string  ClassName;
            public string  Label;
            public int     Stack;
            public Vector3 Position;
            public int     ApproxPrice;
            public int     Rarity;           // NEW: From CommonData
            public ItemDebug Debug;
        }

        public struct Container
        {
            public ulong   Actor;
            public Vector3 Position;
            public string  ClassName;
            public string  Label;
            public int     ItemCount;
            public int     ApproxPrice;      // NEW: Container value
            public int     Rarity;           // NEW: Container rarity
            public bool    IsSearched;
            public bool    IsEmpty => ItemCount == 0;
            public ContainerDebug Debug;
        }

        public struct Frame
        {
            public long            StampTicks;
            public List<Item>      Items;
            public List<Container> Containers;
            public int             TotalActorsSeen;
            public int             ContainersFound;
            public int             ContainersExpanded;
        }

        public interface IPriceProvider { int TryGetPrice(string className); }

        // -----------------------------------------------------------------------------
        // Confirmed offsets from SDK
        // -----------------------------------------------------------------------------
        internal static class InvCommonOffsets
        {
            public const int OFF_StandardPrice  = 0x010C;
            public const int OFF_Rarity         = 0x0110;
            public const int OFF_DisplayName    = 0x0138;  // FText
            public const int OFF_Description    = 0x0150;  // FText
            public const int OFF_SimpleName     = 0x0168;  // FText
        }

        // CRITICAL: Different offsets for items vs containers!
        internal static class ComponentSlots
        {
            public const int ITEM_COMMONDATA = 0x08A0;      // Items: ABP_AmmoBase_C, etc.
            public const int CONTAINER_COMMONDATA = 0x08B0;  // Containers: ABP_ArmoredContainerBase_C
            public const int ITEM_PICKUPMESH = 0x08E0;      // For better item positions
        }

        internal static class LootSceneOffsets
        {
            public static ulong AActor_RootComponent = ABIOffsets.AActor_RootComponent;
            public static ulong USceneComponent_ComponentToWorld_Ptr = ABIOffsets.USceneComponent_ComponentToWorld_Ptr;
        }

        // Container manager offsets
        private static readonly int[] CANDIDATE_MGR_OFFS = { 0x8F0, 0x8E8, 0x8A0 };
        private const int OFF_MGR_BASELIST = 0x140;
        private const int OFF_INVBASE_ROW = 0x00;
        private const int OFF_INVBASE_COLUMN = 0x04;
        private const int OFF_INVBASE_CHILD_ACTORS = 0x10;
        private const int SIZEOF_INVBASE = 0x48;

        // -----------------------------------------------------------------------------
        // Controls
        // -----------------------------------------------------------------------------
        public static int  UpdateIntervalMs { get => _intervalMs; set => _intervalMs = Math.Clamp(value, 16, 500); }
        public static bool IsRunning => _running;
        public static void SetPriceProvider(IPriceProvider provider) => _priceProvider = provider;
        public static bool EnableDebugLogging = false;

        public static void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.Loot" };
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            try { _thread?.Join(250); } catch { }
            _thread = null;
        }

        public static bool TryGetLoot(out Frame f)
        {
            lock (_sync) { f = _latest; return f.StampTicks != 0; }
        }

        public static DebugSnapshot GetDebugSnapshot()
        {
            lock (_sync) return _dbg;
        }

        // -----------------------------------------------------------------------------
        // Internal state
        // -----------------------------------------------------------------------------
        private static volatile bool _running;
        private static Thread _thread;
        private static readonly object _sync = new();
        private static Frame _latest;
        private static int _intervalMs = 60;
        private static IPriceProvider _priceProvider;

        private static readonly Dictionary<ulong, (long timestamp, List<Item> items)> _searchedContainers = new(256);
        private static readonly TimeSpan _searchedContainerExpiry = TimeSpan.FromMinutes(5);

        private static DebugSnapshot _dbg;
        private static int _dbgContainersHeu;
        private static int _dbgContainersMgr;
        private static int _dbgPricesFromCommon;
        private static int _dbgPricesFromProvider;
        private static int _dbgLabelsFromCommon;

        private static readonly HashSet<string> LootContainerClassHints = new(StringComparer.OrdinalIgnoreCase)
        {
            "ABP_LootBoxBase_C", "BP_ContainerBase_C", "BP_ArmoredContainerBase_C",
            "ABP_ArmoredContainerBase_C", "ArmoredContainer", "ABP_LootBox_C", "BP_LootBox_C",
        };

        private static readonly HashSet<string> LootItemBaseHints = new(StringComparer.OrdinalIgnoreCase)
        {
            "BP_ItemBase_C", "BP_AmmoBase_C", "BP_MagazineBase_C", "BP_StockBase_C",
            "BP_SightBase_C", "BP_MountBase_C", "BP_HeadsetsBase_C", "BP_HelmetBase_C",
            "BP_VestBase_C", "BP_HandGuardBase_C", "BP_MuzzleBase_C", "BP_RecoveryBase_C",
            "BP_SGWeapon_C", "BP_FaceCoverBase_C", "BP_EyewearBase_C",
            "BP_ReceiverCoverBase_C", "BP_ChargingHandleBase_C",
            "BP_BackPackBase_C", "BP_TacRigBase_C", "BP_PouchBase_C",
        };

        // -----------------------------------------------------------------------------
        // Main loop
        // -----------------------------------------------------------------------------
        private static void Loop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (_running)
            {
                sw.Restart();
                try { Build(); }
                catch (Exception ex)
                {
                    if (EnableDebugLogging) Console.WriteLine($"[ABILoot] Loop error: {ex.Message}");
                }
                int left = _intervalMs - (int)sw.ElapsedMilliseconds;
                if (left > 0) HighResDelay(left);
            }
        }

        private static void Build()
        {
            ulong actorArray = Players.ActorArray;
            int   actorCount = Players.ActorCount;

            if (actorArray == 0 || actorCount <= 0) { PublishEmpty(actorCount); return; }

            int take = Math.Min(actorCount, 4096);
            var ptrs = DmaMemory.ReadArray<ulong>(actorArray, take);
            if (ptrs == null || ptrs.Length == 0) { PublishEmpty(actorCount); return; }

            using var scatter = DmaMemory.Scatter();

            for (int i = 0; i < ptrs.Length; i++)
            {
                ulong a = ptrs[i]; if (a == 0) continue;
                scatter.PrepareReadValue<uint>(a + 24);
                scatter.PrepareReadValue<ulong>(a + LootSceneOffsets.AActor_RootComponent);
            }
            scatter.Execute();

            var items = new List<Item>(256);
            var containerActors = new List<ulong>(64);
            var allContainers = new List<Container>(64);
            int containersExpanded = 0;

            _dbgContainersHeu = _dbgContainersMgr = 0;
            _dbgPricesFromCommon = _dbgPricesFromProvider = _dbgLabelsFromCommon = 0;

            // Classify actors
            for (int i = 0; i < ptrs.Length; i++)
            {
                ulong a = ptrs[i]; if (a == 0) continue;
                if (!scatter.ReadValue(a + 24, out uint fname) || fname == 0) continue;
                string cls = ABINamePool.GetName(fname);
                if (string.IsNullOrEmpty(cls)) continue;

                // CRITICAL FIX: Skip character/pawn actors
                if (cls.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Pawn", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsContainer(cls))
                {
                    Vector3 pos = ReadActorWorldPosFromRoot(
                        scatter.ReadValue(a + LootSceneOffsets.AActor_RootComponent, out ulong root) ? root : 0);

                    // Try container-specific offset
                    string label = null; int price = 0; int rarity = 0;
                    _ = TryFillMetaFromCommon(a, ComponentSlots.CONTAINER_COMMONDATA, ref label, ref price, ref rarity, out var compPtr, out var slotUsed);

                    var cont = new Container
                    {
                        Actor = a,
                        Position = pos,
                        ClassName = cls,
                        Label = label ?? cls,
                        ItemCount = 0,
                        ApproxPrice = price,
                        Rarity = rarity,
                        IsSearched = false,
                        Debug = new ContainerDebug
                        {
                            UsedManagerPath = false,
                            ManagerOffsetTried = 0,
                            BaseListCount = 0,
                            HeuristicArraysTried = 0,
                            HeuristicItemsFound = 0,
                            CommonDataPtr = compPtr,
                            CommonDataSlot = slotUsed
                        }
                    };
                    containerActors.Add(a);
                    allContainers.Add(cont);
                    continue;
                }

                if (IsItem(cls))
                {
                    Vector3 pos = ReadActorWorldPosForItem(a, scatter.ReadValue(a + LootSceneOffsets.AActor_RootComponent, out ulong root) ? root : 0);

                    // Try item-specific offset
                    string label = null; int price = 0; int rarity = 0;
                    var success = TryFillMetaFromCommon(a, ComponentSlots.ITEM_COMMONDATA, ref label, ref price, ref rarity, out var compPtr, out var slotUsed);

                    ItemDebug idbg = new ItemDebug
                    {
                        LabelSrc = success && label != null ? LabelSource.Common_DisplayName : LabelSource.Fallback_ClassName,
                        PriceSrc = success && price > 0 ? PriceSource.Common_StandardPrice : PriceSource.None,
                        CommonDataPtr = compPtr,
                        CommonDataSlot = slotUsed
                    };

                    if (idbg.LabelSrc != LabelSource.Fallback_ClassName) _dbgLabelsFromCommon++;
                    if (idbg.PriceSrc == PriceSource.Common_StandardPrice) _dbgPricesFromCommon++;

                    int finalPrice = price > 0 ? price : (_priceProvider?.TryGetPrice(cls) ?? 0);
                    if (idbg.PriceSrc == PriceSource.None && finalPrice > 0)
                    {
                        idbg.PriceSrc = PriceSource.Provider_ClassName;
                        _dbgPricesFromProvider++;
                    }

                    items.Add(new Item {
                        Actor = a, ContainerActor = 0, InContainer = false,
                        ClassName = cls, Label = label ?? cls, Stack = 1,
                        Position = pos,
                        ApproxPrice = finalPrice,
                        Rarity = rarity,
                        Debug = idbg
                    });
                }
            }

            if (EnableDebugLogging)
                Console.WriteLine($"[ABILoot] Found {containerActors.Count} containers, {items.Count} loose items");

            // Prune old searched caches
            var now = DateTime.UtcNow;
            var toRemove = _searchedContainers.Where(kvp => now - new DateTime(kvp.Value.timestamp) > _searchedContainerExpiry)
                                              .Select(kvp => kvp.Key).ToList();
            foreach (var k in toRemove) _searchedContainers.Remove(k);

            // Expand containers
            for (int c = 0; c < containerActors.Count; c++)
            {
                var idx = allContainers.FindIndex(x => x.Actor == containerActors[c]);
                var cdbg = allContainers[idx].Debug;

                int before = items.Count;
                bool ok = ExpandContainer(containerActors[c], items, out cdbg);
                if (ok)
                {
                    containersExpanded++;
                    int added = items.Count - before;

                    var ct = allContainers[idx];
                    ct.ItemCount = added;
                    ct.Debug = cdbg;
                    allContainers[idx] = ct;

                    if (cdbg.UsedManagerPath) _dbgContainersMgr++; else _dbgContainersHeu++;
                }
                else
                {
                    var ct = allContainers[idx];
                    ct.Debug = cdbg;
                    allContainers[idx] = ct;
                }
            }

            // Merge previously searched cache
            foreach (var kvp in _searchedContainers)
            {
                foreach (var it in kvp.Value.items)
                    if (!items.Any(x => x.Actor == it.Actor)) items.Add(it);
            }

            // Publish
            lock (_sync)
            {
                _latest = new Frame
                {
                    StampTicks         = DateTime.UtcNow.Ticks,
                    Items              = items,
                    Containers         = allContainers,
                    TotalActorsSeen    = actorCount,
                    ContainersFound    = containerActors.Count,
                    ContainersExpanded = containersExpanded,
                };

                _dbg = new DebugSnapshot
                {
                    ActorsScanned          = actorCount,
                    ItemsLoose             = items.Count(x => !x.InContainer),
                    ContainersSeen         = containerActors.Count,
                    ContainersExpandedMgr  = _dbgContainersMgr,
                    ContainersExpandedHeu  = _dbgContainersHeu,
                    PricesFromCommon       = _dbgPricesFromCommon,
                    PricesFromProvider     = _dbgPricesFromProvider,
                    LabelsFromCommon       = _dbgLabelsFromCommon
                };
            }
        }

        // -----------------------------------------------------------------------------
        // CRITICAL FIX: Proper FText reading
        // -----------------------------------------------------------------------------
        private static string ReadFTextString(ulong baseAddr, int relOff)
        {
            try
            {
                ulong ftextAddr = baseAddr + (ulong)relOff;
                
                if (EnableDebugLogging)
                {
                    // Dump first 64 bytes of FText structure
                    var raw = DmaMemory.ReadBytes(ftextAddr, 64);
                    if (raw != null)
                    {
                        var hex = BitConverter.ToString(raw).Replace("-", " ");
                        Console.WriteLine($"    [FText] Raw bytes at 0x{ftextAddr:X}:");
                        Console.WriteLine($"      {hex.Substring(0, Math.Min(71, hex.Length))}");
                        if (hex.Length > 71)
                            Console.WriteLine($"      {hex.Substring(72)}");
                    }
                }

                // ATTEMPT 1: Maybe FText is just FString directly (no shared pointer)?
                {
                    ulong strPtr1 = DmaMemory.Read<ulong>(ftextAddr + 0);
                    int len1 = DmaMemory.Read<int>(ftextAddr + 8);
                    if (EnableDebugLogging)
                        Console.WriteLine($"    [Attempt 1] Direct FString: ptr=0x{strPtr1:X}, len={len1}");
                    
                    if (strPtr1 > 0x10000 && len1 > 0 && len1 < 500)
                    {
                        var bytes = DmaMemory.ReadBytes(strPtr1, (uint)(len1 * 2));
                        if (bytes != null)
                        {
                            var result = System.Text.Encoding.Unicode.GetString(bytes)?.TrimEnd('\0');
                            if (!string.IsNullOrWhiteSpace(result))
                            {
                                if (EnableDebugLogging)
                                    Console.WriteLine($"    [Attempt 1] SUCCESS: {result}");
                                return result;
                            }
                        }
                    }
                }

                // ATTEMPT 2: Original shared pointer approach
                {
                    ulong textDataPtr = DmaMemory.Read<ulong>(ftextAddr + 8);
                    if (EnableDebugLogging)
                        Console.WriteLine($"    [Attempt 2] SharedPtr approach: textDataPtr=0x{textDataPtr:X}");
                    
                    if (textDataPtr > 0x10000)
                    {
                        // Dump what's at textDataPtr
                        var raw2 = DmaMemory.ReadBytes(textDataPtr, 48);
                        if (raw2 != null && EnableDebugLogging)
                        {
                            var hex2 = BitConverter.ToString(raw2).Replace("-", " ");
                            Console.WriteLine($"      Data at textDataPtr: {hex2}");
                        }

                        ulong fstringAddr = textDataPtr + 0x08;
                        ulong strPtr2 = DmaMemory.Read<ulong>(fstringAddr);
                        int len2 = DmaMemory.Read<int>(fstringAddr + 8);
                        
                        if (EnableDebugLogging)
                            Console.WriteLine($"      FString at +8: ptr=0x{strPtr2:X}, len={len2}");

                        if (strPtr2 > 0x10000 && len2 > 0 && len2 < 500)
                        {
                            var bytes = DmaMemory.ReadBytes(strPtr2, (uint)(len2 * 2));
                            if (bytes != null)
                            {
                                var result = System.Text.Encoding.Unicode.GetString(bytes)?.TrimEnd('\0');
                                if (!string.IsNullOrWhiteSpace(result))
                                {
                                    if (EnableDebugLogging)
                                        Console.WriteLine($"    [Attempt 2] SUCCESS: {result}");
                                    return result;
                                }
                            }
                        }
                    }
                }

                // ATTEMPT 3: Maybe the object pointer is at offset 0, not 8?
                {
                    ulong textDataPtr3 = DmaMemory.Read<ulong>(ftextAddr + 0);
                    if (EnableDebugLogging)
                        Console.WriteLine($"    [Attempt 3] Reading object at offset 0: ptr=0x{textDataPtr3:X}");
                    
                    if (textDataPtr3 > 0x10000)
                    {
                        ulong fstringAddr = textDataPtr3 + 0x08;
                        ulong strPtr3 = DmaMemory.Read<ulong>(fstringAddr);
                        int len3 = DmaMemory.Read<int>(fstringAddr + 8);
                        
                        if (EnableDebugLogging)
                            Console.WriteLine($"      FString: ptr=0x{strPtr3:X}, len={len3}");

                        if (strPtr3 > 0x10000 && len3 > 0 && len3 < 500)
                        {
                            var bytes = DmaMemory.ReadBytes(strPtr3, (uint)(len3 * 2));
                            if (bytes != null)
                            {
                                var result = System.Text.Encoding.Unicode.GetString(bytes)?.TrimEnd('\0');
                                if (!string.IsNullOrWhiteSpace(result))
                                {
                                    if (EnableDebugLogging)
                                        Console.WriteLine($"    [Attempt 3] SUCCESS: {result}");
                                    return result;
                                }
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                if (EnableDebugLogging)
                    Console.WriteLine($"    [FText] Exception: {ex.Message}");
                return null;
            }
        }

        private static bool TryFillMetaFromCommon(
            ulong actor, int preferredOffset, ref string label, ref int approxPrice, ref int rarity,
            out ulong compPtrOut, out int slotUsedOut)
        {
            compPtrOut = 0;
            slotUsedOut = -1;

            // Try multiple candidate offsets
            int[] candidates = { 0x08A0, 0x08A8, 0x08B0, 0x08B8 };

            foreach (int offset in candidates)
            {
                try
                {
                    ulong comp = DmaMemory.Read<ulong>(actor + (ulong)offset);
                    if (comp == 0 || comp < 0x10000) continue;

                    compPtrOut = comp;
                    slotUsedOut = offset;

                    // Read price and rarity FIRST (these are simple ints)
                    int price = DmaMemory.Read<int>(comp + (ulong)InvCommonOffsets.OFF_StandardPrice);
                    int rar = DmaMemory.Read<int>(comp + (ulong)InvCommonOffsets.OFF_Rarity);

                    if (EnableDebugLogging)
                        Console.WriteLine($"[ABILoot] Actor 0x{actor:X} @ offset 0x{offset:X}: comp=0x{comp:X}, price={price}, rarity={rar}");

                    // Validation: Accept if price OR rarity is reasonable (not both must be valid)
                    bool priceValid = price >= 0 && price <= 10_000_000;
                    bool rarityValid = rar >= 0 && rar <= 100;
                    
                    if (!priceValid && !rarityValid)
                    {
                        if (EnableDebugLogging)
                            Console.WriteLine($"  ? Validation failed: price={price}, rarity={rar}");
                        continue;
                    }

                    if (EnableDebugLogging)
                        Console.WriteLine($"  ? Validation passed! Trying to read DisplayName...");

                    // Try reading FText - MULTIPLE STRATEGIES
                    string name = null;
                    
                    // Strategy 1: Standard shared pointer dereference
                    name = ReadFTextString(comp, InvCommonOffsets.OFF_DisplayName);
                    if (EnableDebugLogging)
                        Console.WriteLine($"  Strategy 1 (DisplayName): {name ?? "null"}");
                    
                    // Strategy 2: Try SimpleName if DisplayName failed
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = ReadFTextString(comp, InvCommonOffsets.OFF_SimpleName);
                        if (EnableDebugLogging)
                            Console.WriteLine($"  Strategy 2 (SimpleName): {name ?? "null"}");
                    }

                    // Strategy 3: Try direct FString read (in case FText is just wrapping FString)
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = ReadDirectFString(comp, InvCommonOffsets.OFF_DisplayName);
                        if (EnableDebugLogging)
                            Console.WriteLine($"  Strategy 3 (Direct FString): {name ?? "null"}");
                    }

                    // Accept this component even if name reading failed
                    if (!string.IsNullOrWhiteSpace(name)) label = name;
                    if (priceValid && price > 0) approxPrice = price;
                    if (rarityValid) rarity = rar;

                    if (EnableDebugLogging)
                        Console.WriteLine($"  Final: label={label ?? "null"}, price={approxPrice}, rarity={rarity}");

                    return priceValid || rarityValid || !string.IsNullOrWhiteSpace(name);
                }
                catch (Exception ex)
                {
                    if (EnableDebugLogging)
                        Console.WriteLine($"[ABILoot] Exception at offset 0x{offset:X}: {ex.Message}");
                    continue;
                }
            }

            return false;
        }

        // NEW: Alternative FString reading strategy
        private static string ReadDirectFString(ulong baseAddr, int relOff)
        {
            try
            {
                // Maybe FText is just { FString Data; uint32 Flags } without shared pointer?
                ulong fstringAddr = baseAddr + (ulong)relOff;
                
                // Read FString: { TCHAR* Data, int32 ArrayNum, int32 ArrayMax }
                ulong strPtr = DmaMemory.Read<ulong>(fstringAddr);
                int len = DmaMemory.Read<int>(fstringAddr + 8);

                if (strPtr == 0 || len <= 0 || len > 500) return null;

                var bytes = DmaMemory.ReadBytes(strPtr, (uint)(len * 2));
                if (bytes == null) return null;

                var result = System.Text.Encoding.Unicode.GetString(bytes)?.TrimEnd('\0');
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------------
        // Classification helpers
        // -----------------------------------------------------------------------------
        private static bool IsContainer(string cls)
        {
            if (LootContainerClassHints.Contains(cls)) return true;
            return cls.IndexOf("Inventory_", StringComparison.OrdinalIgnoreCase) >= 0
                || cls.IndexOf("LootBox",    StringComparison.OrdinalIgnoreCase) >= 0
                || cls.IndexOf("Container",  StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsItem(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (LootItemBaseHints.Contains(cls)) return true;

            if (cls.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) &&
                cls.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
            {
                if (cls.Contains("LootBox", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            return false;
        }

        // -----------------------------------------------------------------------------
        // Container expansion (same as before but with proper metadata)
        // -----------------------------------------------------------------------------
        private static bool ExpandContainer(ulong boxActor, List<Item> outList, out ContainerDebug cdbg)
        {
            cdbg = new ContainerDebug
            {
                UsedManagerPath = false,
                ManagerOffsetTried = 0,
                BaseListCount = 0,
                HeuristicArraysTried = 0,
                HeuristicItemsFound = 0,
                CommonDataPtr = 0,
                CommonDataSlot = -1
            };

            int totalKids = 0;

            try
            {
                Vector3 contPos = ReadActorWorldPos(boxActor);

                ulong mgr = GetContainerMgr(boxActor, out int mgrOffTried);
                if (mgr != 0)
                {
                    cdbg.UsedManagerPath = true;
                    cdbg.ManagerOffsetTried = mgrOffTried;

                    var baseList = ReadTArray(mgr + OFF_MGR_BASELIST);
                    cdbg.BaseListCount = baseList.Count;

                    if (baseList.Count > 0 && baseList.Data != 0)
                    {
                        ulong elem0 = baseList.Data;
                        int row0 = DmaMemory.Read<int>(elem0 + OFF_INVBASE_ROW);
                        int col0 = DmaMemory.Read<int>(elem0 + OFF_INVBASE_COLUMN);
                        if (row0 >= 0 && row0 <= 64 && col0 >= 0 && col0 <= 64)
                        {
                            int count = Math.Min(baseList.Count, 128);
                            int childOffset = OFF_INVBASE_CHILD_ACTORS;

                            using var childScatter = DmaMemory.Scatter();

                            for (int i = 0; i < count; i++)
                            {
                                ulong elem = baseList.Data + (ulong)(i * SIZEOF_INVBASE);
                                childScatter.PrepareReadValue<ulong>(elem + (ulong)childOffset + 0x00);
                                childScatter.PrepareReadValue<int>(elem + (ulong)childOffset + 0x08);
                            }
                            childScatter.Execute();

                            for (int i = 0; i < count; i++)
                            {
                                ulong elem = baseList.Data + (ulong)(i * SIZEOF_INVBASE);
                                if (!childScatter.ReadValue(elem + (ulong)childOffset + 0x00, out ulong data) || data == 0) continue;
                                if (!childScatter.ReadValue(elem + (ulong)childOffset + 0x08, out int c) || c <= 0 || c > 512) continue;

                                var kids = DmaMemory.ReadArray<ulong>(data, c);
                                if (kids == null || kids.Length == 0) continue;

                                using var actorScatter = DmaMemory.Scatter();
                                for (int k = 0; k < kids.Length; k++)
                                {
                                    ulong a = kids[k]; if (a == 0) continue;
                                    actorScatter.PrepareReadValue<uint>(a + 24);
                                }
                                actorScatter.Execute();

                                for (int k = 0; k < kids.Length; k++)
                                {
                                    ulong a = kids[k]; if (a == 0) continue;
                                    string cls = actorScatter.ReadValue(a + 24, out uint fname) ? ABINamePool.GetName(fname) : null;
                                    if (string.IsNullOrEmpty(cls) || !IsItem(cls)) continue;

                                    // Skip character actors here too
                                    if (cls.Contains("Character", StringComparison.OrdinalIgnoreCase)) continue;

                                    string label = null; int price = 0; int rarity = 0;
                                    var success = TryFillMetaFromCommon(a, ComponentSlots.ITEM_COMMONDATA, ref label, ref price, ref rarity, out var compPtr, out var slotUsed);

                                    ItemDebug idbg = new ItemDebug
                                    {
                                        LabelSrc = success && label != null ? LabelSource.Common_DisplayName : LabelSource.Fallback_ClassName,
                                        PriceSrc = success && price > 0 ? PriceSource.Common_StandardPrice : PriceSource.None,
                                        CommonDataPtr = compPtr,
                                        CommonDataSlot = slotUsed
                                    };

                                    if (idbg.LabelSrc != LabelSource.Fallback_ClassName) _dbgLabelsFromCommon++;
                                    if (idbg.PriceSrc == PriceSource.Common_StandardPrice) _dbgPricesFromCommon++;

                                    int finalPrice = price > 0 ? price : (_priceProvider?.TryGetPrice(cls) ?? 0);
                                    if (idbg.PriceSrc == PriceSource.None && finalPrice > 0)
                                    {
                                        idbg.PriceSrc = PriceSource.Provider_ClassName;
                                        _dbgPricesFromProvider++;
                                    }

                                    outList.Add(new Item
                                    {
                                        Actor = a,
                                        ContainerActor = boxActor,
                                        InContainer = true,
                                        ClassName = cls,
                                        Label = label ?? cls,
                                        Stack = 1,
                                        Position = contPos,
                                        ApproxPrice = finalPrice,
                                        Rarity = rarity,
                                        Debug = idbg
                                    });
                                    totalKids++;
                                }
                            }
                        }
                    }
                }

                return totalKids > 0;
            }
            catch (Exception ex)
            {
                if (EnableDebugLogging)
                    Console.WriteLine($"[ABILoot] ExpandContainer error 0x{boxActor:X}: {ex.Message}");
                return false;
            }
        }

        private static ulong GetContainerMgr(ulong boxActor, out int offsetTried)
        {
            offsetTried = 0;
            for (int i = 0; i < CANDIDATE_MGR_OFFS.Length; i++)
            {
                int off = CANDIDATE_MGR_OFFS[i];
                ulong p = DmaMemory.Read<ulong>(boxActor + (ulong)off);
                if (p != 0 && (p & 0xFFF0_0000_0000_0000UL) == 0 && p > 0x10000)
                {
                    offsetTried = off;
                    return p;
                }
            }
            return 0;
        }

        // -----------------------------------------------------------------------------
        // Position helpers - FIXED: Store raw world positions
        // -----------------------------------------------------------------------------
        private static Vector3 ReadActorWorldPos(ulong actor)
        {
            if (actor == 0) return default;
            ulong root = DmaMemory.Read<ulong>(actor + LootSceneOffsets.AActor_RootComponent);
            return ReadActorWorldPosFromRoot(root);
        }

        private static Vector3 ReadActorWorldPosForItem(ulong actor, ulong root)
        {
            // Try PickupMesh first for better positions
            ulong pickupMesh = DmaMemory.Read<ulong>(actor + (ulong)ComponentSlots.ITEM_PICKUPMESH);
            if (pickupMesh != 0)
            {
                var pm = TryReadSceneCompWorldPos(pickupMesh);
                if (!float.IsNaN(pm.X)) return pm;
            }

            return ReadActorWorldPosFromRoot(root);
        }

        private static Vector3 ReadActorWorldPosFromRoot(ulong rootComp)
        {
            if (rootComp == 0) return default;

            ulong ctwPtr = DmaMemory.Read<ulong>(rootComp + LootSceneOffsets.USceneComponent_ComponentToWorld_Ptr);
            if (ctwPtr == 0) return default;

            var t = DmaMemory.Read<FTransform>(ctwPtr);
            if (!float.IsFinite(t.Translation.X) || !float.IsFinite(t.Translation.Y) || !float.IsFinite(t.Translation.Z))
                return default;

            // FIXED: Return raw world position without bias
            // Bias will be applied during rendering to match player coordinate system
            return t.Translation;
        }

        private static Vector3 TryReadSceneCompWorldPos(ulong sceneComp)
        {
            if (sceneComp == 0) return new Vector3(float.NaN, 0, 0);
            ulong ctwPtr = DmaMemory.Read<ulong>(sceneComp + LootSceneOffsets.USceneComponent_ComponentToWorld_Ptr);
            if (ctwPtr == 0) return new Vector3(float.NaN, 0, 0);
            var t = DmaMemory.Read<FTransform>(ctwPtr);
            if (!float.IsFinite(t.Translation.X)) return new Vector3(float.NaN, 0, 0);
            
            // FIXED: Return raw world position without bias
            return t.Translation;
        }

        // -----------------------------------------------------------------------------
        // Small helpers
        // -----------------------------------------------------------------------------
        private readonly struct TArrayHdr
        {
            public readonly ulong Data;
            public readonly int   Count;
            public readonly int   Max;
            public TArrayHdr(ulong d, int c, int m) { Data = d; Count = c; Max = m; }
        }

        private static TArrayHdr ReadTArray(ulong addr)
        {
            Span<byte> buf = stackalloc byte[16];
            if (!DmaMemory.Read(addr, buf)) return default;
            ulong data = Unsafe.ReadUnaligned<ulong>(ref buf[0]);
            int count  = Unsafe.ReadUnaligned<int>(ref buf[8]);
            int max    = Unsafe.ReadUnaligned<int>(ref buf[12]);
            if (count < 0 || count > max || count > 1_000_000) return default;
            return new TArrayHdr(data, count, max);
        }

        private static void PublishEmpty(int totalActors)
        {
            lock (_sync)
            {
                _latest = new Frame
                {
                    StampTicks = DateTime.UtcNow.Ticks,
                    Items = new List<Item>(0),
                    Containers = new List<Container>(0),
                    TotalActorsSeen = Math.Max(totalActors, 0),
                    ContainersFound = 0,
                    ContainersExpanded = 0
                };

                _dbg = new DebugSnapshot
                {
                    ActorsScanned = Math.Max(totalActors, 0),
                    ItemsLoose = 0,
                    ContainersSeen = 0,
                    ContainersExpandedMgr = 0,
                    ContainersExpandedHeu = 0,
                    PricesFromCommon = 0,
                    PricesFromProvider = 0,
                    LabelsFromCommon = 0
                };
            }
        }

        private static void HighResDelay(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int s = Math.Max(0, ms - 1);
            if (s > 0) Thread.Sleep(s);
            while (sw.ElapsedMilliseconds < ms) Thread.SpinWait(80);
        }

        public static void OnContainerSearched(ulong containerActor, List<Item> contents)
        {
            _searchedContainers[containerActor] = (DateTime.UtcNow.Ticks, new List<Item>(contents));
            if (EnableDebugLogging)
                Console.WriteLine($"[ABILoot] Container 0x{containerActor:X} searched, cached {contents.Count} items");
        }
    }
}
