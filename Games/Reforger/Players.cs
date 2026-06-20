using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MamboDMA;
using MamboDMA.Games.Reforger;

namespace ArmaReforgerFeeder
{
    public static class Players
    {
        public static bool DebugLogDrawn = false;
        public static int DebugLogDrawnEveryMs = 1000;
        public static bool DebugLogTypePointers = true;
        public static bool DebugLogSlow = false;

        private struct DebugTypeRecord
        {
            public string Type;
            public string ClassShort;
            public ulong PrefabMgr;
            public ulong TypeClass;
            public ulong TypePtr;
            public ulong AnimComp;
            public ulong ExtDamageMgr;
            public ulong HitZone;
            public ulong MeshComp;
            public ulong MeshObj;
        }

        private static readonly ConcurrentDictionary<ulong, DebugTypeRecord> _dbgTypeByEnt = new();

        public enum CharacterFilterMode { HumanoidHeuristic, ChimeraPreferred, ChimeraOnly }

        public static CharacterFilterMode FilterMode = CharacterFilterMode.HumanoidHeuristic;
        public static bool IncludeFriendlies = false;
        public static bool OnlyPlayersFromPlayerManager = false;
        public static bool RequireHitZones = true;
        public static bool IncludeRagdolls = false;
        public static bool AnimatedOnly = true;
        public static bool RequireKnownStance = false;

        public static float MaxDrawDistance = 350f;
        public static int FrameCap = 256;
        public static int MaxEntitiesToScan = 32768;

        public static int FastIntervalMs = 3;
        public static int HpIntervalMs = 35;
        public static int SlowIntervalMs = 140;

        public static bool EnableSkeletons = false;
        public enum SkeletonDetail { Compact6 = 6, Lite10 = 10, Baller14 = 14 }
        public static SkeletonDetail SkeletonLevel = SkeletonDetail.Lite10;
        public static float SkeletonThickness = 1.6f;

        public static bool ShowWeaponBelowName = true;
        public static int TypeStringMax = 64;
        public static int FactionStringMax = 64;

        private const float PersonHeightMeters = 1.75f;
        private const float PersonWidthAspect = 0.38f;
        private const float BoxMinHPx = 24f;
        private const float BoxMaxHPx = 220f;

        public static float ItemAttachRadiusM = 1.20f;
        public static float ItemAttachHysteresisM = 1.80f;
        public static bool OwnOnlyWeaponsForNow = true;
        public static bool ExcludeGrenadesInWeapon = true;

        public static Action<ActorDto[], float, float>? UiSink;

        public struct AnchorInfo
        {
            public ulong Ptr;
            public Vector3f Core;
            public Vector3f Head;
            public string Name;
        }

        private static volatile AnchorInfo[] _anchors = Array.Empty<AnchorInfo>();
        public static AnchorInfo[] LatestAnchors => Volatile.Read(ref _anchors);

        private static readonly ConcurrentDictionary<ulong, string> _equippedWeapon = new();
        private static readonly ConcurrentDictionary<ulong, List<string>> _ownedItems = new();
        private static readonly ConcurrentDictionary<ulong, ulong> _itemOwner = new();

        public static bool TryGetEquippedWeapon(ulong playerPtr, out string weapon) => _equippedWeapon.TryGetValue(playerPtr, out weapon);
        public static IReadOnlyDictionary<ulong, List<string>> OwnedItemsByPlayer => _ownedItems;

        private static readonly (int a, int b)[] SkelEdges6 = { (0,1),(1,2),(2,3),(3,4),(3,5) };
        private static readonly (int a, int b)[] SkelEdges10 = { (0,1),(1,2),(2,3), (2,4),(2,5), (3,6),(6,8), (3,7),(7,9) };
        private static readonly (int a, int b)[] SkelEdges14 = { (0,1),(1,2),(2,3), (2,4),(4,5),(5,6), (2,7),(7,8),(8,9), (3,10),(10,11), (3,12),(12,13) };

        public static (int a, int b)[] ActiveSkelEdges =>
            SkeletonLevel == SkeletonDetail.Baller14 ? SkelEdges14 :
            SkeletonLevel == SkeletonDetail.Lite10 ? SkelEdges10 : SkelEdges6;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ClassName(string type)
        {
            if (string.IsNullOrEmpty(type)) return string.Empty;
            int cut = type.LastIndexOfAny(new[] { '/', '\\', ':' });
            return (cut >= 0) ? type[(cut + 1)..] : type;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ClassEquals(string type, string className) => ClassName(type).Equals(className, StringComparison.OrdinalIgnoreCase);

        private static readonly string[] HumanoidNameTokens = { "character", "soldier", "human", "man", "unit", "infantry", "player" };
        private static readonly string[] NonHumanoidNameTokens = {
            "vehicle", "car", "truck", "heli", "helicopter", "plane", "boat", "ship", "turret", "gun", "static", "prop",
            "crate", "container", "box", "ammobox", "magazine", "building", "door", "fence", "decal", "effect", "particle",
            "gameentity", "clothing", "cloth", "apparel", "equipment", "helmet", "hat", "cap", "mask", "goggle", "balaclava",
            "vest", "carrier", "chestrig", "plate", "armor", "jacket", "coat", "shirt", "pants", "trouser", "shorts", "boots",
            "shoes", "gloves", "backpack", "bag", "pouch", "holster", "sheath", "belt", "scope", "optic", "suppressor",
            "silencer", "stock", "animal", "fauna", "bird", "avian", "crow", "seagull", "ragdoll", "corpse", "gib"
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsAny(string s, string[] toks)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < toks.Length; i++)
                if (s.IndexOf(toks[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCharacterish(string type) => ContainsAny(type, HumanoidNameTokens) && !ContainsAny(type, NonHumanoidNameTokens);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsChimeraStrict(string type)
        {
            var cls = ClassName(type);
            return cls.Equals("ChimeraCharacter", StringComparison.OrdinalIgnoreCase) || cls.Equals("SCR_ChimeraCharacter", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasHumanoidSkeleton(ulong ent, string type)
        {
            return Bones.EnsureIndicesForModel(ent, type ?? "", out var map) && map.Length > 0 && map[0] >= 0 &&
                   ((map.Length > 3 && map[3] >= 0) || (map.Length > 5 && map[4] >= 0 && map[5] >= 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3f MulPoint(in Matrix3x4 parent, in Matrix3x4 bone)
        {
            float x = bone.m09, y = bone.m10, z = bone.m11;
            return new Vector3f(
                parent.m00 * x + parent.m03 * y + parent.m06 * z + parent.m09,
                parent.m01 * x + parent.m04 * y + parent.m07 * z + parent.m10,
                parent.m02 * x + parent.m05 * y + parent.m08 * z + parent.m11);
        }

        private static bool IsZero(in Matrix3x4 m) =>
            m.m00 == 0 && m.m01 == 0 && m.m02 == 0 && m.m03 == 0 && m.m04 == 0 && m.m05 == 0 &&
            m.m06 == 0 && m.m07 == 0 && m.m08 == 0 && m.m09 == 0 && m.m10 == 0 && m.m11 == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double NowSeconds() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        private sealed class MetaSnapshot
        {
            public readonly ulong[] Ents;
            public readonly string[] Types;
            public readonly string[] Factions;
            public readonly ulong[] HitZone;
            public readonly string[] Names;
            public int Count => Ents?.Length ?? 0;

            public MetaSnapshot(ulong[] e, string[] t, string[] f, ulong[] hz, string[] names)
            { Ents = e; Types = t; Factions = f; HitZone = hz; Names = names; }

            public static readonly MetaSnapshot Empty = new(Array.Empty<ulong>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ulong>(), Array.Empty<string>());
        }

        private sealed class FrameSnapshot
        {
            public readonly ActorDto[] Actors;
            public FrameSnapshot(ActorDto[] a) { Actors = a; }
            public static readonly FrameSnapshot Empty = new(Array.Empty<ActorDto>());
        }

        private static MetaSnapshot _meta = MetaSnapshot.Empty;
        private static FrameSnapshot _frame = FrameSnapshot.Empty;
        private static readonly ConcurrentDictionary<ulong, float> _hp = new();
        private static readonly ConcurrentDictionary<ulong, bool> _dead = new();

        private struct VelHist { public Vector3f Pos; public double T; public Vector3f Vel; }
        private static readonly ConcurrentDictionary<ulong, VelHist> _vel = new();

        private static Thread? _fastT, _hpT, _slowT;
        private static volatile bool _run;

        public static void StartWorkers()
        {
            if (_run) return;
            _run = true;
            TimerResolution.Enable1ms();
            _slowT = new Thread(SlowLoop) { IsBackground = true, Name = "Players.Slow", Priority = ThreadPriority.BelowNormal };
            _hpT = new Thread(HpLoop) { IsBackground = true, Name = "Players.HP", Priority = ThreadPriority.AboveNormal };
            _fastT = new Thread(FastLoop) { IsBackground = true, Name = "Players.Fast", Priority = ThreadPriority.Highest };
            _slowT.Start();
            _hpT.Start();
            _fastT.Start();
        }

        public static void StopWorkers()
        {
            _run = false;
            try { _fastT?.Join(150); _hpT?.Join(150); _slowT?.Join(150); } catch { }
            TimerResolution.Disable1ms();
        }

        internal static class TimerResolution
        {
            [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
            [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
            public static void Enable1ms() { try { timeBeginPeriod(1); } catch { } }
            public static void Disable1ms() { try { timeEndPeriod(1); } catch { } }
        }

        public static void PublishLatestToUI()
        {
            var fr = Volatile.Read(ref _frame);
            var actors = fr?.Actors ?? Array.Empty<ActorDto>();
            UiSink?.Invoke(actors, Game.Screen.W, Game.Screen.H);
        }

        public static void ResetSession(bool hard, string why)
        {
            Console.WriteLine($"[Players] ResetSession(hard: {hard}) due to: {why}");
            _vel.Clear();
            _hp.Clear();
            _dead.Clear();
            Bones.ClearCache();
            EntityListManager.ResetCache();
            _equippedWeapon.Clear();
            _ownedItems.Clear();
            foreach (var kv in _itemOwner.ToArray()) _itemOwner.TryRemove(kv.Key, out _);
            ExchangeMeta(MetaSnapshot.Empty);
            ExchangeFrame(FrameSnapshot.Empty);
            PublishAnchors(Array.Empty<AnchorInfo>());
            Game.Reset();
            StopWorkers();
            StartWorkers();
        }

        private static string TryResolveTypeLive(ulong ent, out ulong prefabMgr, out ulong typeClass, out ulong typePtr)
        {
            prefabMgr = typeClass = typePtr = 0;
            if (DmaMemory.Read(ent + Off.PrefabMgr, out prefabMgr) && prefabMgr != 0 &&
                DmaMemory.Read(prefabMgr + Off.PrefabDataClass, out typeClass) && typeClass != 0 &&
                DmaMemory.Read(typeClass + Off.PrefabDataType, out typePtr) && typePtr != 0)
            {
                var s = DmaMemory.ReadString(typePtr, TypeStringMax, Encoding.ASCII) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(s))
                    s = DmaMemory.ReadString(typePtr, TypeStringMax, Encoding.UTF8) ?? string.Empty;
                return s;
            }
            return string.Empty;
        }

        private static void SlowLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            int dbgTick = 0;

            while (_run)
            {
                sw.Restart();
                //Game.UpdateCamera();

                if (!DmaMemory.Read(DmaMemory.Base + Off.Game, out ulong game) || game == 0) goto SleepSlow;
                if (!DmaMemory.Read(game + Off.GameWorld, out ulong gw) || gw == 0) goto SleepSlow;

                string localFaction = TryGetLocalFaction(gw);
                Console.WriteLine($"[DEBUG] game=0x{game:X}, gw=0x{gw:X}");
                // NEW: Use EntityListManager for character-specific list
                var ents = EntityListManager.GetAllEntitiesForType(gw, EntityListManager.EntityType.Character, MaxEntitiesToScan);

                if (ents.Length == 0) 
                { 
                    ExchangeMeta(MetaSnapshot.Empty); 
                    goto SleepSlow; 
                }

                if (ents.Length == 0) { ExchangeMeta(MetaSnapshot.Empty); goto SleepSlow; }

                var posTmp = new Vector3f[ents.Length];
                using (var scatter = DmaMemory.Scatter())
                {
                    for (int i = 0; i < ents.Length; i++)
                    {
                        ulong e = ents[i];
                        if (e == 0) continue;
                        scatter.PrepareReadValue<Vector3f>(e + Off.EntityPosition);
                    }
                    scatter.Execute();
                    for (int i = 0; i < ents.Length; i++)
                    {
                        ulong e = ents[i];
                        if (e != 0)
                            scatter.ReadValue(e + Off.EntityPosition, out posTmp[i]);
                    }
                }

                var keep = new List<int>(ents.Length);
                var cam = Game.Camera.Position;
                bool camSane = !float.IsNaN(cam.X) && !float.IsNaN(cam.Y) && !float.IsNaN(cam.Z) &&
                    !(cam.X == 0 && cam.Y == 0 && cam.Z == 0) && Game.Camera.Fov > 1f && Game.Camera.Fov < 179f;

                float maxSq = MaxDrawDistance * MaxDrawDistance;

                for (int i = 0; i < ents.Length; i++)
                {
                    if (ents[i] == 0) continue;
                    if (!camSane) { keep.Add(i); continue; }
                    float dx = posTmp[i].X - cam.X, dy = posTmp[i].Y - cam.Y, dz = posTmp[i].Z - cam.Z;
                    if ((dx * dx + dy * dy + dz * dz) <= maxSq) keep.Add(i);
                }

                if (keep.Count == 0)
                {
                    int head = Math.Min(ents.Length, 512);
                    for (int i = 0; i < head; i++) if (ents[i] != 0) keep.Add(i);
                }

                var prefabMgr = new ulong[keep.Count];
                var factionComp = new ulong[keep.Count];
                var hitZone = new ulong[keep.Count];
                var animComp = new ulong[keep.Count];
                var typeClass = new ulong[keep.Count];
                var extMgr = new ulong[keep.Count];
                var isDeadB = new bool[keep.Count];

                using (var scatter = DmaMemory.Scatter())
                {
                    for (int k = 0; k < keep.Count; k++)
                    {
                        ulong e = ents[keep[k]];
                        scatter.PrepareReadValue<ulong>(e + Off.PrefabMgr);
                        scatter.PrepareReadValue<ulong>(e + Off.FactionComponent);
                        scatter.PrepareReadValue<ulong>(e + Off.ExtDamageMgr);
                        scatter.PrepareReadValue<ulong>(e + Off.CharacterAnimationComponent);
                    }
                    scatter.Execute();

                    for (int k = 0; k < keep.Count; k++)
                    {
                        ulong e = ents[keep[k]];
                        scatter.ReadValue(e + Off.PrefabMgr, out prefabMgr[k]);
                        scatter.ReadValue(e + Off.FactionComponent, out factionComp[k]);
                        scatter.ReadValue(e + Off.CharacterAnimationComponent, out animComp[k]);

                        if (scatter.ReadValue(e + Off.ExtDamageMgr, out ulong ext) && ext != 0)
                        {
                            extMgr[k] = ext;
                            DmaMemory.Read(ext + Off.HitZone, out hitZone[k]);
                        }

                        if (hitZone[k] != 0 && DmaMemory.Read(hitZone[k] + Off.Isdead, out byte db))
                            isDeadB[k] = db != 0;
                    }
                }

                var types = new string[keep.Count];
                var typePtr = new ulong[keep.Count];
                {
                    using (var scatter = DmaMemory.Scatter())
                    {
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (prefabMgr[k] == 0) continue;
                            scatter.PrepareReadValue<ulong>(prefabMgr[k] + Off.PrefabDataClass);
                        }
                        scatter.Execute();
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (prefabMgr[k] == 0) continue;
                            if (scatter.ReadValue(prefabMgr[k] + Off.PrefabDataClass, out ulong cls) && cls != 0)
                            {
                                typeClass[k] = cls;
                                DmaMemory.Read(cls + Off.PrefabDataType, out typePtr[k]);
                            }
                        }
                    }

                    using (var scatter = DmaMemory.Scatter(useCache: true))
                    {
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (typePtr[k] == 0) continue;
                            scatter.PrepareRead(typePtr[k], (uint)TypeStringMax);
                        }
                        scatter.Execute();
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (typePtr[k] != 0)
                                types[k] = scatter.ReadString(typePtr[k], TypeStringMax, Encoding.ASCII);
                        }
                    }
                }

                var factions = new string[keep.Count];
                {
                    var ftypePtr = new ulong[keep.Count];
                    using (var scatter = DmaMemory.Scatter(useCache: true))
                    {
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (factionComp[k] == 0) continue;
                            scatter.PrepareReadValue<ulong>(factionComp[k] + Off.FactionComponentDataClass);
                        }
                        scatter.Execute();
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (factionComp[k] == 0) continue;
                            if (scatter.ReadValue(factionComp[k] + Off.FactionComponentDataClass, out ulong fcls) && fcls != 0)
                            {
                                DmaMemory.Read(fcls + Off.FactionComponentDataType, out ftypePtr[k]);
                            }
                        }
                    }

                    using (var scatter = DmaMemory.Scatter(useCache: true))
                    {
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (ftypePtr[k] == 0) continue;
                            scatter.PrepareRead(ftypePtr[k], (uint)FactionStringMax);
                        }
                        scatter.Execute();
                        for (int k = 0; k < keep.Count; k++)
                        {
                            if (ftypePtr[k] != 0)
                                factions[k] = scatter.ReadString(ftypePtr[k], FactionStringMax, Encoding.ASCII);
                        }
                    }
                }

                Dictionary<ulong, string> pmMap = OnlyPlayersFromPlayerManager ? BuildNameMap() : new();

                var final = new List<int>(keep.Count);

                for (int k = 0; k < keep.Count; k++)
                {
                    var type = types[k] ?? "";
                    ulong ent = ents[keep[k]];

                    if (ClassEquals(type, "GenericEntityClass")) continue;

                    bool hasAnim = animComp[k] != 0;
                    bool hasHit = hitZone[k] != 0;
                    bool isDead = isDeadB[k];

                    bool charName = IsCharacterish(type);
                    bool chimera = IsChimeraStrict(type);
                    bool humanSkel = HasHumanoidSkeleton(ent, type);

                    bool passType = FilterMode switch
                    {
                        CharacterFilterMode.ChimeraOnly => chimera,
                        CharacterFilterMode.ChimeraPreferred => chimera || (charName && humanSkel),
                        _ => (charName || humanSkel) && !ContainsAny(type, NonHumanoidNameTokens),
                    };

                    if (!passType) continue;
                    if (RequireHitZones && !hasHit) continue;
                    if (!IncludeRagdolls && !hasAnim && !isDead) continue;
                    if (OnlyPlayersFromPlayerManager && (pmMap == null || !pmMap.ContainsKey(ent))) continue;

                    bool sameFaction = !string.IsNullOrEmpty(factions[k]) && factions[k].Equals(localFaction, StringComparison.OrdinalIgnoreCase);
                    if (!IncludeFriendlies && sameFaction) continue;

                    final.Add(k);
                }

                var bestByKey = new Dictionary<ulong, (int idx, int score)>();
                for (int ii = 0; ii < final.Count; ii++)
                {
                    int k = final[ii];
                    ulong key = (hitZone[k] != 0) ? hitZone[k] : (extMgr[k] != 0) ? extMgr[k] : (animComp[k] != 0) ? animComp[k] : ents[keep[k]];

                    int score = 0;
                    if (animComp[k] != 0) score += 3;
                    if (HasHumanoidSkeleton(ents[keep[k]], types[k])) score += 2;
                    if (hitZone[k] != 0) score += 1;

                    if (!bestByKey.TryGetValue(key, out var cur) || score > cur.score)
                        bestByKey[key] = (k, score);
                }

                var final2 = new List<int>(bestByKey.Count);
                foreach (var v in bestByKey.Values) final2.Add(v.idx);
                final = final2;

                var entsOut = new ulong[final.Count];
                var typesOut = new string[final.Count];
                var factionsOut = new string[final.Count];
                var hitZoneOut = new ulong[final.Count];
                var namesOut = new string[final.Count];

                for (int j = 0; j < final.Count; j++)
                {
                    int k = final[j];
                    ulong e = ents[keep[k]];

                    entsOut[j] = e;
                    typesOut[j] = types[k] ?? string.Empty;
                    factionsOut[j] = (k < factions.Length && factions[k] != null) ? factions[k] : string.Empty;
                    hitZoneOut[j] = hitZone[k];

                    if (OnlyPlayersFromPlayerManager && pmMap.TryGetValue(e, out var nick) && !string.IsNullOrWhiteSpace(nick))
                        namesOut[j] = nick;
                    else if (!ActorNameResolver.TryGetName(e, typeClass[k], out namesOut[j]))
                        namesOut[j] = string.Empty;

                    var rec = new DebugTypeRecord
                    {
                        Type = types[k] ?? string.Empty,
                        ClassShort = ClassName(types[k] ?? string.Empty),
                        PrefabMgr = prefabMgr[k],
                        TypeClass = typeClass[k],
                        TypePtr = typePtr[k],
                        AnimComp = animComp[k],
                        ExtDamageMgr = extMgr[k],
                        HitZone = hitZone[k],
                        MeshComp = 0,
                        MeshObj = 0
                    };

                    if (DebugLogTypePointers && DmaMemory.Read(e + Off.MeshComponent, out ulong mc) && mc != 0)
                    {
                        rec.MeshComp = mc;
                        if (DmaMemory.Read(mc + Off.MeshComponentData, out ulong mcd) && mcd != 0)
                            DmaMemory.Read(mcd + Off.MeshObject, out rec.MeshObj);
                    }

                    _dbgTypeByEnt[e] = rec;
                }

                ExchangeMeta(new MetaSnapshot(entsOut, typesOut, factionsOut, hitZoneOut, namesOut));

                if (DebugLogSlow && ((dbgTick++ & 0x1F) == 0))
                    Console.WriteLine($"[Players/Slow] kept={keep.Count} final={final.Count} fov={Game.Camera.Fov:0.0}");

            SleepSlow:
                var spent = (int)sw.ElapsedMilliseconds;
                if (spent < SlowIntervalMs) Thread.Sleep(SlowIntervalMs - spent);
            }
        }

        private static string TryGetLocalFaction(ulong gw)
        {
            if (!DmaMemory.Read(gw + Off.LocalPlayerController, out ulong ctrl) || ctrl == 0) return string.Empty;
            if (!DmaMemory.Read(ctrl + Off.LocalPlayer, out ulong local) || local == 0) return string.Empty;
            if (!DmaMemory.Read(local + Off.FactionComponentLocal, out ulong fLocal) || fLocal == 0) return string.Empty;
            if (!DmaMemory.Read(fLocal + Off.FactionComponentDataClass, out ulong fClass) || fClass == 0) return string.Empty;
            if (!DmaMemory.Read(fClass + Off.FactionComponentDataType, out ulong fType) || fType == 0) return string.Empty;
            return DmaMemory.ReadString(fType, FactionStringMax, Encoding.ASCII) ?? string.Empty;
        }

        private static void ExchangeMeta(MetaSnapshot snap) => Interlocked.Exchange(ref _meta, snap);

        private static void HpLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (_run)
            {
                sw.Restart();
                var meta = Volatile.Read(ref _meta);
                int n = meta.Count;
                if (n > 0)
                {
                    using var scatter = DmaMemory.Scatter(useCache: true);
                    for (int i = 0; i < n; i++)
                    {
                        if (i >= meta.HitZone.Length) break;
                        ulong hz = meta.HitZone[i];
                        if (hz == 0) continue;
                        scatter.PrepareReadValue<float>(hz + Off.HitZoneHP);
                        scatter.PrepareReadValue<byte>(hz + Off.Isdead);
                    }
                    scatter.Execute();
                    for (int i = 0; i < n && i < meta.HitZone.Length; i++)
                    {
                        ulong hz = meta.HitZone[i];
                        if (hz == 0) continue;
                        if (scatter.ReadValue(hz + Off.HitZoneHP, out float hp)) _hp[meta.Ents[i]] = hp;
                        if (scatter.ReadValue(hz + Off.Isdead, out byte deadB)) _dead[meta.Ents[i]] = deadB != 0;
                    }
                }
                var spent = (int)sw.ElapsedMilliseconds;
                if (spent < HpIntervalMs) Thread.Sleep(HpIntervalMs - spent);
            }
        }

        private static void FastLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (_run)
            {
                sw.Restart();
                Game.UpdateCamera();
                var meta = Volatile.Read(ref _meta);
                int n = meta.Count;
                if (n == 0)
                {
                    ExchangeFrame(FrameSnapshot.Empty);
                    PublishAnchors(Array.Empty<AnchorInfo>());
                    RebuildOwnedItems(Array.Empty<AnchorInfo>());
                    SleepFast(sw);
                    continue;
                }

                int cap = Math.Min(n, FrameCap);
                var em = new Matrix3x4[cap];
                using (var scatter = DmaMemory.Scatter())
                {
                    for (int i = 0; i < cap; i++)
                        scatter.PrepareReadValue<Matrix3x4>(meta.Ents[i] + Off.EntityMatrix);
                    scatter.Execute();
                    for (int i = 0; i < cap; i++)
                        scatter.ReadValue(meta.Ents[i] + Off.EntityMatrix, out em[i]);
                }

                var headIdx = new int[cap]; var neckIdx = new int[cap]; var spineIdx = new int[cap]; var hipIdx = new int[cap];
                var lFootIdx = new int[cap]; var rFootIdx = new int[cap]; var lHandIdx = new int[cap]; var rHandIdx = new int[cap];
                var lKneeIdx = new int[cap]; var rKneeIdx = new int[cap]; var lShoulderIdx = new int[cap]; var rShoulderIdx = new int[cap];
                var lElbowIdx = new int[cap]; var rElbowIdx = new int[cap]; var bonesPtr = new ulong[cap]; var animPtr = new ulong[cap];

                for (int i = 0; i < cap; i++)
                {
                    headIdx[i] = neckIdx[i] = spineIdx[i] = hipIdx[i] = lFootIdx[i] = rFootIdx[i] = -1;
                    lHandIdx[i] = rHandIdx[i] = lKneeIdx[i] = rKneeIdx[i] = -1;
                    lShoulderIdx[i] = rShoulderIdx[i] = lElbowIdx[i] = rElbowIdx[i] = -1;
                    bonesPtr[i] = 0; animPtr[i] = 0;
                }

                using (var scatter = DmaMemory.Scatter(useCache: true))
                {
                    for (int i = 0; i < cap; i++)
                    {
                        ulong ent = meta.Ents[i];
                        scatter.PrepareReadValue<ulong>(ent + Off.MeshComponent);
                        scatter.PrepareReadValue<ulong>(ent + Off.CharacterAnimationComponent);
                    }
                    scatter.Execute();
                    for (int i = 0; i < cap; i++)
                    {
                        ulong ent = meta.Ents[i];
                        if (scatter.ReadValue(ent + Off.MeshComponent, out ulong mc) && mc != 0)
                            DmaMemory.Read(mc + Off.MeshComponentBones, out bonesPtr[i]);
                        var model = (i < meta.Types.Length) ? meta.Types[i] : string.Empty;
                        if (Bones.EnsureIndicesForModel(ent, model, out var map6) && map6.Length > 0)
                        {
                            headIdx[i] = map6[0];
                            neckIdx[i] = (map6.Length > 1) ? map6[1] : -1;
                            spineIdx[i] = (map6.Length > 2) ? map6[2] : -1;
                            hipIdx[i] = (map6.Length > 3) ? map6[3] : -1;
                            lFootIdx[i] = (map6.Length > 4) ? map6[4] : -1;
                            rFootIdx[i] = (map6.Length > 5) ? map6[5] : -1;
                        }
                        if ((SkeletonLevel == SkeletonDetail.Lite10 || SkeletonLevel == SkeletonDetail.Baller14) &&
                            Bones.EnsureIndicesForModelVariant(ent, model, true, out var map16) && map16.Length >= 16)
                        {
                            if (map16[1] >= 0) neckIdx[i] = map16[1];
                            if (map16[2] >= 0) spineIdx[i] = map16[2];
                            if (SkeletonLevel == SkeletonDetail.Lite10)
                            {
                                lHandIdx[i] = map16[6]; rHandIdx[i] = map16[9];
                                lKneeIdx[i] = map16[11]; rKneeIdx[i] = map16[14];
                            }
                            else
                            {
                                lShoulderIdx[i] = map16[4]; lElbowIdx[i] = map16[5]; lHandIdx[i] = map16[6];
                                rShoulderIdx[i] = map16[7]; rElbowIdx[i] = map16[8]; rHandIdx[i] = map16[9];
                                lKneeIdx[i] = map16[11]; rKneeIdx[i] = map16[14];
                            }
                        }
                        scatter.ReadValue(ent + Off.CharacterAnimationComponent, out animPtr[i]);
                    }
                }

                var headMat = new Matrix3x4[cap]; var neckMat = new Matrix3x4[cap]; var spineMat = new Matrix3x4[cap];
                var hipMat = new Matrix3x4[cap]; var lFootMat = new Matrix3x4[cap]; var rFootMat = new Matrix3x4[cap];
                var lHandMat = new Matrix3x4[cap]; var rHandMat = new Matrix3x4[cap]; var lKneeMat = new Matrix3x4[cap];
                var rKneeMat = new Matrix3x4[cap]; var lShoulderMat = new Matrix3x4[cap]; var rShoulderMat = new Matrix3x4[cap];
                var lElbowMat = new Matrix3x4[cap]; var rElbowMat = new Matrix3x4[cap]; var stanceA = new EntityStance[cap];

                using (var scatter = DmaMemory.Scatter())
                {
                    for (int i = 0; i < cap; i++)
                    {
                        if (headIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], headIdx[i]));
                        if (hipIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], hipIdx[i]));
                        if (lFootIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lFootIdx[i]));
                        if (rFootIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rFootIdx[i]));
                        if (neckIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], neckIdx[i]));
                        if (spineIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], spineIdx[i]));
                        if (SkeletonLevel == SkeletonDetail.Lite10)
                        {
                            if (lHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lHandIdx[i]));
                            if (rHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rHandIdx[i]));
                            if (lKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lKneeIdx[i]));
                            if (rKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rKneeIdx[i]));
                        }
                        else if (SkeletonLevel == SkeletonDetail.Baller14)
                        {
                            if (lShoulderIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lShoulderIdx[i]));
                            if (rShoulderIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rShoulderIdx[i]));
                            if (lElbowIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lElbowIdx[i]));
                            if (rElbowIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rElbowIdx[i]));
                            if (lHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lHandIdx[i]));
                            if (rHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rHandIdx[i]));
                            if (lKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], lKneeIdx[i]));
                            if (rKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.PrepareReadValue<Matrix3x4>(BoneAddress(bonesPtr[i], rKneeIdx[i]));
                        }
                        if (animPtr[i] != 0) scatter.PrepareReadValue<int>(animPtr[i] + Off.CharacterStanceType);
                    }
                    scatter.Execute();

                    for (int i = 0; i < cap; i++)
                    {
                        if (headIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], headIdx[i]), out headMat[i]);
                        if (hipIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], hipIdx[i]), out hipMat[i]);
                        if (lFootIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], lFootIdx[i]), out lFootMat[i]);
                        if (rFootIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], rFootIdx[i]), out rFootMat[i]);
                        if (neckIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], neckIdx[i]), out neckMat[i]);
                        if (spineIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], spineIdx[i]), out spineMat[i]);
                        if (lHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], lHandIdx[i]), out lHandMat[i]);
                        if (rHandIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], rHandIdx[i]), out rHandMat[i]);
                        if (lKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], lKneeIdx[i]), out lKneeMat[i]);
                        if (rKneeIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], rKneeIdx[i]), out rKneeMat[i]);
                        if (lShoulderIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], lShoulderIdx[i]), out lShoulderMat[i]);
                        if (rShoulderIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], rShoulderIdx[i]), out rShoulderMat[i]);
                        if (lElbowIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], lElbowIdx[i]), out lElbowMat[i]);
                        if (rElbowIdx[i] >= 0 && bonesPtr[i] != 0) scatter.ReadValue(BoneAddress(bonesPtr[i], rElbowIdx[i]), out rElbowMat[i]);

                        stanceA[i] = EntityStance.UNKNOWN;
                        if (animPtr[i] != 0 && scatter.ReadValue(animPtr[i] + Off.CharacterStanceType, out int raw))
                        {
                            stanceA[i] = raw switch
                            {
                                0 => EntityStance.STAND,
                                1 => EntityStance.CROUCH,
                                2 => EntityStance.PRONE,
                                _ => EntityStance.UNKNOWN
                            };
                        }
                    }
                }

                static ulong BoneAddress(ulong bones, int index)
                    => bones + (ulong)index * Off.MeshComponentBonesMatrixSize;

                var cam = Game.Camera.Position;
                var bestByBody = new Dictionary<ulong, ActorDto>(capacity: cap);
                var anchorsBag = new Dictionary<ulong, AnchorInfo>(capacity: cap);

                for (int i = 0; i < cap; i++)
                {
                    Vector3f headW = new Vector3f(em[i].m09, em[i].m10, em[i].m11);
                    bool hasHead = (headIdx[i] >= 0 && bonesPtr[i] != 0 && !IsZero(headMat[i]));
                    if (hasHead) headW = MulPoint(em[i], headMat[i]);
                    bool hasHit = (i < meta.HitZone.Length) && meta.HitZone[i] != 0;
                    bool renderHumanoid = hasHit && hasHead;
                    if (OnlyPlayersFromPlayerManager) renderHumanoid &= (i < meta.Names.Length) && !string.IsNullOrWhiteSpace(meta.Names[i]);
                    if (!renderHumanoid) continue;
                    if (!Game.WorldToScreen(headW, out float sx, out float sy)) continue;
                    if (sx < 0 || sx > Game.Screen.W || sy < 0 || sy > Game.Screen.H) continue;
                    float dx = cam.X - headW.X, dy = cam.Y - headW.Y, dz = cam.Z - headW.Z;
                    int dist = (int)MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    float hpRaw; bool haveHp = _hp.TryGetValue(meta.Ents[i], out hpRaw);
                    float hpUi = haveHp ? (hpRaw <= 1.001f ? hpRaw * 100f : hpRaw) : 100f;
                    hpUi = Misc.Clamp(hpUi, 0f, 100f);
                    bool reallyDead = hpUi <= 0.5f;
                    bool haveLF = (lFootIdx[i] >= 0 && bonesPtr[i] != 0 && !IsZero(lFootMat[i]));
                    bool haveRF = (rFootIdx[i] >= 0 && bonesPtr[i] != 0 && !IsZero(rFootMat[i]));
                    bool haveHip = (hipIdx[i] >= 0 && bonesPtr[i] != 0 && !IsZero(hipMat[i]));
                    Vector3f baseW = headW; bool usedFeet = false;
                    if (haveLF || haveRF)
                    {
                        Vector3f sum = default; int c = 0;
                        if (haveLF) { var w = MulPoint(em[i], lFootMat[i]); sum = w; c++; }
                        if (haveRF) { var w = MulPoint(em[i], rFootMat[i]); sum = (c == 0) ? w : new Vector3f((sum.X + w.X) * 0.5f, (sum.Y + w.Y) * 0.5f, (sum.Z + w.Z) * 0.5f); c++; }
                        if (c > 0) { baseW = sum; usedFeet = true; }
                    }
                    else if (haveHip) { baseW = MulPoint(em[i], hipMat[i]); }
                    float hPxFromBones = -1f;
                    if (Game.WorldToScreen(baseW, out float sxBase, out float syBase)) hPxFromBones = MathF.Abs(syBase - sy);
                    float hPx, wPx;
                    if (hPxFromBones > 0f)
                    {
                        float fudge = usedFeet ? 1.00f : 2.15f;
                        hPx = Misc.Clamp(hPxFromBones * fudge, BoxMinHPx, BoxMaxHPx);
                        wPx = hPx * PersonWidthAspect;
                    }
                    else
                    {
                        var camM = Game.Camera;
                        var d = new Vector3f(headW.X - camM.Position.X, headW.Y - camM.Position.Y, headW.Z - camM.Position.Z);
                        float distF = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                        if (distF <= 1e-3f) continue;
                        float fov = camM.Fov; if (camM.CameraZoom > 0f) fov = 90f;
                        float vf = 1f / MathF.Tan((fov * MathF.PI / 180f) * 0.5f);
                        float pxPerMeter = (Game.Screen.H * 0.5f) * vf / distF;
                        hPx = Misc.Clamp(pxPerMeter * PersonHeightMeters, BoxMinHPx, BoxMaxHPx);
                        wPx = hPx * PersonWidthAspect;
                    }
                    Vector3f core = headW;
                    if (haveHip) core = MulPoint(em[i], hipMat[i]);
                    else if (usedFeet) core = baseW;
                    var now = NowSeconds();
                    _vel.TryGetValue(meta.Ents[i], out var h);
                    double dt = Math.Max(1e-3, now - (h.T == 0 ? now : h.T));
                    float dxR = core.X - h.Pos.X, dyR = core.Y - h.Pos.Y, dzR = core.Z - h.Pos.Z;
                    float jump = MathF.Sqrt(dxR * dxR + dyR * dyR + dzR * dzR);
                    bool reset = (h.T == 0) || dt > 0.6 || jump > 25f;
                    Vector3f rawVel = reset ? default : new Vector3f((dxR / (float)dt), (dyR / (float)dt), (dzR / (float)dt));
                    const float alpha = 0.35f;
                    Vector3f vel = reset ? rawVel : new Vector3f(h.Vel.X + (rawVel.X - h.Vel.X) * alpha, h.Vel.Y + (rawVel.Y - h.Vel.Y) * alpha, h.Vel.Z + (rawVel.Z - h.Vel.Z) * alpha);
                    _vel[meta.Ents[i]] = new VelHist { Pos = core, T = now, Vel = vel };
                    Vector3f vel2D = new Vector3f(vel.X, 0f, vel.Z);
                    float speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);
                    float speed2D = MathF.Sqrt(vel2D.X * vel2D.X + vel2D.Z * vel2D.Z);
                    Vector3f dir = speed > 1e-3f ? new Vector3f(vel.X / speed, vel.Y / speed, vel.Z / speed) : default;
                    Vector3f dir2 = speed2D > 1e-3f ? new Vector3f(vel2D.X / speed2D, 0f, vel2D.Z / speed2D) : default;
                    float yawRad = (speed2D > 1e-3f) ? MathF.Atan2(dir2.X, dir2.Z) : 0f;
                    float yawDeg = yawRad * (180f / MathF.PI);
                    Vector2f[] bones2D = null; Vector2f bmin = default, bmax = default; int valid = 0;
                    if (hasHead && bonesPtr[i] != 0)
                    {
                        if (SkeletonLevel == SkeletonDetail.Compact6)
                        {
                            bones2D = new Vector2f[6];
                            for (int b = 0; b < bones2D.Length; b++) bones2D[b] = new Vector2f(float.NaN, float.NaN);
                            (Matrix3x4 mat, bool ok)[] mats = { (headMat[i], !IsZero(headMat[i])), (neckMat[i], !IsZero(neckMat[i])), (spineMat[i], !IsZero(spineMat[i])), (hipMat[i], !IsZero(hipMat[i])), (lFootMat[i], !IsZero(lFootMat[i])), (rFootMat[i], !IsZero(rFootMat[i])), };
                            for (int b = 0; b < mats.Length; b++)
                                if (mats[b].ok && Game.WorldToScreen(MulPoint(em[i], mats[b].mat), out float px, out float py))
                                { bones2D[b] = new Vector2f(px, py); if (valid++ == 0) { bmin = bones2D[b]; bmax = bones2D[b]; } else { if (px < bmin.X) bmin.X = px; if (py < bmin.Y) bmin.Y = py; if (px > bmax.X) bmax.X = px; if (py > bmax.Y) bmax.Y = py; } }
                        }
                        else if (SkeletonLevel == SkeletonDetail.Lite10)
                        {
                            bones2D = new Vector2f[10];
                            for (int b = 0; b < bones2D.Length; b++) bones2D[b] = new Vector2f(float.NaN, float.NaN);
                            (Matrix3x4 mat, bool ok)[] mats = { (headMat[i], !IsZero(headMat[i])), (neckMat[i], !IsZero(neckMat[i])), (spineMat[i], !IsZero(spineMat[i])), (hipMat[i], !IsZero(hipMat[i])), (lHandMat[i], !IsZero(lHandMat[i])), (rHandMat[i], !IsZero(rHandMat[i])), (lKneeMat[i], !IsZero(lKneeMat[i])), (rKneeMat[i], !IsZero(rKneeMat[i])), (lFootMat[i], !IsZero(lFootMat[i])), (rFootMat[i], !IsZero(rFootMat[i])), };
                            for (int b = 0; b < mats.Length; b++)
                                if (mats[b].ok && Game.WorldToScreen(MulPoint(em[i], mats[b].mat), out float px, out float py))
                                { bones2D[b] = new Vector2f(px, py); if (valid++ == 0) { bmin = bones2D[b]; bmax = bones2D[b]; } else { if (px < bmin.X) bmin.X = px; if (py < bmin.Y) bmin.Y = py; if (px > bmax.X) bmax.X = px; if (py > bmax.Y) bmax.Y = py; } }
                        }
                        else
                        {
                            bones2D = new Vector2f[14];
                            for (int b = 0; b < bones2D.Length; b++) bones2D[b] = new Vector2f(float.NaN, float.NaN);
                            (Matrix3x4 mat, bool ok)[] mats = { (headMat[i], !IsZero(headMat[i])), (neckMat[i], !IsZero(neckMat[i])), (spineMat[i], !IsZero(spineMat[i])), (hipMat[i], !IsZero(hipMat[i])), (lShoulderMat[i], !IsZero(lShoulderMat[i])), (lElbowMat[i], !IsZero(lElbowMat[i])), (lHandMat[i], !IsZero(lHandMat[i])), (rShoulderMat[i], !IsZero(rShoulderMat[i])), (rElbowMat[i], !IsZero(rElbowMat[i])), (rHandMat[i], !IsZero(rHandMat[i])), (lKneeMat[i], !IsZero(lKneeMat[i])), (lFootMat[i], !IsZero(lFootMat[i])), (rKneeMat[i], !IsZero(rKneeMat[i])), (rFootMat[i], !IsZero(rFootMat[i])) };
                            for (int b = 0; b < mats.Length; b++)
                                if (mats[b].ok && Game.WorldToScreen(MulPoint(em[i], mats[b].mat), out float px, out float py))
                                { bones2D[b] = new Vector2f(px, py); if (valid++ == 0) { bmin = bones2D[b]; bmax = bones2D[b]; } else { if (px < bmin.X) bmin.X = px; if (py < bmin.Y) bmin.Y = py; if (px > bmax.X) bmax.X = px; if (py > bmax.Y) bmax.Y = py; } }
                        }
                    }
                    var actor = new ActorDto
                    {
                        Ptr = meta.Ents[i], Entity = (i < meta.Types.Length) ? meta.Types[i] : string.Empty,
                        Faction = (i < meta.Factions.Length) ? meta.Factions[i] : string.Empty, Name = (i < meta.Names.Length) ? meta.Names[i] : string.Empty,
                        Health = (int)(hpUi + 0.5f), IsDead = reallyDead, Distance = dist, Position = headW, Projected = new Vector2f(sx, sy),
                        HasHead = hasHead, HeadWorld = hasHead ? headW : default, Head2D = new Vector2f(sx, sy), Bones = bones2D,
                        HasBones = (hPxFromBones > 0f) || (bones2D != null && valid >= 2), BoxW = wPx, BoxH = hPx,
                        BMin = bmin, BMax = bmax, Velocity = vel, Speed = speed, Velocity2D = new Vector3f(vel.X, 0f, vel.Z),
                        Speed2D = speed2D, Dir = dir, Dir2D = dir2, YawRad = yawRad, YawDeg = yawDeg, Stance = stanceA[i],
                    };
                    ulong bodyKey = (i < meta.HitZone.Length && meta.HitZone[i] != 0) ? meta.HitZone[i] : (animPtr[i] != 0 ? animPtr[i] : actor.Ptr);
                    if (bestByBody.TryGetValue(bodyKey, out var prev))
                    {
                        if (Better(actor, prev)) bestByBody[bodyKey] = actor;
                    }
                    else { bestByBody[bodyKey] = actor; }
                    var acore = haveHip ? MulPoint(em[i], hipMat[i]) : (usedFeet ? baseW : headW);
                    var an = new AnchorInfo { Ptr = actor.Ptr, Core = acore, Head = headW, Name = actor.Name };
                    if (!anchorsBag.ContainsKey(bodyKey)) anchorsBag[bodyKey] = an;
                }

                static bool Better(ActorDto a, ActorDto b)
                {
                    int sa = a.Stance != EntityStance.UNKNOWN ? 1 : 0; int sb = b.Stance != EntityStance.UNKNOWN ? 1 : 0;
                    if (sa != sb) return sa > sb;
                    if (a.HasBones != b.HasBones) return a.HasBones;
                    if (a.Speed2D != b.Speed2D) return a.Speed2D > b.Speed2D;
                    if (a.IsDead != b.IsDead) return !a.IsDead;
                    if (a.Health != b.Health) return a.Health > b.Health;
                    bool aName = !string.IsNullOrWhiteSpace(a.Name); bool bName = !string.IsNullOrWhiteSpace(b.Name);
                    if (aName != bName) return aName;
                    return true;
                }

                var dedupedList = bestByBody.Values.ToList();
                var actorsCollapsed = CoalesceByHead(dedupedList, epsMeters: 0.50f, epsPx: 10f);
                DebugDumpDrawn(actorsCollapsed);
                ExchangeFrame(new FrameSnapshot(actorsCollapsed));
                PublishAnchors(anchorsBag.Values.ToArray());
                RebuildOwnedItems(LatestAnchors);
                SleepFast(sw);
            }
        }

        private static void PublishAnchors(AnchorInfo[] anchors) => Volatile.Write(ref _anchors, anchors);
        private static void ExchangeFrame(FrameSnapshot snap) => Interlocked.Exchange(ref _frame, snap);

        private static void SleepFast(System.Diagnostics.Stopwatch sw)
        {
            int spentMs = (int)sw.ElapsedMilliseconds; int remainMs = FastIntervalMs - spentMs;
            if (remainMs > 1) Thread.Sleep(remainMs - 1);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            double targetMs = Math.Max(0, FastIntervalMs - sw.ElapsedMilliseconds);
            long targetTicks = start + (long)(targetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < targetTicks) Thread.SpinWait(64);
        }

        public static bool IsItemCarried(ulong itemPtr, in Vector3f pos, string kind, string name, out ulong ownerPtr)
        {
            ownerPtr = 0;
            if (OwnOnlyWeaponsForNow && !IsWeapon(kind, name)) return false;
            var anchors = LatestAnchors;
            if (anchors == null || anchors.Length == 0) return false;
            float r0 = Math.Max(1.6f, ItemAttachRadiusM); float r1 = Math.Max(r0 * 1.5f, ItemAttachHysteresisM);
            float r0sq = r0 * r0, r1sq = r1 * r1;
            static float Dist2(in Vector3f a, in Vector3f b) { float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return dx * dx + dy * dy + dz * dz; }
            if (_itemOwner.TryGetValue(itemPtr, out var prevOwner))
            {
                var havePrev = anchors.FirstOrDefault(a => a.Ptr == prevOwner);
                if (havePrev.Ptr != 0)
                {
                    float d2 = MathF.Min(Dist2(pos, havePrev.Core), Dist2(pos, havePrev.Head));
                    if (d2 <= r1sq) { ownerPtr = prevOwner; return true; }
                }
            }
            float best = float.MaxValue; ulong bestOwner = 0;
            for (int i = 0; i < anchors.Length; i++)
            {
                float d2 = MathF.Min(Dist2(pos, anchors[i].Core), Dist2(pos, anchors[i].Head));
                if (d2 <= r0sq && d2 < best) { best = d2; bestOwner = anchors[i].Ptr; }
            }
            if (bestOwner != 0) { _itemOwner[itemPtr] = bestOwner; ownerPtr = bestOwner; return true; }
            return false;
        }

        private static long _lastDrawLogTick;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DebugDumpDrawn(ActorDto[] list)
        {
            if (!DebugLogDrawn) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double sinceMs = (_lastDrawLogTick == 0) ? double.MaxValue : (now - _lastDrawLogTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (sinceMs < DebugLogDrawnEveryMs) return;
            _lastDrawLogTick = now;
            var sb = new StringBuilder(512 + list.Length * 96);
            sb.Append("[Players/Draw] count=").Append(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                var a = list[i]; DebugTypeRecord rec;
                if (!_dbgTypeByEnt.TryGetValue(a.Ptr, out rec)) rec = default;
                string typeStr = !string.IsNullOrWhiteSpace(a.Entity) ? a.Entity : !string.IsNullOrWhiteSpace(rec.Type) ? rec.Type : TryResolveTypeLive(a.Ptr, out rec.PrefabMgr, out rec.TypeClass, out rec.TypePtr);
                string clsShort = !string.IsNullOrWhiteSpace(typeStr) ? ClassName(typeStr) : (!string.IsNullOrWhiteSpace(rec.ClassShort) ? rec.ClassShort : string.Empty);
                if (!_dbgTypeByEnt.ContainsKey(a.Ptr))
                {
                    rec.Type = typeStr ?? string.Empty; rec.ClassShort = clsShort ?? string.Empty;
                    if (rec.MeshComp == 0 && DmaMemory.Read(a.Ptr + Off.MeshComponent, out ulong mc) && mc != 0)
                    {
                        rec.MeshComp = mc;
                        if (DmaMemory.Read(mc + Off.MeshComponentData, out ulong mcd) && mcd != 0) DmaMemory.Read(mcd + Off.MeshObject, out rec.MeshObj);
                    }
                    _dbgTypeByEnt[a.Ptr] = rec;
                }
                sb.Append("\n  #").Append(i.ToString("D2")).Append(" ptr=0x").Append(a.Ptr.ToString("X")).Append(" class=").Append(clsShort ?? string.Empty)
                  .Append(" type='").Append(typeStr ?? string.Empty).Append('\'').Append(" name='").Append(a.Name ?? string.Empty).Append('\'')
                  .Append(" hp=").Append(a.Health).Append(" dead=").Append(a.IsDead ? 1 : 0).Append(" bones=").Append(a.HasBones ? 1 : 0)
                  .Append(" stance=").Append(a.Stance).Append(" dist=").Append(a.Distance)
                  .Append(" box=").Append(a.BoxW.ToString("0.0")).Append('x').Append(a.BoxH.ToString("0.0"))
                  .Append(" head2D=(").Append(a.Head2D.X.ToString("0")).Append(",").Append(a.Head2D.Y.ToString("0")).Append(")");
                if (DebugLogTypePointers)
                {
                    sb.Append("\n      pmgr=0x").Append(rec.PrefabMgr.ToString("X")).Append(" tclass=0x").Append(rec.TypeClass.ToString("X"))
                      .Append(" tptr=0x").Append(rec.TypePtr.ToString("X")).Append(" anim=0x").Append(rec.AnimComp.ToString("X"))
                      .Append(" ext=0x").Append(rec.ExtDamageMgr.ToString("X")).Append(" hz=0x").Append(rec.HitZone.ToString("X"))
                      .Append(" mesh=0x").Append(rec.MeshComp.ToString("X")).Append(" mo=0x").Append(rec.MeshObj.ToString("X"));
                }
            }
            Console.WriteLine(sb.ToString());
        }

        private static void RebuildOwnedItems(AnchorInfo[] anchors)
        {
            var items = GameObjects.LatestItemsAll ?? Array.Empty<GameObjects.ItemDto>();
            if (anchors == null || anchors.Length == 0 || items.Length == 0)
            {
                _equippedWeapon.Clear(); _ownedItems.Clear();
                if (_itemOwner.Count > 0) foreach (var kv in _itemOwner.ToArray()) _itemOwner.TryRemove(kv.Key, out _);
                return;
            }
            var currentItems = new HashSet<ulong>(items.Select(it => it.Ptr));
            foreach (var kv in _itemOwner.ToArray()) if (!currentItems.Contains(kv.Key)) _itemOwner.TryRemove(kv.Key, out _);
            var tmpOwned = new Dictionary<ulong, List<string>>(); var primary = new Dictionary<ulong, string>();
            foreach (var it in items)
            {
                if (OwnOnlyWeaponsForNow && !IsWeapon(it.Kind, it.Name)) continue;
                if (IsItemCarried(it.Ptr, it.Position, it.Kind, it.Name, out var owner) && owner != 0)
                {
                    if (!tmpOwned.TryGetValue(owner, out var list)) tmpOwned[owner] = list = new List<string>(8);
                    list.Add(it.Name);
                    if (IsGun(it.Name, it.Type) && !primary.ContainsKey(owner)) primary[owner] = it.Name;
                }
            }
            _ownedItems.Clear(); foreach (var (owner, list) in tmpOwned) _ownedItems[owner] = list;
            _equippedWeapon.Clear(); foreach (var (owner, gun) in primary) _equippedWeapon[owner] = gun;
        }

        private static bool IsWeapon(string kind, string name)
        {
            if (!string.IsNullOrEmpty(kind) && kind.Equals("Weapon", StringComparison.OrdinalIgnoreCase)) return true;
            return ContainsAny(name ?? "", new[] { "Rifle", "Carbine", "Pistol", "SMG", "MG", "Shotgun", "Sniper", "AK", "M4", "M16", "PKM", "SVD", "VSS", "Uzi", "MP5" });
        }

        private static bool IsGun(string name, string type)
        {
            string s = $"{name} {type}";
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (ContainsAny(s, new[] { "Grenade", "Launcher", "Rocket", "RPG", "Mine", "Explosive", "Binocular", "Binoculars" })) return false;
            return ContainsAny(s, new[] { "Rifle", "Carbine", "Pistol", "SMG", "MG", "PK", "PKM", "RPK", "AK", "M4", "M16", "AR", "DMR", "Marksman", "Sniper", "SVD", "SVU", "VSS", "AS VAL", "Shotgun", "UZI", "MP5" });
        }

        private static bool IsPtr(ulong v) => v > 0x10000 && v < 0x0000800000000000UL;

        private static Dictionary<ulong, string> BuildNameMap()
        {
            var map = new Dictionary<ulong, string>();
            if (!DmaMemory.Read(DmaMemory.Base + Off.Game, out ulong game) || game == 0) return map;
            if (!DmaMemory.Read(game + Off.PlayerManager, out ulong pm) || pm == 0) return map;
            if (!DmaMemory.Read(pm + Off.PmPlayerCount, out int count) || count <= 0 || count > 1024) return map;
            if (!DmaMemory.Read(pm + Off.PmPlayerArray, out ulong arr) || arr == 0) return map;
            var players = DmaMemory.ReadArray<ulong>(arr, count) ?? Array.Empty<ulong>();
            if (players.Length == 0) return map;
            var names = new string[players.Length]; var pawns = new ulong[players.Length];
            using (var scatter = DmaMemory.Scatter(useCache: true))
            {
                for (int i = 0; i < players.Length; i++)
                {
                    ulong p = players[i];
                    if (p == 0) continue;
                    scatter.PrepareReadValue<ulong>(p + Off.Player_Name);
                    scatter.PrepareReadValue<ulong>(p + Off.Player_FirstLevelPtr);
                }
                scatter.Execute();
                for (int i = 0; i < players.Length; i++)
                {
                    ulong p = players[i];
                    if (p == 0) continue;
                    if (scatter.ReadValue(p + Off.Player_Name, out ulong namePtr) && namePtr != 0)
                    {
                        var s = DmaMemory.ReadString(namePtr, 64, Encoding.UTF8);
                        if (IsLikelyName(s)) names[i] = s;
                    }
                    if (scatter.ReadValue(p + Off.Player_FirstLevelPtr, out ulong lvl1) && lvl1 != 0)
                    {
                        if (DmaMemory.Read(lvl1 + Off.FirstLevel_ControlledEntity, out ulong ctrlEnt) && ctrlEnt != 0)
                            DmaMemory.Read(ctrlEnt + Off.ControlledEntity_Ptr2, out pawns[i]);
                    }
                }
            }
            for (int i = 0; i < players.Length; i++) if (pawns[i] != 0 && !string.IsNullOrWhiteSpace(names[i])) map[pawns[i]] = names[i];
            return map;
            static bool IsLikelyName(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return false;
                if (s.Length < 2 || s.Length > 32) return false;
                if (s.Contains('/') || s.Contains('\\') || s.Contains(".et", StringComparison.OrdinalIgnoreCase)) return false;
                if (s.Contains("Chimera", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }

        static ActorDto[] CoalesceByHead(List<ActorDto> src, float epsMeters = 0.35f, float epsPx = 8f)
        {
            float eps2 = epsMeters * epsMeters; var outList = new List<ActorDto>(src.Count);
            static bool SameNameOrBlank(string a, string b) => string.Equals(a, b, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b);
            static bool CloseWorld(in ActorDto a, in ActorDto b, float e2) { float dx = a.HeadWorld.X - b.HeadWorld.X; float dy = a.HeadWorld.Y - b.HeadWorld.Y; float dz = a.HeadWorld.Z - b.HeadWorld.Z; return dx * dx + dy * dy + dz * dz <= e2; }
            static bool CloseScreen(in ActorDto a, in ActorDto b, float px) { float dx = a.Head2D.X - b.Head2D.X; float dy = a.Head2D.Y - b.Head2D.Y; return MathF.Abs(dx) <= px && MathF.Abs(dy) <= px; }
            static bool Better(ActorDto a, ActorDto b)
            {
                int sa = a.Stance != EntityStance.UNKNOWN ? 1 : 0; int sb = b.Stance != EntityStance.UNKNOWN ? 1 : 0;
                if (sa != sb) return sa > sb;
                if (a.HasBones != b.HasBones) return a.HasBones;
                if (a.Speed2D != b.Speed2D) return a.Speed2D > b.Speed2D;
                if (a.IsDead != b.IsDead) return !a.IsDead;
                if (a.Health != b.Health) return a.Health > b.Health;
                bool aName = !string.IsNullOrWhiteSpace(a.Name); bool bName = !string.IsNullOrWhiteSpace(b.Name);
                if (aName != bName) return aName;
                return true;
            }
            for (int i = 0; i < src.Count; i++)
            {
                var a = src[i]; bool merged = false;
                for (int j = 0; j < outList.Count; j++)
                {
                    var b = outList[j];
                    if (SameNameOrBlank(a.Name, b.Name) && (CloseWorld(a, b, eps2) || CloseScreen(a, b, epsPx)))
                    { if (Better(a, b)) outList[j] = a; merged = true; break; }
                }
                if (!merged) outList.Add(a);
            }
            return outList.ToArray();
        }

        public static class Bones
        {
            public static void ClearCache() => _indexCache.Clear();
            private static readonly string[][] NameSets6 = { new[]{ "Head", "head" }, new[]{ "Neck", "Neck1", "Spine3", "spine_2" }, new[]{ "Spine2", "Spine", "spine" }, new[]{ "Hips", "Pelvis", "pelvis", "Root" }, new[]{ "L_Foot", "LeftFoot", "l_foot" }, new[]{ "R_Foot", "RightFoot", "r_foot" }, };
            private static readonly string[][] NameSets16 = { new[]{ "Head", "head" }, new[]{ "Neck", "Neck1", "neck" }, new[]{ "Spine3","Spine2","Spine","spine_2","spine" }, new[]{ "Hips","Pelvis","pelvis","Root" }, new[]{ "L_UpperArm","LeftArm","LeftShoulder","l_upperarm" }, new[]{ "L_ForeArm","LeftForeArm","l_forearm" }, new[]{ "L_Hand","LeftHand","l_hand" }, new[]{ "R_UpperArm","RightArm","RightShoulder","r_upperarm" }, new[]{ "R_ForeArm","RightForeArm","r_forearm" }, new[]{ "R_Hand","RightHand","r_hand" }, new[]{ "L_UpperLeg","LeftUpLeg","LeftThigh","l_thigh" }, new[]{ "L_LowerLeg","LeftLeg","l_calf","l_leg" }, new[]{ "L_Foot","LeftFoot","l_foot" }, new[]{ "R_UpperLeg","RightUpLeg","RightThigh","r_thigh" }, new[]{ "R_LowerLeg","RightLeg","r_calf","r_leg" }, new[]{ "R_Foot","RightFoot","r_foot" }, };
            private static readonly ConcurrentDictionary<string, int[]> _indexCache = new(StringComparer.OrdinalIgnoreCase);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int HashBoneName(string s) { int h = 5381; for (int i = 0; i < s.Length; i++) { int c = s[i] | 0x20; unchecked { h = (int)(((uint)h * 0x21u + (uint)c) & 0xFFFFFFFF); } } return h; }
            public static bool EnsureIndicesForModel(ulong ent, string modelType, out int[] idx) => EnsureIndicesForModelInternal(ent, modelType, use16: false, out idx);
            public static bool EnsureIndicesForModelVariant(ulong ent, string modelType, bool want16, out int[] idx) => EnsureIndicesForModelInternal(ent, modelType, use16: want16, out idx);
            private static bool EnsureIndicesForModelInternal(ulong ent, string modelType, bool use16, out int[] idx)
            {
                var sets = use16 ? NameSets16 : NameSets6; string suffix = use16 ? "|16" : "|6";
                if (!string.IsNullOrEmpty(modelType) && _indexCache.TryGetValue(modelType + suffix, out idx)) return true;
                if (!DmaMemory.Read(ent + Off.MeshComponent, out ulong mc) || mc == 0) { idx = Array.Empty<int>(); return false; }
                if (!DmaMemory.Read(mc + Off.MeshComponentData, out ulong mcd) || mcd == 0) { idx = Array.Empty<int>(); return false; }
                if (!DmaMemory.Read(mcd + Off.MeshObject, out ulong mo) || mo == 0) { idx = Array.Empty<int>(); return false; }
                string moKey = $"mo:{mo:X}{suffix}";
                if (_indexCache.TryGetValue(moKey, out idx)) return true;
                if (!DmaMemory.Read(mo + Off.MeshObjectBonesCount, out uint boneCount) || boneCount == 0 || boneCount > 500) { idx = Array.Empty<int>(); return false; }
                if (!DmaMemory.Read(mo + Off.MeshObjectBonesList, out ulong list) || list == 0) { idx = Array.Empty<int>(); return false; }
                int logicalSlots = sets.Length; var want = new Dictionary<int, int>(logicalSlots * 4);
                for (int slot = 0; slot < logicalSlots; slot++) foreach (var alt in sets[slot]) want[HashBoneName(alt)] = slot;
                var map = new int[logicalSlots]; for (int i = 0; i < logicalSlots; i++) map[i] = -1;
                for (uint i = 0; i < boneCount; i++) { int id; if (!DmaMemory.Read(list + 0x10 + (Off.MeshObjectBonesSize * i), out id)) continue; if (want.TryGetValue(id, out int logical) && (uint)logical < (uint)logicalSlots) map[logical] = (int)i; }
                _indexCache[moKey] = map;
                if (!string.IsNullOrEmpty(modelType)) _indexCache.TryAdd(modelType + suffix, map);
                idx = map; return true;
            }
        }
    }

    static class ActorNameResolver
    {
        static readonly int[] Candidates = { 0x7D8, 0x2B0, 0x468, 0x620 };
        static readonly Dictionary<ulong, int> _ofsByClass = new();
        static readonly Dictionary<int, int> _votes = new();
        static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        public static bool TryGetName(ulong actor, ulong classKey, out string name)
        {
            name = null;
            if (classKey != 0 && _ofsByClass.TryGetValue(classKey, out int knownOfs)) { if (TryReadNameAt(actor, knownOfs, out var s) && LooksLikeDisplayName(s)) { name = s; return true; } }
            string best = null; int chosen = 0;
            foreach (var ofs in Candidates) { if (!TryReadNameAt(actor, ofs, out var s)) continue; if (LooksLikeDisplayName(s)) { best = s; chosen = ofs; break; } if (best == null && !string.IsNullOrWhiteSpace(s)) { best = s; chosen = ofs; } }
            if (!string.IsNullOrWhiteSpace(best)) { name = best; if (classKey != 0) { _ofsByClass[classKey] = chosen; _votes.TryGetValue(chosen, out var v); _votes[chosen] = v + 1; } return true; }
            return false;
        }
        static bool TryReadNameAt(ulong actor, int ofs, out string s) { s = null; if (!DmaMemory.Read(actor + (ulong)ofs, out ulong p) || p == 0) return false; var u8 = DmaMemory.ReadString(p, 64, Utf8); if (!string.IsNullOrEmpty(u8)) { s = Clean(u8); return true; } var asc = DmaMemory.ReadString(p, 64, Encoding.ASCII); if (!string.IsNullOrEmpty(asc)) { s = Clean(asc); return true; } return false; }
        static string Clean(string s) { s = s.TrimEnd('\0').Trim(); if (s.Length == 0) return s; var arr = s.ToCharArray(); for (int i = 0; i < arr.Length; i++) if (char.IsControl(arr[i]) && arr[i] != '\t' && arr[i] != '\n' && arr[i] != '\r') arr[i] = ' '; return new string(arr).Trim(); }
        static bool LooksLikeDisplayName(string s) { if (string.IsNullOrWhiteSpace(s)) return false; s = s.Trim(); if (s.Length < 3 || s.Length > 48) return false; string[] bad = { "FakeDeath", "pilot", "Compartment", "SelectAction" }; foreach (var b in bad) if (s.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return false; int letters = 0; foreach (var c in s) if (char.IsLetter(c)) letters++; if (letters < 2) return false; return true; }
        public static string GetVoteSummary() { var sb = new StringBuilder(); foreach (var kv in _votes) sb.AppendLine($"+0x{kv.Key:X} -> {kv.Value} hits"); return sb.ToString(); }
    }
}
