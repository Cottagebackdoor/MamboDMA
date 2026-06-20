using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using MamboDMA.Services;

namespace MamboDMA.Games.ABI
{
    internal static class ABIOffsetsExt
    {
        //  Existing
        public const int OFF_PAWN_ASC            = 0x15E0;
        public const int OFF_PAWN_DEATHCOMP      = 0x1728;
        public const int OFF_ASC_ATTRSETS        = 0x0188;
        public const int OFF_ATTR_HEALTH         = 0x48;
        public const int OFF_ATTR_HEALTHMAX      = 0x4C;
        public const int OFF_DEATHCOMP_DEATHINFO = 0x0240;

        public const ulong OFF_MESH_TIMERS = 0x3D8;
        public const int   OFF_LASTSUBMIT  = 0x4;
        public const int   OFF_LASTONSCREEN= 0xC;

        public const int OFF_CHAR_TICKING_ON_DEATH = 0x16B0;

        public const float VIS_TICK = 0.06f;

        //  New (Weapon Zoom path)
        public const int   OFF_PAWN_WEAPONMAN      = 0x1878; // fill if known
        public const ulong OFF_WEAPON_CURRENT      = 0x158;
        public const ulong OFF_WEAPON_ZOOMCOMP     = 0xB00;
        public const ulong OFF_ZOOM_PROGRESSRATE   = 0x404;
        public const ulong OFF_ZOOM_SCOPEMAG       = 0x578;
    }

    public static class Players
    {
        //  world/camera pointers
        public static ulong UWorld, UGameInstance, GameState, PersistentLevel;
        public static ulong ActorArray; public static int ActorCount;
        public static ulong LocalPlayers, PlayerController, PlayerArray; public static int PlayerCount;
        public static ulong LocalPawn, LocalRoot, LocalState, LocalCameraMgr;

        // published snapshots
        public static Vector3 LocalPosition;     // world (bias applied)
        public static FMinimalViewInfo Camera;   // for W2S
        public static float CtrlYaw;             // from APlayerController::ControlRotation

        // origin bias
        private static Vector3 _originBias;
        private static Vector3 _prevLocalWorld;
        private static bool _havePrevLocal;

        // public views
        public static List<ABIPlayer> ActorList = new();
        public static List<ActorPos>  ActorPositions = new();

        // skeleton cache
        private static readonly Dictionary<ulong, (Vector3[] pts, long ts)> _skeletons = new();

        public static readonly object Sync = new();

        // seqlock for camera/local
        private static int _camSeq;
        private static Vector3 _camLocalBuf;
        private static FMinimalViewInfo _camBuf;
        private static float _ctrlYawBuf;
        private static Vector3 _camBiasBuf;

        // positions' active bias
        private static Vector3 _positionsBias;

        // skeleton cache knobs
        private const double SKEL_MAX_AGE_MS        = 900.0;
        private const double SKEL_FORCE_REFRESH_MS  = 500.0;
        private const float  SUBMIT_EPSILON         = 0.0005f;

        // coherent frame for ESP
        public struct Frame
        {
            public FMinimalViewInfo Cam;
            public Vector3 Local;
            public List<ActorPos> Positions;
            public long Stamp;
        }
        private static int _frameSeq;
        private static Frame _frameBuf;

        private static bool _running;

        // stable cache (sticky)
        private struct ActorCacheEntry
        {
            public ulong Pawn;
            public ulong Mesh;
            public ulong Root;
            public ulong ASC;
            public ulong DeathComp;
            public ulong HealthSet;
            public bool  IsBot;
            public string Name;
        }
        private static readonly Dictionary<ulong, ActorCacheEntry> _actorCache = new(1024);

        // guard for bone recompute
        private static readonly Dictionary<ulong, float> _lastSubmitSeen = new(256);

        // throttles
        private static long _lastEnumTicks;
        private const int ENUM_PERIOD_MS = 150;

        //  Weapon Zoom snapshot
        public struct ZoomInfo
        {
            public bool  Valid;
            public float Zoom;          // effective zoom (1..N)
            public float ScopeMag;      // scope base magnification
            public float Progress;      // 0..1
            public ulong WeaponMan;     // component ptr
            public ulong CurrentWeapon;
            public ulong ZoomComp;
            public long  Stamp;
        }
        /// <summary>
        /// Gets the current origin bias applied to all positions.
        /// This is needed by ABILootESP to align loot positions with player positions.
        /// </summary>
        public static Vector3 GetOriginBias()
        {
            return _originBias;
        }
        private static readonly object _zoomSync = new();
        private static ZoomInfo _zoom;

        public static bool TryGetZoom(out ZoomInfo zi)
        {
            lock (_zoomSync) { zi = _zoom; return zi.Valid; }
        }

        // API
        public static void StartCache()
        {
            if (_running || !DmaMemory.IsAttached) return;
            _running = true;

            new Thread(CacheWorldLoop)     { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.World"     }.Start();
            new Thread(CacheCameraLoop)    { IsBackground = true, Priority = ThreadPriority.Highest,     Name = "ABI.Camera"    }.Start();
            new Thread(CachePlayersLoop)   { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.Players"   }.Start();
            new Thread(CacheVitalsLoop)    { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.Vitals"    }.Start();
            new Thread(CachePositionsLoop) { IsBackground = true, Priority = ThreadPriority.Highest,     Name = "ABI.Positions" }.Start();
            new Thread(CacheSkeletonsLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.Skeletons" }.Start();
            new Thread(FramePulseLoop)     { IsBackground = true, Priority = ThreadPriority.Highest,     Name = "ABI.FramePulse"}.Start();
            new Thread(CacheWeaponZoomLoop){ IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "ABI.Weapon"    }.Start();
        }

        public static void Stop() => _running = false;

        // seqlock helpers
        public static bool TryGetFrame(out Frame f)
        {
            f = default;
            for (int i = 0; i < 3; i++)
            {
                int s1 = Volatile.Read(ref _frameSeq);
                if ((s1 & 1) != 0) continue;
                f = _frameBuf;
                Thread.MemoryBarrier();
                int s2 = Volatile.Read(ref _frameSeq);
                if (s1 == s2 && (s2 & 1) == 0 && f.Positions != null) return true;
            }
            return false;
        }

        public static bool TryGetSkeleton(ulong pawn, out Vector3[] pts)
        {
            lock (Sync)
            {
                if (_skeletons.TryGetValue(pawn, out var v))
                { pts = v.pts; return pts != null; }
            }
            pts = null; return false;
        }

        internal static void __SetSkeletonUnsafe(ulong pawn, Vector3[] pts)
        {
            _skeletons[pawn] = (pts, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        //  loops
        private static void CacheWorldLoop()
        {
            while (_running) { try { CacheWorld(); } catch { } HighResDelay(50); }
        }

        private static bool CacheWorld()
        {
            UWorld = DmaMemory.Read<ulong>(DmaMemory.Base + ABIOffsets.GWorld);
            if (UWorld == 0) return false;

            UGameInstance   = DmaMemory.Read<ulong>(UWorld + ABIOffsets.UWorld_OwningGameInstance);
            GameState       = DmaMemory.Read<ulong>(UWorld + ABIOffsets.UWorld_GameState);
            PersistentLevel = DmaMemory.Read<ulong>(UWorld + ABIOffsets.UWorld_PersistentLevel);

            ActorArray      = DmaMemory.Read<ulong>(PersistentLevel + ABIOffsets.ULevel_ActorArray + 0x00);
            ActorCount      = DmaMemory.Read<int>  (PersistentLevel + ABIOffsets.ULevel_ActorArray + 0x08);

            LocalPlayers     = DmaMemory.Read<ulong>(DmaMemory.Read<ulong>(UGameInstance + ABIOffsets.UGameInstance_LocalPlayers));
            PlayerController = DmaMemory.Read<ulong>(LocalPlayers + ABIOffsets.UPlayer_PlayerController);
            LocalCameraMgr   = DmaMemory.Read<ulong>(PlayerController + ABIOffsets.APlayerController_PlayerCameraManager);
            LocalPawn        = DmaMemory.Read<ulong>(PlayerController + ABIOffsets.APlayerController_AcknowledgedPawn);
            LocalRoot        = DmaMemory.Read<ulong>(LocalPawn + ABIOffsets.AActor_RootComponent);

            PlayerArray      = DmaMemory.Read<ulong>(GameState + ABIOffsets.AGameStateBase_PlayerArray);
            PlayerCount      = DmaMemory.Read<int>  (GameState + ABIOffsets.AGameStateBase_PlayerCount);
            return true;
        }

        private static void CacheCameraLoop()
        {
            // Use what the engine actually drew last frame to cut jitter/flicker
            const bool useLastFrameCam = true;

            while (_running)
            {
                try
                {
                    if (LocalCameraMgr != 0 && LocalRoot != 0 && PlayerController != 0)
                    {
                        using var scatter = DmaMemory.Scatter();

                        ulong camCache = useLastFrameCam
                            ? ABIOffsets.APlayerCameraManager_LastFrameCameraCachePrivate
                            : ABIOffsets.APlayerCameraManager_CameraCachePrivate;

                        ulong cameraAddress = LocalCameraMgr + camCache + 0x10;
                        ulong localPositionAddress = LocalRoot + ABIOffsets.USceneComponent_RelativeLocation;
                        ulong controlRotationAddress = PlayerController + ABIOffsets.AController_ControlRotation;
                        scatter.PrepareReadValue<FMinimalViewInfo>(cameraAddress);
                        scatter.PrepareReadValue<Vector3>(localPositionAddress);
                        scatter.PrepareReadValue<Rotator>(controlRotationAddress);
                        scatter.Execute();

                        if (scatter.ReadValue(cameraAddress, out FMinimalViewInfo cam) &&
                            scatter.ReadValue(localPositionAddress, out Vector3 localWorld))
                        {
                            if (_havePrevLocal)
                            {
                                var jump = _prevLocalWorld - localWorld;
                                if (jump.Length() > 5000f) _originBias += jump;
                            }
                            _prevLocalWorld = localWorld;
                            _havePrevLocal = true;

                            var localBiased = localWorld + _originBias;
                            cam.Location += _originBias;

                            float ctrlYaw = scatter.ReadValue(controlRotationAddress, out Rotator ctrlRot)
                                ? ctrlRot.Yaw
                                : cam.Rotation.Yaw;

                            Interlocked.Increment(ref _camSeq);
                            _camBuf      = cam;
                            _camLocalBuf = localBiased;
                            _ctrlYawBuf  = ctrlYaw;
                            _camBiasBuf  = _originBias;
                            Camera       = cam;
                            LocalPosition= localBiased;
                            CtrlYaw      = ctrlYaw;
                            Interlocked.Increment(ref _camSeq);
                        }
                    }
                }
                catch { }
                HighResDelay(1);
            }
        }

        private static void CachePlayersLoop()
        {
            while (_running)
            {
                try
                {
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double ms   = now * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double last = _lastEnumTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms - last < ENUM_PERIOD_MS) { HighResDelay(10); continue; }
                    _lastEnumTicks = now;

                    if (ActorArray == 0 || ActorCount <= 0) { lock (Sync) ActorList = new(); continue; }

                    var tmp = new List<ABIPlayer>(Math.Min(ActorCount, 2048));

                    using (var scatter = DmaMemory.Scatter())
                    {
                        int take = Math.Min(ActorCount, 2048);
                        for (int i = 0; i < take; i++)
                            scatter.PrepareReadValue<ulong>(ActorArray + (ulong)i * 8);
                        scatter.Execute();

                        for (int i = 0; i < take; i++)
                        {
                            ulong actorAddress = ActorArray + (ulong)i * 8;
                            if (!scatter.ReadValue(actorAddress, out ulong pawn) || pawn == 0 || pawn == LocalPawn) continue;

                            uint id = DmaMemory.Read<uint>(pawn + 24);
                            string name = ABINamePool.GetName(id);
                            if (string.IsNullOrEmpty(name)) continue;

                            if (!name.Contains("BP_UamCharacter") &&
                                !name.Contains("BP_UamAICharacter") &&
                                !name.Contains("BP_UamRangeCharacter_C"))
                                continue;

                            bool isBot = name.Contains("AI");
                            tmp.Add(new ABIPlayer(pawn, 0, 0, name, isBot, 0, 0, 0));
                        }
                    }

                    // resolve sticky ptrs
                    if (tmp.Count > 0)
                    {
                        using var scatter = DmaMemory.Scatter();
                        var idxMap = new List<int>(tmp.Count);

                        for (int i = 0; i < tmp.Count; i++)
                        {
                            var t = tmp[i];
                            _actorCache.TryGetValue(t.Pawn, out var ac);

                            bool need = false;
                            if (ac.Mesh == 0)      { scatter.PrepareReadValue<ulong>(t.Pawn + ABIOffsets.ACharacter_Mesh); need = true; }
                            if (ac.Root == 0)      { scatter.PrepareReadValue<ulong>(t.Pawn + ABIOffsets.AActor_RootComponent); need = true; }
                            if (ac.ASC == 0)       { scatter.PrepareReadValue<ulong>(t.Pawn + (ulong)ABIOffsetsExt.OFF_PAWN_ASC); need = true; }
                            if (ac.DeathComp == 0) { scatter.PrepareReadValue<ulong>(t.Pawn + (ulong)ABIOffsetsExt.OFF_PAWN_DEATHCOMP); need = true; }
                            if (need) idxMap.Add(i);
                        }

                        if (idxMap.Count > 0)
                        {
                            scatter.Execute();
                            foreach (int i in idxMap)
                            {
                                var t = tmp[i];
                                _actorCache.TryGetValue(t.Pawn, out var ac);

                                if (scatter.ReadValue(t.Pawn + ABIOffsets.ACharacter_Mesh, out ulong mesh) && mesh != 0) ac.Mesh = mesh;
                                if (scatter.ReadValue(t.Pawn + ABIOffsets.AActor_RootComponent, out ulong root) && root != 0) ac.Root = root;
                                if (scatter.ReadValue(t.Pawn + (ulong)ABIOffsetsExt.OFF_PAWN_ASC, out ulong asc) && asc != 0) ac.ASC = asc;
                                if (scatter.ReadValue(t.Pawn + (ulong)ABIOffsetsExt.OFF_PAWN_DEATHCOMP, out ulong dc) && dc != 0) ac.DeathComp = dc;

                                ac.IsBot = t.IsBot; ac.Name = t.Name;
                                _actorCache[t.Pawn] = ac;
                            }
                        }

                        for (int i = 0; i < tmp.Count; i++)
                        {
                            var t = tmp[i];
                            if (_actorCache.TryGetValue(t.Pawn, out var ac))
                                tmp[i] = new ABIPlayer(t.Pawn, ac.Mesh, ac.Root, t.Name, ac.IsBot, ac.ASC, ac.HealthSet, ac.DeathComp);
                        }
                    }

                    lock (Sync) ActorList = tmp;
                }
                catch { }
            }
        }

        private static void CacheVitalsLoop()
        {
            while (_running)
            {
                try
                {
                    List<ABIPlayer> actors;
                    lock (Sync) actors = ActorList.Count == 0 ? null : new List<ABIPlayer>(ActorList);
                    if (actors == null || actors.Count == 0) { HighResDelay(8); continue; }

                    // Resolve missing HealthSet once
                    var needHS = new List<int>(128);
                    for (int i = 0; i < actors.Count; i++)
                    {
                        var a = actors[i];
                        if (a.ASC == 0) continue;
                        if (_actorCache.TryGetValue(a.Pawn, out var ac) && ac.HealthSet != 0) continue;
                        needHS.Add(i);
                    }

                    if (needHS.Count > 0)
                    {
                        using var scatter = DmaMemory.Scatter();

                        for (int k = 0; k < needHS.Count; k++)
                        {
                            int i = needHS[k];
                            ulong basePtr = actors[i].ASC + (ulong)ABIOffsetsExt.OFF_ASC_ATTRSETS;
                            scatter.PrepareReadValue<ulong>(basePtr + 0x0);
                            scatter.PrepareReadValue<int>(basePtr + 0x8);
                        }
                        scatter.Execute();

                        const int MAX_SCAN = 8;
                        for (int k = 0; k < needHS.Count; k++)
                        {
                            int i = needHS[k];
                            ulong basePtr = actors[i].ASC + (ulong)ABIOffsetsExt.OFF_ASC_ATTRSETS;
                            if (!scatter.ReadValue(basePtr + 0x0, out ulong data) || data == 0) continue;
                            if (!scatter.ReadValue(basePtr + 0x8, out int num) || num <= 0) continue;

                            int take = Math.Min(num, MAX_SCAN);
                            using var candidateScatter = DmaMemory.Scatter();
                            for (int e = 0; e < take; e++)
                                candidateScatter.PrepareReadValue<ulong>(data + (ulong)e * 8);
                            candidateScatter.Execute();

                            var hsCandidates = new List<ulong>(take);
                            for (int e = 0; e < take; e++)
                            {
                                if (candidateScatter.ReadValue(data + (ulong)e * 8, out ulong hs) && hs != 0)
                                    hsCandidates.Add(hs);
                            }

                            ulong resolved = 0;
                            if (hsCandidates.Count > 0)
                            {
                                using var candidateHealthScatter = DmaMemory.Scatter();
                                for (int e = 0; e < hsCandidates.Count; e++)
                                {
                                    ulong hs = hsCandidates[e];
                                    candidateHealthScatter.PrepareReadValue<float>(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTH);
                                    candidateHealthScatter.PrepareReadValue<float>(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTHMAX);
                                }
                                candidateHealthScatter.Execute();

                                for (int e = 0; e < hsCandidates.Count; e++)
                                {
                                    ulong hs = hsCandidates[e];
                                    bool haveHealth = candidateHealthScatter.ReadValue(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTH, out float h);
                                    bool haveMaxHealth = candidateHealthScatter.ReadValue(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTHMAX, out float hm);
                                    if (haveHealth && haveMaxHealth &&
                                        hm > 1f && hm < 5000f && h >= -50f && h < hm * 2.5f)
                                    { resolved = hsCandidates[e]; break; }
                                }
                            }

                            if (resolved != 0)
                            {
                                if (!_actorCache.TryGetValue(actors[i].Pawn, out var ac)) ac = new ActorCacheEntry { Pawn = actors[i].Pawn };
                                ac.HealthSet = resolved;
                                _actorCache[actors[i].Pawn] = ac;
                                actors[i] = new ABIPlayer(actors[i].Pawn, actors[i].Mesh, actors[i].Root, actors[i].Name, actors[i].IsBot, actors[i].ASC, resolved, actors[i].DeathComp);
                            }
                        }

                        lock (Sync) ActorList = actors;
                    }

                    // Read vitals (light)
                    var hsList = new List<(int idx, ulong hs)>(actors.Count);
                    for (int i = 0; i < actors.Count; i++)
                        if (actors[i].HealthSet != 0) hsList.Add((i, actors[i].HealthSet));
                    if (hsList.Count == 0) { HighResDelay(8); continue; }

                    using var healthScatter = DmaMemory.Scatter();
                    for (int k = 0; k < hsList.Count; k++)
                    {
                        ulong hs = hsList[k].hs;
                        healthScatter.PrepareReadValue<float>(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTH);
                        healthScatter.PrepareReadValue<float>(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTHMAX);
                    }
                    healthScatter.Execute();

                    var vitals = new Dictionary<ulong, (float h, float hm)>(hsList.Count);
                    for (int k = 0; k < hsList.Count; k++)
                    {
                        ulong hs = hsList[k].hs;
                        healthScatter.ReadValue(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTH, out float h);
                        healthScatter.ReadValue(hs + (ulong)ABIOffsetsExt.OFF_ATTR_HEALTHMAX, out float hm);
                        var pawn = actors[hsList[k].idx].Pawn;
                        vitals[pawn] = (h, hm);
                    }
                    lock (_pendingVitalsSync) _pendingVitals = vitals;
                }
                catch { }

                HighResDelay(8);
            }
        }

        private static readonly object _pendingVitalsSync = new();
        private static Dictionary<ulong, (float h, float hm)> _pendingVitals;

        private static void CachePositionsLoop()
        {
            var posScratch = new List<ActorPos>(256);

            while (_running)
            {
                try
                {
                    List<ABIPlayer> actors;
                    Dictionary<ulong, (float h, float hm)> vitals = null;
                    lock (Sync)
                    {
                        actors = ActorList.Count == 0 ? null : new List<ABIPlayer>(ActorList);
                    }
                    lock (_pendingVitalsSync)
                    {
                        if (_pendingVitals != null)
                            vitals = new Dictionary<ulong, (float h, float hm)>(_pendingVitals);
                    }

                    if (actors != null && actors.Count > 0)
                    {
                        // Snapshot bias used for this positions frame
                        TryGetCameraSnapshot(out var camRaw, out var localRaw, out _, out var camBias);
                        var frameBias = camBias;
                        _positionsBias = frameBias;

                        // Read CTWs, timers, flags
                        var rootCtwPtrs = new ulong[actors.Count];
                        var meshCtwPtrs = new ulong[actors.Count];
                        var lastSubmit  = new float[actors.Count];
                        var lastOnScr   = new float[actors.Count];
                        var deadFlag    = new bool[actors.Count];
                        var deadComp    = new bool[actors.Count];

                        using (var scatter = DmaMemory.Scatter())
                        {
                            for (int i = 0; i < actors.Count; i++)
                            {
                                var a = actors[i];

                                if (a.Root != 0)
                                    scatter.PrepareReadValue<ulong>(a.Root + ABIOffsets.USceneComponent_ComponentToWorld_Ptr);

                                if (a.Mesh != 0)
                                {
                                    scatter.PrepareReadValue<ulong>(a.Mesh + ABIOffsets.USceneComponent_ComponentToWorld_Ptr);

                                    ulong timers = a.Mesh + ABIOffsetsExt.OFF_MESH_TIMERS;
                                    scatter.PrepareReadValue<float>(timers + (ulong)ABIOffsetsExt.OFF_LASTSUBMIT);
                                    scatter.PrepareReadValue<float>(timers + (ulong)ABIOffsetsExt.OFF_LASTONSCREEN);
                                }

                                if (ABIOffsetsExt.OFF_CHAR_TICKING_ON_DEATH != 0)
                                    scatter.PrepareReadValue<byte>(a.Pawn + (ulong)ABIOffsetsExt.OFF_CHAR_TICKING_ON_DEATH);

                                if (a.DeathComp != 0)
                                    scatter.PrepareReadValue<byte>(a.DeathComp + (ulong)ABIOffsetsExt.OFF_DEATHCOMP_DEATHINFO);
                            }
                            scatter.Execute();

                            for (int i = 0; i < actors.Count; i++)
                            {
                                var a = actors[i];
                                if (a.Root != 0)
                                    scatter.ReadValue(a.Root + ABIOffsets.USceneComponent_ComponentToWorld_Ptr, out rootCtwPtrs[i]);
                                if (a.Mesh != 0)
                                {
                                    scatter.ReadValue(a.Mesh + ABIOffsets.USceneComponent_ComponentToWorld_Ptr, out meshCtwPtrs[i]);
                                    ulong timers = a.Mesh + ABIOffsetsExt.OFF_MESH_TIMERS;
                                    scatter.ReadValue(timers + (ulong)ABIOffsetsExt.OFF_LASTSUBMIT, out lastSubmit[i]);
                                    scatter.ReadValue(timers + (ulong)ABIOffsetsExt.OFF_LASTONSCREEN, out lastOnScr[i]);
                                }

                                deadFlag[i] = false;
                                if (ABIOffsetsExt.OFF_CHAR_TICKING_ON_DEATH != 0 &&
                                    scatter.ReadValue(a.Pawn + (ulong)ABIOffsetsExt.OFF_CHAR_TICKING_ON_DEATH, out byte dbyte))
                                    deadFlag[i] = (dbyte & 0x01) != 0;

                                deadComp[i] = false;
                                if (a.DeathComp != 0 &&
                                    scatter.ReadValue(a.DeathComp + (ulong)ABIOffsetsExt.OFF_DEATHCOMP_DEATHINFO, out byte bIsDeadByte))
                                    deadComp[i] = (bIsDeadByte & 0x01) != 0;
                            }
                        }

                        // Deref CTWs & bias-align
                        var rootCtwValues = new FTransform[actors.Count];
                        var meshCtwValues = new FTransform[actors.Count];

                        using (var scatter = DmaMemory.Scatter())
                        {
                            for (int i = 0; i < actors.Count; i++)
                            {
                                if (rootCtwPtrs[i] != 0) scatter.PrepareReadValue<FTransform>(rootCtwPtrs[i]);
                                if (meshCtwPtrs[i] != 0) scatter.PrepareReadValue<FTransform>(meshCtwPtrs[i]);
                            }
                            scatter.Execute();

                            for (int i = 0; i < actors.Count; i++)
                            {
                                if (rootCtwPtrs[i] != 0 && scatter.ReadValue(rootCtwPtrs[i], out FTransform rctw))
                                {
                                    if (float.IsFinite(rctw.Translation.X)) rctw.Translation += frameBias;
                                    rootCtwValues[i] = rctw;
                                }
                                if (meshCtwPtrs[i] != 0 && scatter.ReadValue(meshCtwPtrs[i], out FTransform mctw))
                                {
                                    if (float.IsFinite(mctw.Translation.X)) mctw.Translation += frameBias;
                                    meshCtwValues[i] = mctw;
                                }
                            }
                        }

                        // Assemble positions
                        posScratch.Clear();
                        for (int i = 0; i < actors.Count; i++)
                        {
                            var a = actors[i];
                            if (a.Root == 0) continue;

                            Vector3 pos = (meshCtwValues[i].Translation.X == 0 && meshCtwValues[i].Translation.Y == 0 && meshCtwValues[i].Translation.Z == 0)
                                ? rootCtwValues[i].Translation
                                : meshCtwValues[i].Translation;

                            float H = 0f, HM = 0f;
                            if (vitals != null && vitals.TryGetValue(a.Pawn, out var vt))
                            { H = vt.h; HM = vt.hm; }

                            bool freshSkel = HasFreshSkeleton(a.Pawn);

                            posScratch.Add(new ActorPos(
                                a.Pawn, a.Mesh, a.Root, a.DeathComp,
                                pos, meshCtwValues[i],
                                lastSubmit[i], lastOnScr[i],
                                H, HM,
                                freshSkel,
                                deadFlag[i],
                                deadComp[i]
                            ));
                        }

                        // Publish full frame (rebase camera/local to this frame's bias)
                        TryGetCameraSnapshot(out var camNow, out var localNow, out _, out var camBiasNow);
                        var camSnap   = camNow;  camSnap.Location = camNow.Location - camBiasNow + frameBias;
                        var localSnap =              localNow      - camBiasNow + frameBias;

                        Interlocked.Increment(ref _frameSeq);
                        _frameBuf = new Frame
                        {
                            Cam       = camSnap,
                            Local     = localSnap,
                            Positions = new List<ActorPos>(posScratch),
                            Stamp     = System.Diagnostics.Stopwatch.GetTimestamp()
                        };
                        Interlocked.Increment(ref _frameSeq);

                        lock (Sync) ActorPositions = new List<ActorPos>(posScratch);

                        // SAME-FRAME SKELETON WARMUP (favor on-screen to save budget)
                        const int WarmupK = 12;
                        if (ActorPositions.Count > 0)
                        {
                            var isBotMap = new Dictionary<ulong, bool>(ActorList.Count);
                            lock (Sync) foreach (var a in ActorList) isBotMap[a.Pawn] = a.IsBot;

                            var shortlist = new List<ActorPos>(WarmupK);
                            foreach (var ap in ActorPositions)
                            {
                                if (ap.Mesh == 0 || ap.IsDead) continue;
                                shortlist.Add(ap);
                            }

                            // Prefer currently visible to avoid wasted recomputes
                            shortlist.RemoveAll(p => !p.IsVisible);

                            // If all invisible, fall back to distance list
                            if (shortlist.Count == 0) shortlist = new List<ActorPos>(ActorPositions);

                            shortlist.Sort((a, b) =>
                            {
                                bool an = isBotMap.TryGetValue(a.Pawn, out var ia) ? !ia : true;
                                bool bn = isBotMap.TryGetValue(b.Pawn, out var ib) ? !ib : true;
                                int pri = bn.CompareTo(an); if (pri != 0) return pri;
                                float da = Vector3.DistanceSquared(localSnap, a.Position);
                                float db = Vector3.DistanceSquared(localSnap, b.Position);
                                return da.CompareTo(db);
                            });

                            int taken = 0;
                            foreach (var ap in shortlist)
                            {
                                if (taken >= WarmupK) break;

                                float lastSeen = 0f;
                                _lastSubmitSeen.TryGetValue(ap.Pawn, out lastSeen);

                                // detect backwards (wrap/LOD): allow recompute
                                if (ap.LastSubmit + SUBMIT_EPSILON < lastSeen)
                                    _lastSubmitSeen[ap.Pawn] = ap.LastSubmit - 2 * SUBMIT_EPSILON;

                                bool submitAdvanced = ap.LastSubmit > lastSeen + SUBMIT_EPSILON;

                                bool needsStaleRefresh = true;
                                lock (Sync)
                                {
                                    if (_skeletons.TryGetValue(ap.Pawn, out var v))
                                    {
                                        double ageMs = (System.Diagnostics.Stopwatch.GetTimestamp() - v.ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                                        needsStaleRefresh = ageMs >= SKEL_FORCE_REFRESH_MS;
                                    }
                                }
                                if (!(submitAdvanced || needsStaleRefresh)) continue;

                                var ctwLocal = ap.Ctw; // bias-aligned mesh CTW
                                if (Skeleton.TryGetWorldBones(ap.Mesh, ap.Root, in ctwLocal, out var pts, out var dbg))
                                {
                                    __SetSkeletonUnsafe(ap.Pawn, pts);
                                    Skeleton.LastDebug = dbg;
                                    _lastSubmitSeen[ap.Pawn] = Math.Max(ap.LastSubmit, lastSeen);
                                    taken++;
                                }
                            }
                        }
                    }
                }
                catch { }

                HighResDelay(2);
            }
        }

        private static void FramePulseLoop()
        {
            while (_running)
            {
                try
                {
                    List<ActorPos> currentPositions;
                    lock (Sync) currentPositions = ActorPositions?.Count > 0 ? new List<ActorPos>(ActorPositions) : null;
                    if (currentPositions != null)
                    {
                        TryGetCameraSnapshot(out var cam, out var local, out _, out var camBias);
                        var camRebased   = cam;   camRebased.Location = cam.Location - camBias + _positionsBias;
                        var localRebased =          local             - camBias + _positionsBias;

                        Interlocked.Increment(ref _frameSeq);
                        _frameBuf = new Frame
                        {
                            Cam       = camRebased,
                            Local     = localRebased,
                            Positions = currentPositions,
                            Stamp     = System.Diagnostics.Stopwatch.GetTimestamp()
                        };
                        Interlocked.Increment(ref _frameSeq);
                    }
                }
                catch { }
                HighResDelay(1);
            }
        }

        private static void CacheSkeletonsLoop()
        {
            const int MAX_PER_SLICE = 48;
            const int MIN_PER_SLICE = 12; // Slightly higher to reduce stale states
            const int BUDGET_MS     = 5;  // Small budget for better stability when zoomed
            var sw = new System.Diagnostics.Stopwatch();

            while (_running)
            {
                try
                {
                    List<ABIPlayer> actors;
                    List<ActorPos> positions;
                    Vector3 local;
                    lock (Sync)
                    {
                        if (ActorList.Count == 0) { _PruneOldSkeletons(0); HighResDelay(2); continue; }
                        actors = new List<ABIPlayer>(ActorList);
                        positions = new List<ActorPos>(ActorPositions);
                        local = LocalPosition;
                    }

                    if (positions.Count == 0) { HighResDelay(2); continue; }

                    var posMap = new Dictionary<ulong, ActorPos>(positions.Count);
                    for (int i = 0; i < positions.Count; i++) posMap[positions[i].Pawn] = positions[i];

                    // Prioritize non-bots and nearest
                    actors.Sort((a, b) =>
                    {
                        int ra = a.IsBot ? 1 : 0;
                        int rb = b.IsBot ? 1 : 0;
                        int r = ra.CompareTo(rb);
                        if (r != 0) return r;
                        posMap.TryGetValue(a.Pawn, out var pa);
                        posMap.TryGetValue(b.Pawn, out var pb);
                        float da = Vector3.DistanceSquared(local, pa.Position);
                        float db = Vector3.DistanceSquared(local, pb.Position);
                        return da.CompareTo(db);
                    });

                    sw.Restart();
                    int processed = 0, limit = Math.Min(MAX_PER_SLICE, actors.Count);

                    for (int i = 0; i < actors.Count; i++)
                    {
                        var a = actors[i];
                        if (a.Mesh == 0) continue;
                        if (!posMap.TryGetValue(a.Pawn, out var ap)) continue;
                        if (ap.IsDead) continue;

                        float lastSeen = 0f;
                        _lastSubmitSeen.TryGetValue(a.Pawn, out lastSeen);

                        // detect backwards
                        if (ap.LastSubmit + SUBMIT_EPSILON < lastSeen)
                            _lastSubmitSeen[a.Pawn] = ap.LastSubmit - 2 * SUBMIT_EPSILON;

                        bool submitAdvanced = ap.LastSubmit > lastSeen + SUBMIT_EPSILON;

                        bool needsStaleRefresh = true;
                        lock (Sync)
                        {
                            if (_skeletons.TryGetValue(a.Pawn, out var v))
                            {
                                double ageMs = (System.Diagnostics.Stopwatch.GetTimestamp() - v.ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                                needsStaleRefresh = ageMs >= SKEL_FORCE_REFRESH_MS;
                            }
                        }
                        if (!(submitAdvanced || needsStaleRefresh)) continue;

                        var ctwLocal = ap.Ctw;
                        if (Skeleton.TryGetWorldBones(a.Mesh, a.Root, in ctwLocal, out var pts, out var dbg))
                        {
                            lock (Sync) _skeletons[a.Pawn] = (pts, System.Diagnostics.Stopwatch.GetTimestamp());
                            Skeleton.LastDebug = dbg;
                            _lastSubmitSeen[a.Pawn] = Math.Max(ap.LastSubmit, lastSeen);
                        }

                        processed++;
                        if (processed >= MIN_PER_SLICE && sw.ElapsedMilliseconds >= BUDGET_MS) break;
                        if (processed >= limit) break;
                    }

                    _PruneOldSkeletons(System.Diagnostics.Stopwatch.GetTimestamp());
                }
                catch { }
            }
        }

        private static bool TryGetCameraSnapshot(out FMinimalViewInfo cam, out Vector3 local, out float ctrlYaw, out Vector3 camBias)
        {
            cam = default; local = default; ctrlYaw = 0f; camBias = default;
            for (int i = 0; i < 3; i++)
            {
                int s1 = Volatile.Read(ref _camSeq);
                if ((s1 & 1) != 0) continue;
                cam     = _camBuf;
                local   = _camLocalBuf;
                ctrlYaw = _ctrlYawBuf;
                camBias = _camBiasBuf;
                Thread.MemoryBarrier();
                int s2 = Volatile.Read(ref _camSeq);
                if (s1 == s2 && (s2 & 1) == 0) return true;
            }
            return false;
        }

        private static void _PruneOldSkeletons(long nowTicks)
        {
            if (nowTicks == 0) { lock (Sync) _skeletons.Clear(); return; }

            long hz = System.Diagnostics.Stopwatch.Frequency;
            long maxAge  = (long)(SKEL_MAX_AGE_MS  * 0.001 * hz);

            lock (Sync)
            {
                if (_skeletons.Count == 0) return;
                var toRemove = new List<ulong>(4);
                foreach (var kv in _skeletons)
                {
                    long age = nowTicks - kv.Value.ts;
                    if (age > maxAge) toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) _skeletons.Remove(toRemove[i]);
            }
        }

        private static bool HasFreshSkeleton(ulong pawn, double maxAgeMs = SKEL_MAX_AGE_MS)
        {
            lock (Sync)
            {
                if (_skeletons.TryGetValue(pawn, out var v))
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double ageMs = (now - v.ts) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    return ageMs <= maxAgeMs;
                }
            }
            return false;
        }

        private static void HighResDelay(int targetMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int sleepMs = Math.Max(0, targetMs - 1);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
            while (sw.ElapsedMilliseconds < targetMs) Thread.SpinWait(80);
        }

        //  Weapon zoom poller
        private static void CacheWeaponZoomLoop()
        {
            while (_running)
            {
                try
                {
                    if (LocalPawn == 0 || ABIOffsetsExt.OFF_PAWN_WEAPONMAN == 0)
                    {
                        lock (_zoomSync) _zoom = default;
                        HighResDelay(16);
                        continue;
                    }

                    ulong wm = DmaMemory.Read<ulong>(LocalPawn + (ulong)ABIOffsetsExt.OFF_PAWN_WEAPONMAN);
                    if (wm == 0) { lock (_zoomSync) _zoom = default; HighResDelay(16); continue; }

                    ulong weapon = DmaMemory.Read<ulong>(wm + ABIOffsetsExt.OFF_WEAPON_CURRENT);
                    if (weapon == 0) { lock (_zoomSync) _zoom = default; HighResDelay(16); continue; }

                    ulong zoomComp = DmaMemory.Read<ulong>(weapon + ABIOffsetsExt.OFF_WEAPON_ZOOMCOMP);
                    if (zoomComp == 0) { lock (_zoomSync) _zoom = default; HighResDelay(16); continue; }

                    ulong progressAddress = zoomComp + ABIOffsetsExt.OFF_ZOOM_PROGRESSRATE;
                    ulong magnificationAddress = zoomComp + ABIOffsetsExt.OFF_ZOOM_SCOPEMAG;
                    using var scatter = DmaMemory.Scatter();
                    scatter.PrepareReadValue<float>(progressAddress);
                    scatter.PrepareReadValue<float>(magnificationAddress);
                    scatter.Execute();

                    float progress = scatter.ReadValue(progressAddress, out float p) ? p : 0f; // 0..1
                    float scopeMag = scatter.ReadValue(magnificationAddress, out float m) ? m : 1f; // e.g., 1.0, 3.0, 6.0
                    if (!float.IsFinite(progress)) progress = 0f;
                    if (!float.IsFinite(scopeMag)) scopeMag = 1f;
                    progress = Math.Clamp(progress, 0f, 1.2f); // small overshoot guard
                    scopeMag = Math.Clamp(scopeMag, 1f, 60f);

                    float zoom = 1f + (scopeMag - 1f) * progress;
                    zoom = Math.Clamp(zoom, 1f, 60f);

                    lock (_zoomSync)
                    {
                        _zoom = new ZoomInfo
                        {
                            Valid = true,
                            Zoom = zoom,
                            ScopeMag = scopeMag,
                            Progress = Math.Clamp(progress, 0f, 1f),
                            WeaponMan = wm,
                            CurrentWeapon = weapon,
                            ZoomComp = zoomComp,
                            Stamp = System.Diagnostics.Stopwatch.GetTimestamp()
                        };
                    }
                }
                catch
                {
                    lock (_zoomSync) _zoom = default;
                }

                HighResDelay(8);
            }
        }

        // data
        public readonly record struct ABIPlayer(
            ulong Pawn, ulong Mesh, ulong Root, string Name, bool IsBot,
            ulong ASC, ulong HealthSet, ulong DeathComp
        );

        public readonly record struct ActorPos(
            ulong Pawn,
            ulong Mesh,
            ulong Root,
            ulong DeathComp,
            Vector3 Position,  // world + bias (from Root CTW)
            FTransform Ctw,    // Mesh CTW (world + bias) for bones
            float LastSubmit,
            float LastOnScreen,
            float Health,
            float HealthMax,
            bool HasFreshSkeleton,
            bool DeadByNativeFlag,
            bool DeadByDeathComp
        )
        {
            public bool IsVisible => (LastOnScreen + ABIOffsetsExt.VIS_TICK) >= LastSubmit;
            public bool IsDead => (HealthMax > 1f && Health <= 0.01f) || DeadByDeathComp;
        }
    }
}
