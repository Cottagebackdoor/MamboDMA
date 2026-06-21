#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MamboDMA.Diagnostics;
using MamboDMA.Services;

namespace MamboDMA.Games.DayZ
{
    public static class DayZUpdater
    {
        private const int NearEntityLimit = 4_096;
        private const int FarEntityLimit = 8_192;
        private const int SlowEntityLimit = 16_384;
        private const int ItemEntityLimit = 32_768;
        private const int EntityScatterChunkSize = 4_096;
        private const int EntityUpdateIntervalMs = 50;
        private const int DiagnosticSampleCount = 3;
        private const int DiagnosticIntervalMs = 5_000;
        private const int LocalPlayerProbeIntervalMs = 10_000;
        private const int LocalPlayerWorldProbeRadius = 0x800;
        private const int LocalPlayerObjectProbeSize = 0x1000;
        private const int LocalPlayerSecondLevelProbeSize = 0x400;
        private const int LocalPlayerSecondLevelPointerLimit = 32;

        private static readonly object LifecycleSync = new();
        private static readonly Dictionary<ulong, ItemTrace> ItemTraces = new();

        private static CancellationTokenSource? _cts;
        private static Task[] _workers = Array.Empty<Task>();
        private static long _nextWorldDiagnostic;
        private static long _nextEntityDiagnostic;
        private static long _nextErrorDiagnostic;
        private static long _nextFrameDiagnostic;
        private static long _nextLocalPlayerProbe;
        private static long _entityPass;
        private static long _framesPublished;
        // Written and read only on the WorldLoop thread (via BuildWorldSnapshot → ResolveLocalPlayer).
        // Not guarded by _frameLock; do not access from CameraLoop, EntityLoop, or consumers.
        private static ulong _localPlayerPathWorld;
        private static ulong _localPlayerPathEntity;
        private static LocalPlayerPath? _localPlayerPath;

        // _entityPass (declared above) is reused for entityHz — do not add a second entity counter.
        private static long _worldPasses;
        private static long _cameraPasses;
        private static long _itemRefreshes;
        // volatile-style access: producers Volatile.Write, readers Volatile.Read for stale-gap math.
        private static long _lastWorldTicks;
        private static long _lastCameraTicks;
        private static long _lastEntityTicks;
        private static long _lastItemRefreshTicks;

        private static readonly RollingSampleWindow _entityPassMs   = new();
        private static readonly RollingSampleWindow _prepareMs      = new();
        private static readonly RollingSampleWindow _metadataExecMs = new();
        private static readonly RollingSampleWindow _positionExecMs = new();
        private static readonly RollingSampleWindow _parseMs        = new();
        private static readonly RollingSampleWindow _worldPassMs    = new();
        private static readonly RollingSampleWindow _cameraPassMs   = new();

        // DayZ-side wrappers only — Memory.cs intentionally untouched so other games are unaffected.
        private static long _directReadCount;
        private static long _directReadTotalTicks;

        // Cached/uncached counts make NOCACHE intent observable and surface accidental drift.
        private static long _scatterCachedCount;
        private static long _scatterUncachedCount;

        // Only the producer thread that wins the throttle CAS reads/writes these — no Interlocked needed.
        private static long _prevFramesPublished;
        private static long _prevWorldPasses;
        private static long _prevCameraPasses;
        private static long _prevEntityPasses;
        private static long _prevItemRefreshes;
        private static long _prevFrameLogTicks;

        // Producer-private latest-value state. The three subsystem loops update
        // their slot under _frameLock and then publish a unified DayZFrameSnapshot
        // so consumers always see a consistent (world, camera, entities) triple.
        private static readonly object _frameLock = new();
        private static DayZSnapshot _latestWorld = DayZSnapshot.Empty;
        private static DayZCamera? _latestCamera;
        private static ImmutableArray<Entity> _latestEntities = ImmutableArray<Entity>.Empty;

        public static ulong WorldPtr => DayZFrameSnapshots.Current.World.WorldAddress;
        public static ulong NetMgrPtr => DayZFrameSnapshots.Current.World.NetworkManagerAddress;

        // Publishes the current latest world+camera+entities triple as a single
        // DayZFrameSnapshot. Lock is held only for the three reads + construction;
        // the actual publication is lock-free Volatile.Write inside the holder.
        private static void RepublishFrame()
        {
            DayZFrameSnapshot frame;
            lock (_frameLock)
            {
                frame = new DayZFrameSnapshot
                {
                    World = _latestWorld,
                    Camera = _latestCamera,
                    Entities = _latestEntities,
                };
            }
            DayZFrameSnapshots.Publish(frame);
            long published = Interlocked.Increment(ref _framesPublished);
            MaybeLogFrameDiagnostic(published, frame);
        }

        private static void MaybeLogFrameDiagnostic(long published, DayZFrameSnapshot frame)
        {
            if (!ShouldLog(ref _nextFrameDiagnostic, 1_000))
                return;

            long nowTicks = Stopwatch.GetTimestamp();
            long prevTicks = Volatile.Read(ref _prevFrameLogTicks);
            double elapsedSec = prevTicks == 0
                ? 0d
                : (nowTicks - prevTicks) / (double)Stopwatch.Frequency;
            Volatile.Write(ref _prevFrameLogTicks, nowTicks);

            long worldPasses    = Interlocked.Read(ref _worldPasses);
            long cameraPasses   = Interlocked.Read(ref _cameraPasses);
            long entityPasses   = Interlocked.Read(ref _entityPass);
            long itemRefreshes  = Interlocked.Read(ref _itemRefreshes);

            double publishHz       = HzFromDelta(published,       ref _prevFramesPublished, elapsedSec);
            double worldHz         = HzFromDelta(worldPasses,     ref _prevWorldPasses,     elapsedSec);
            double cameraHz        = HzFromDelta(cameraPasses,    ref _prevCameraPasses,    elapsedSec);
            double entityHz        = HzFromDelta(entityPasses,    ref _prevEntityPasses,    elapsedSec);
            double itemRefreshHz   = HzFromDelta(itemRefreshes,   ref _prevItemRefreshes,   elapsedSec);

            double worldStaleMs        = StaleMs(nowTicks, Volatile.Read(ref _lastWorldTicks));
            double cameraStaleMs       = StaleMs(nowTicks, Volatile.Read(ref _lastCameraTicks));
            double entityStaleMs       = StaleMs(nowTicks, Volatile.Read(ref _lastEntityTicks));
            double itemRefreshStaleMs  = StaleMs(nowTicks, Volatile.Read(ref _lastItemRefreshTicks));

            int overlayFps = DayZRenderMetrics.LastOverlayFps;
            string overlayFpsField = overlayFps <= 0 ? "warming" : overlayFps.ToString();

            Logger.Info(
                $"[DayZ/Frame] framesPublished={published} publishHz={publishHz:F1} " +
                $"worldHz={worldHz:F1} cameraHz={cameraHz:F1} entityHz={entityHz:F1} " +
                $"itemRefreshHz={itemRefreshHz:F1} overlayFps={overlayFpsField} " +
                $"worldStaleMs={worldStaleMs:F1} cameraStaleMs={cameraStaleMs:F1} " +
                $"entityStaleMs={entityStaleMs:F1} itemRefreshStaleMs={itemRefreshStaleMs:F1} " +
                $"world=0x{frame.World.WorldAddress:X16} " +
                $"camera={(frame.Camera is { IsValid: true })} " +
                $"entities={frame.Entities.Length} " +
                $"players={frame.World.Players} " +
                $"zombies={frame.World.Zombies} " +
                $"cars={frame.World.Cars} " +
                $"items={frame.World.ItemCount}");
        }

        private static double HzFromDelta(long current, ref long previous, double elapsedSec)
        {
            long delta = current - Volatile.Read(ref previous);
            Volatile.Write(ref previous, current);
            if (elapsedSec <= 0d || delta <= 0)
                return 0d;
            return delta / elapsedSec;
        }

        private static double StaleMs(long nowTicks, long lastTicks)
        {
            if (lastTicks <= 0)
                return 0d;
            return (nowTicks - lastTicks) * 1000.0 / Stopwatch.Frequency;
        }

        public static void Start()
        {
            lock (LifecycleSync)
            {
                if (_cts is { IsCancellationRequested: false })
                    return;

                Logger.Info($"[DayZ/Lifecycle] event=start pid={DmaMemory.Pid}");
                Logger.Info(
                    $"[DayZ/Lifecycle] event=attached " +
                    $"pid={DmaMemory.Pid} base=0x{DmaMemory.Base:X16}");

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // Long-running update loops must not occupy the shared JobSystem workers.
                _workers =
                [
                    Task.Run(() => RunLoop("World", WorldLoop, token), token),
                    Task.Run(() => RunLoop("Camera", CameraLoop, token), token),
                    Task.Run(() => RunLoop("Entities", EntityLoop, token), token)
                ];
            }
        }

        public static void Stop()
        {
            CancellationTokenSource? cts;
            Task[] workers;

            lock (LifecycleSync)
            {
                cts = _cts;
                workers = _workers;
                _cts = null;
                _workers = Array.Empty<Task>();
            }

            if (cts is null)
                return;

            long finalFramesPublished = Interlocked.Read(ref _framesPublished);
            Logger.Info(
                $"[DayZ/Lifecycle] event=stop framesPublished={finalFramesPublished}");

            try { cts.Cancel(); } catch { }
            try { Task.WaitAll(workers, 750); } catch { }
            cts.Dispose();

            lock (_frameLock)
            {
                _latestWorld = DayZSnapshot.Empty;
                _latestCamera = null;
                _latestEntities = ImmutableArray<Entity>.Empty;
            }
            DayZFrameSnapshots.Publish(DayZFrameSnapshot.Empty);
            ItemTraces.Clear();
            Interlocked.Exchange(ref _entityPass, 0);
            Interlocked.Exchange(ref _framesPublished, 0);
            Interlocked.Exchange(ref _nextLocalPlayerProbe, 0);
            Interlocked.Exchange(ref _nextFrameDiagnostic, 0);
            Interlocked.Exchange(ref _worldPasses, 0);
            Interlocked.Exchange(ref _cameraPasses, 0);
            Interlocked.Exchange(ref _itemRefreshes, 0);
            Volatile.Write(ref _lastWorldTicks, 0);
            Volatile.Write(ref _lastCameraTicks, 0);
            Volatile.Write(ref _lastEntityTicks, 0);
            Volatile.Write(ref _lastItemRefreshTicks, 0);
            Interlocked.Exchange(ref _directReadCount, 0);
            Interlocked.Exchange(ref _directReadTotalTicks, 0);
            Interlocked.Exchange(ref _scatterCachedCount, 0);
            Interlocked.Exchange(ref _scatterUncachedCount, 0);
            _prevFramesPublished = 0;
            _prevWorldPasses = 0;
            _prevCameraPasses = 0;
            _prevEntityPasses = 0;
            _prevItemRefreshes = 0;
            _prevFrameLogTicks = 0;
            _localPlayerPathWorld = 0;
            _localPlayerPathEntity = 0;
            _localPlayerPath = null;

            Logger.Info($"[DayZ/Lifecycle] event=detached reason=stop");
        }

        private static async Task RunLoop(
            string name,
            Func<CancellationToken, Task> loop,
            CancellationToken token)
        {
            try
            {
                await loop(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.Error($"[DayZ/{name}] worker stopped: {ex}");
            }
        }

        private static async Task WorldLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                long worldStart = Stopwatch.GetTimestamp();
                bool worldOk = false;
                try
                {
                    var snapshot = BuildWorldSnapshot();
                    lock (_frameLock) { _latestWorld = snapshot; }
                    RepublishFrame();
                    MaybeLogWorldSnapshot(snapshot);
                    worldOk = true;
                }
                catch (Exception ex)
                {
                    MaybeLogError("World", ex);
                    var fallback = DayZSnapshot.Empty with
                    {
                        VmmReady = DmaMemory.IsVmmReady,
                        DmaAttached = DmaMemory.IsAttached,
                        ProcessId = DmaMemory.Pid,
                        ModuleBase = DmaMemory.Base
                    };
                    lock (_frameLock) { _latestWorld = fallback; }
                    RepublishFrame();
                }

                _worldPassMs.Add(Stopwatch.GetElapsedTime(worldStart).TotalMilliseconds);
                if (worldOk)
                {
                    Interlocked.Increment(ref _worldPasses);
                    Volatile.Write(ref _lastWorldTicks, Stopwatch.GetTimestamp());
                }

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private static DayZSnapshot BuildWorldSnapshot()
        {
            bool vmmReady = DmaMemory.IsVmmReady;
            bool dmaAttached = DmaMemory.IsAttached;
            uint processId = DmaMemory.Pid;
            ulong moduleBase = DmaMemory.Base;

            if (!dmaAttached || moduleBase == 0)
            {
                return DayZSnapshot.Empty with
                {
                    VmmReady = vmmReady,
                    DmaAttached = dmaAttached,
                    ProcessId = processId,
                    ModuleBase = moduleBase
                };
            }

            ulong worldAddress = moduleBase + DayZOffsets.Module.World;
            ulong networkAddress = moduleBase + DayZOffsets.Module.Network;
            ulong networkManagerAddress = moduleBase + DayZOffsets.Module.NetworkManager;

            TryReadPointer(worldAddress, out ulong world);
            TryReadPointer(networkAddress, out ulong network);
            TryReadPointer(networkManagerAddress, out ulong networkManager);

            ulong camera = 0;
            ulong localPlayerReference = 0;
            ulong localPlayer = 0;
            ulong playerOn = 0;
            Vector3 localPlayerPosition = default;
            string localPlayerResolution = "unresolved";
            TableState near = default;
            TableState far = default;
            TableState slow = default;
            TableState items = default;

            if (IsPlausiblePointer(world))
            {
                TryReadPointer(world + DayZOffsets.World.Camera, out camera);
                TryReadPointer(
                    world + DayZOffsets.World.LocalPlayer,
                    out localPlayerReference);
                TryReadValue(world + DayZOffsets.World.PlayerOn, out playerOn);

                near = ReadFlatTable(
                    world,
                    DayZOffsets.World.NearEntityList,
                    DayZOffsets.World.NearTableSize,
                    NearEntityLimit);
                far = ReadFlatTable(
                    world,
                    DayZOffsets.World.FarEntityList,
                    DayZOffsets.World.FarTableSize,
                    FarEntityLimit);
                slow = ReadStructuredTable(
                    world,
                    DayZOffsets.World.SlowEntityList,
                    DayZOffsets.World.SlowTableSize,
                    DayZOffsets.World.SlowTableValidSize,
                    SlowEntityLimit);
                items = ReadStructuredTable(
                    world,
                    DayZOffsets.World.ItemList,
                    DayZOffsets.World.ItemListSize,
                    DayZOffsets.World.ItemListValidSize,
                    ItemEntityLimit);
            }

            ImmutableArray<Entity> entities;
            lock (_frameLock) { entities = _latestEntities; }
            if (IsPlausiblePointer(world))
            {
                LocalPlayerResult resolved = ResolveLocalPlayer(
                    world,
                    localPlayerReference,
                    entities);
                localPlayer = resolved.EntityPointer;
                localPlayerPosition = resolved.Position;
                localPlayerResolution = resolved.Resolution;
            }

            int playerCount = 0, zombieCount = 0, carCount = 0;
            foreach (Entity entity in entities)
            {
                switch (entity.Category)
                {
                    case EntityType.Player: playerCount++; break;
                    case EntityType.Zombie: zombieCount++; break;
                    case EntityType.Car: carCount++; break;
                }
            }

            return new DayZSnapshot
            {
                VmmReady = vmmReady,
                DmaAttached = dmaAttached,
                Attached = dmaAttached && IsPlausiblePointer(world),
                ProcessId = processId,
                ModuleBase = moduleBase,
                WorldAddress = worldAddress,
                World = world,
                NetworkAddress = networkAddress,
                Network = network,
                NetworkManagerAddress = networkManagerAddress,
                NetworkManager = networkManager,
                Camera = camera,
                LocalPlayerReference = localPlayerReference,
                LocalPlayer = localPlayer,
                LocalPlayerResolution = localPlayerResolution,
                PlayerOn = playerOn,
                LocalPlayerPosition = localPlayerPosition,
                NearTable = near.Pointer,
                NearCount = near.AllocatedCount,
                FarTable = far.Pointer,
                FarCount = far.AllocatedCount,
                SlowTable = slow.Pointer,
                SlowAllocatedCount = slow.AllocatedCount,
                SlowCount = slow.ValidCount,
                ItemTable = items.Pointer,
                ItemAllocatedCount = items.AllocatedCount,
                ItemCount = items.ValidCount,
                Players = playerCount,
                Zombies = zombieCount,
                Cars = carCount,
            };
        }

        private static async Task CameraLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                long cameraStart = Stopwatch.GetTimestamp();
                bool cameraOk = false;
                try
                {
                    DayZSnapshot snapshot;
                    lock (_frameLock) { snapshot = _latestWorld; }

                    DayZCamera? camera = null;
                    if (snapshot.Attached && IsPlausiblePointer(snapshot.Camera))
                        camera = ReadCamera(snapshot.Camera);

                    lock (_frameLock) { _latestCamera = camera; }
                    RepublishFrame();
                    cameraOk = camera is { IsValid: true };
                }
                catch (Exception ex)
                {
                    MaybeLogError("Camera", ex);
                    lock (_frameLock) { _latestCamera = null; }
                    RepublishFrame();
                }

                _cameraPassMs.Add(Stopwatch.GetElapsedTime(cameraStart).TotalMilliseconds);
                if (cameraOk)
                {
                    Interlocked.Increment(ref _cameraPasses);
                    Volatile.Write(ref _lastCameraTicks, Stopwatch.GetTimestamp());
                }

                await Task.Delay(16, token).ConfigureAwait(false);
            }
        }

        private static async Task EntityLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    DayZSnapshot snapshot;
                    lock (_frameLock) { snapshot = _latestWorld; }
                    if (!snapshot.Attached || !IsPlausiblePointer(snapshot.World))
                    {
                        lock (_frameLock) { _latestEntities = ImmutableArray<Entity>.Empty; }
                        RepublishFrame();
                        await Task.Delay(100, token).ConfigureAwait(false);
                        continue;
                    }

                    bool logSamples = ShouldLog(ref _nextEntityDiagnostic, DiagnosticIntervalMs);
                    long pass = Interlocked.Increment(ref _entityPass);
                    long passStart = Stopwatch.GetTimestamp();
                    var gatherResults = new List<EntityGatherResult>(4)
                    {
                        Entity.GatherEntities(
                            "Near",
                            EntityTableMembership.Near,
                            snapshot.NearTable,
                            snapshot.NearCount,
                            logSamples),
                        Entity.GatherEntities(
                            "Far",
                            EntityTableMembership.Far,
                            snapshot.FarTable,
                            snapshot.FarCount,
                            logSamples),
                        Entity.GatherStructuredEntities(
                            "Slow",
                            EntityTableMembership.Slow,
                            snapshot.SlowTable,
                            snapshot.SlowAllocatedCount,
                            snapshot.SlowCount,
                            logSamples),
                        Entity.GatherStructuredEntities(
                            "Item",
                            EntityTableMembership.Item,
                            snapshot.ItemTable,
                            snapshot.ItemAllocatedCount,
                            snapshot.ItemCount,
                            logSamples)
                    };

                    int candidateCount = gatherResults.Sum(result => result.Candidates.Count);
                    int rejected = gatherResults.Sum(result => result.RejectedPointers);
                    List<EntityCandidate> candidates = DeduplicateCandidates(gatherResults);
                    var scatterMetrics = new EntityScatterMetrics
                    {
                        Pass = pass,
                        Candidates = candidateCount,
                        Deduplicated = candidates.Count,
                        Rejected = rejected
                    };

                    Entity[] completeSnapshot = ParseCandidatesBatched(
                        candidates,
                        scatterMetrics);
                    var parsedByPointer = completeSnapshot.ToDictionary(entity => entity.Ptr);
                    var itemPointers = candidates
                        .Where(candidate => candidate.IsItemMember)
                        .Select(candidate => candidate.Pointer)
                        .ToHashSet();
                    var itemEntities = completeSnapshot
                        .Where(entity => itemPointers.Contains(entity.Ptr))
                        .ToArray();

                    // Wrap the producer's Entity[] zero-copy as ImmutableArray<Entity>.
                    // Safe because completeSnapshot is local to this iteration and is
                    // not mutated after this point.
                    ImmutableArray<Entity> publishedEntities =
                        ImmutableCollectionsMarshal.AsImmutableArray(completeSnapshot);

                    int playerCount = 0, zombieCount = 0, carCount = 0;
                    foreach (Entity entity in completeSnapshot)
                    {
                        switch (entity.Category)
                        {
                            case EntityType.Player: playerCount++; break;
                            case EntityType.Zombie: zombieCount++; break;
                            case EntityType.Car: carCount++; break;
                        }
                    }

                    lock (_frameLock)
                    {
                        _latestEntities = publishedEntities;
                        _latestWorld = _latestWorld with
                        {
                            Players = playerCount,
                            Zombies = zombieCount,
                            Cars = carCount,
                        };
                    }
                    RepublishFrame();

                    TrackItems(itemEntities, logSamples, pass);

                    scatterMetrics.TotalMs = Stopwatch.GetElapsedTime(passStart).TotalMilliseconds;

                    _entityPassMs.Add(scatterMetrics.TotalMs);
                    _prepareMs.Add(scatterMetrics.PrepareMs);
                    _metadataExecMs.Add(scatterMetrics.MetadataExecuteMs);
                    _positionExecMs.Add(scatterMetrics.PositionExecuteMs);
                    _parseMs.Add(scatterMetrics.ParseMs);
                    Volatile.Write(ref _lastEntityTicks, Stopwatch.GetTimestamp());
                    // Item table is gathered every entity-loop pass; itemRefreshHz exposes that cadence as a real number.
                    Interlocked.Increment(ref _itemRefreshes);
                    Volatile.Write(ref _lastItemRefreshTicks, Stopwatch.GetTimestamp());

                    if (logSamples)
                    {
                        LogGatherDiagnostics(gatherResults, parsedByPointer);
                        LogRepresentativeEntities(
                            gatherResults,
                            scatterMetrics.ParsedByPointer);
                        Logger.Info(
                            $"[DayZ/Scatter] pass={scatterMetrics.Pass} " +
                            $"candidates={scatterMetrics.Candidates} " +
                            $"deduplicated={scatterMetrics.Deduplicated} " +
                            $"chunks={scatterMetrics.Chunks} " +
                            $"metadataPrepared={scatterMetrics.MetadataPrepared} " +
                            $"metadataOk={scatterMetrics.MetadataOk} " +
                            $"visualStateOk={scatterMetrics.VisualStateOk} " +
                            $"futureVisualFallbacks={scatterMetrics.FutureVisualFallbacks} " +
                            $"positionsPrepared={scatterMetrics.PositionsPrepared} " +
                            $"positionsOk={scatterMetrics.PositionsOk} " +
                            $"positionFallbacks={scatterMetrics.PositionFallbacks} " +
                            $"rejected={scatterMetrics.Rejected} " +
                            $"prepareMs={scatterMetrics.PrepareMs:F2} " +
                            $"metadataExecuteMs={scatterMetrics.MetadataExecuteMs:F2} " +
                            $"positionExecuteMs={scatterMetrics.PositionExecuteMs:F2} " +
                            $"parseMs={scatterMetrics.ParseMs:F2} " +
                            $"totalMs={scatterMetrics.TotalMs:F2}");
                        Logger.Info(
                            $"[DayZ/Perf] entityPass={scatterMetrics.TotalMs:F2}ms " +
                            $"published={completeSnapshot.Length} itemEntities={itemEntities.Length}");

                        var entityPassStats   = _entityPassMs.SnapshotAndReset();
                        var worldPassStats    = _worldPassMs.SnapshotAndReset();
                        var cameraPassStats   = _cameraPassMs.SnapshotAndReset();
                        var prepareStats      = _prepareMs.SnapshotAndReset();
                        var metadataExecStats = _metadataExecMs.SnapshotAndReset();
                        var positionExecStats = _positionExecMs.SnapshotAndReset();
                        var parseStats        = _parseMs.SnapshotAndReset();

                        long directReads      = Interlocked.Read(ref _directReadCount);
                        long directReadTicks  = Interlocked.Read(ref _directReadTotalTicks);
                        double directReadMs   = directReadTicks * 1000.0 / Stopwatch.Frequency;
                        long scatterCached    = Interlocked.Read(ref _scatterCachedCount);
                        long scatterUncached  = Interlocked.Read(ref _scatterUncachedCount);

                        Logger.Info(
                            $"[DayZ/PerfSummary] " +
                            $"entityPass={{{entityPassStats}}} " +
                            $"worldPass={{{worldPassStats}}} " +
                            $"cameraPass={{{cameraPassStats}}} " +
                            $"prepare={{{prepareStats}}} " +
                            $"metadataExec={{{metadataExecStats}}} " +
                            $"positionExec={{{positionExecStats}}} " +
                            $"parse={{{parseStats}}} " +
                            $"directReads={directReads} totalDirectReadMs={directReadMs:F2} " +
                            $"scatterCached={scatterCached} scatterUncached={scatterUncached}");
                    }
                }
                catch (Exception ex)
                {
                    MaybeLogError("Entities", ex);
                }

                await Task.Delay(EntityUpdateIntervalMs, token).ConfigureAwait(false);
            }
        }

        private static List<EntityCandidate> DeduplicateCandidates(
            IReadOnlyList<EntityGatherResult> gatherResults)
        {
            var byPointer = new Dictionary<ulong, EntityCandidate>();
            var ordered = new List<EntityCandidate>();

            foreach (EntityGatherResult result in gatherResults)
            {
                foreach (EntityCandidateOccurrence occurrence in result.Candidates)
                {
                    if (byPointer.TryGetValue(occurrence.Pointer, out EntityCandidate? existing))
                    {
                        existing.Memberships |= occurrence.Membership;
                        continue;
                    }

                    var candidate = new EntityCandidate(
                        occurrence.Pointer,
                        occurrence.SourceTable,
                        occurrence.SourceIndex,
                        occurrence.Membership);
                    byPointer.Add(candidate.Pointer, candidate);
                    ordered.Add(candidate);
                }
            }

            return ordered;
        }

        private static Entity[] ParseCandidatesBatched(
            IReadOnlyList<EntityCandidate> candidates,
            EntityScatterMetrics metrics)
        {
            if (candidates.Count == 0)
                return Array.Empty<Entity>();

            var parsed = new List<Entity>(candidates.Count);
            for (int chunkStart = 0;
                 chunkStart < candidates.Count;
                 chunkStart += EntityScatterChunkSize)
            {
                int chunkCount = Math.Min(
                    EntityScatterChunkSize,
                    candidates.Count - chunkStart);
                metrics.Chunks++;

                var states = new EntityReadState[chunkCount];
                for (int index = 0; index < chunkCount; index++)
                    states[index] = new EntityReadState(candidates[chunkStart + index]);

                Interlocked.Increment(ref _scatterUncachedCount);
                using (DmaMemory.DmaScatter metadataScatter = DmaMemory.Scatter(useCache: false))
                {
                    int preparedMetadataOperations = 0;
                    long prepareStart = Stopwatch.GetTimestamp();
                    foreach (EntityReadState state in states)
                    {
                        ulong pointer = state.Candidate.Pointer;
                        state.TypePrepared = metadataScatter.PrepareReadValue<ulong>(
                            pointer + DayZOffsets.Entity.Type);
                        state.VisualStatePrepared = metadataScatter.PrepareReadValue<ulong>(
                            pointer + DayZOffsets.Entity.VisualState);
                        state.FutureVisualStatePrepared = metadataScatter.PrepareReadValue<ulong>(
                            pointer + DayZOffsets.Entity.FutureVisualState);
                        state.NetworkIdPrepared = metadataScatter.PrepareReadValue<uint>(
                            pointer + DayZOffsets.Entity.NetworkId);
                        state.IsDeadPrepared = metadataScatter.PrepareReadValue<byte>(
                            pointer + DayZOffsets.Entity.IsDead);
                        state.EntityDeadPrepared = metadataScatter.PrepareReadValue<byte>(
                            pointer + DayZOffsets.Entity.EntityDead);

                        preparedMetadataOperations +=
                            CountPreparedMetadataOperations(state);
                    }
                    metrics.MetadataPrepared += preparedMetadataOperations;
                    metrics.PrepareMs += Stopwatch.GetElapsedTime(
                        prepareStart).TotalMilliseconds;

                    if (preparedMetadataOperations > 0)
                    {
                        long executeStart = Stopwatch.GetTimestamp();
                        metadataScatter.Execute();
                        metrics.MetadataExecuteMs += Stopwatch.GetElapsedTime(
                            executeStart).TotalMilliseconds;
                    }

                    foreach (EntityReadState state in states)
                    {
                        ulong pointer = state.Candidate.Pointer;
                        ulong typePointer = 0;
                        ulong visualStatePointer = 0;
                        ulong futureVisualStatePointer = 0;
                        uint networkId = 0;
                        byte isDead = 0;
                        byte entityDead = 0;
                        bool typeOk =
                            state.TypePrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.Type,
                                out typePointer);
                        bool visualOk =
                            state.VisualStatePrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.VisualState,
                                out visualStatePointer);
                        bool futureVisualOk =
                            state.FutureVisualStatePrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.FutureVisualState,
                                out futureVisualStatePointer);
                        bool networkOk =
                            state.NetworkIdPrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.NetworkId,
                                out networkId);
                        bool isDeadOk =
                            state.IsDeadPrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.IsDead,
                                out isDead);
                        bool entityDeadOk =
                            state.EntityDeadPrepared &&
                            metadataScatter.ReadValue(
                                pointer + DayZOffsets.Entity.EntityDead,
                                out entityDead);

                        metrics.MetadataOk +=
                            (typeOk ? 1 : 0) +
                            (visualOk ? 1 : 0) +
                            (futureVisualOk ? 1 : 0) +
                            (networkOk ? 1 : 0) +
                            (isDeadOk ? 1 : 0) +
                            (entityDeadOk ? 1 : 0);

                        state.TypePointer =
                            typeOk && IsPlausiblePointer(typePointer)
                                ? typePointer
                                : 0;
                        state.NetworkId = networkOk ? networkId : 0;
                        state.IsDead =
                            (isDeadOk && isDead != 0) ||
                            (entityDeadOk && entityDead != 0);

                        if (visualOk && IsPlausiblePointer(visualStatePointer))
                        {
                            state.VisualStatePointer = visualStatePointer;
                        }
                        else if (futureVisualOk &&
                                 IsPlausiblePointer(futureVisualStatePointer))
                        {
                            state.VisualStatePointer = futureVisualStatePointer;
                            state.UsedFutureVisualState = true;
                            metrics.FutureVisualFallbacks++;
                        }

                        if (IsPlausiblePointer(state.VisualStatePointer))
                            metrics.VisualStateOk++;
                    }
                }

                DmaMemory.DmaScatter? positionScatter = null;
                try
                {
                    int preparedPositionOperations = 0;
                    if (states.Any(state => IsPlausiblePointer(state.VisualStatePointer)))
                    {
                        Interlocked.Increment(ref _scatterUncachedCount);
                        positionScatter = DmaMemory.Scatter(useCache: false);
                        long prepareStart = Stopwatch.GetTimestamp();
                        foreach (EntityReadState state in states)
                        {
                            if (!IsPlausiblePointer(state.VisualStatePointer))
                                continue;

                            state.PositionPrepared = positionScatter.PrepareReadValue<Vector3>(
                                state.VisualStatePointer + DayZOffsets.VisualState.Position);
                            if (state.PositionPrepared)
                                preparedPositionOperations++;
                        }
                        metrics.PositionsPrepared += preparedPositionOperations;
                        metrics.PrepareMs += Stopwatch.GetElapsedTime(
                            prepareStart).TotalMilliseconds;

                        if (preparedPositionOperations > 0)
                        {
                            long executeStart = Stopwatch.GetTimestamp();
                            positionScatter.Execute();
                            metrics.PositionExecuteMs += Stopwatch.GetElapsedTime(
                                executeStart).TotalMilliseconds;
                        }
                    }

                    long parseStart = Stopwatch.GetTimestamp();
                    foreach (EntityReadState state in states)
                    {
                        Entity entity;
                        try
                        {
                            entity = ParseScatteredEntity(state, positionScatter, metrics);
                        }
                        catch (Exception ex)
                        {
                            entity = new Entity
                            {
                                Ptr = state.Candidate.Pointer,
                                SourceTable = state.Candidate.SourceTable,
                                SourceIndex = state.Candidate.SourceIndex,
                                Validation = $"parse exception: {ex.GetType().Name}"
                            };
                        }

                        metrics.ParsedByPointer[state.Candidate.Pointer] = entity;
                        if (entity.IsValid)
                            parsed.Add(entity);
                        else
                            metrics.Rejected++;
                    }
                    metrics.ParseMs += Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;
                }
                finally
                {
                    positionScatter?.Dispose();
                }
            }

            return parsed.ToArray();
        }

        private static int CountPreparedMetadataOperations(EntityReadState state)
            => (state.TypePrepared ? 1 : 0) +
               (state.VisualStatePrepared ? 1 : 0) +
               (state.FutureVisualStatePrepared ? 1 : 0) +
               (state.NetworkIdPrepared ? 1 : 0) +
               (state.IsDeadPrepared ? 1 : 0) +
               (state.EntityDeadPrepared ? 1 : 0);

        private static Entity ParseScatteredEntity(
            EntityReadState state,
            DmaMemory.DmaScatter? positionScatter,
            EntityScatterMetrics metrics)
        {
            var entity = new Entity
            {
                Ptr = state.Candidate.Pointer,
                SourceTable = state.Candidate.SourceTable,
                SourceIndex = state.Candidate.SourceIndex,
                TypePtr = state.TypePointer,
                VisualStatePtr = state.VisualStatePointer,
                NetworkId = state.NetworkId,
                IsDead = state.IsDead
            };
            var problems = new List<string>(4);

            if (!IsPlausiblePointer(entity.TypePtr))
            {
                problems.Add("invalid type pointer");
            }
            else
            {
                TryReadArmaStringField(
                    entity.TypePtr,
                    DayZOffsets.HumanType.ObjectName,
                    out entity.ObjectNamePtr,
                    out entity.TypeName);
                TryReadArmaStringField(
                    entity.TypePtr,
                    DayZOffsets.HumanType.ModelNameUnverified,
                    out _,
                    out entity.ModelName);
                TryReadArmaStringField(
                    entity.TypePtr,
                    DayZOffsets.HumanType.CategoryName,
                    out entity.CategoryNamePtr,
                    out entity.ConfigName);
                TryReadArmaStringField(
                    entity.TypePtr,
                    DayZOffsets.HumanType.CleanName,
                    out entity.CleanNamePtr,
                    out entity.CleanName);

                if (string.IsNullOrWhiteSpace(entity.CleanName))
                {
                    TryReadArmaStringField(
                        entity.TypePtr,
                        DayZOffsets.HumanType.CleanNameInternal,
                        out entity.CleanNamePtr,
                        out entity.CleanName);
                }

                if (string.IsNullOrWhiteSpace(entity.TypeName) &&
                    string.IsNullOrWhiteSpace(entity.ConfigName) &&
                    string.IsNullOrWhiteSpace(entity.CleanName))
                {
                    problems.Add("no valid type names");
                }
            }

            bool primaryPositionOk =
                state.PositionPrepared &&
                positionScatter is not null &&
                positionScatter.ReadValue(
                    state.VisualStatePointer + DayZOffsets.VisualState.Position,
                    out entity.Position) &&
                IsPlausiblePosition(entity.Position);
            if (primaryPositionOk)
            {
                entity.PositionReadMode = state.UsedFutureVisualState
                    ? "future-visual-state+0x2C"
                    : "visual-state+0x2C";
                metrics.PositionsOk++;
            }
            else if (IsPlausiblePointer(state.VisualStatePointer) &&
                     TryReadEntityPositionFallback(
                         state.VisualStatePointer,
                         out entity.Position,
                         out entity.PositionReadMode))
            {
                metrics.PositionFallbacks++;
            }
            else
            {
                problems.Add("invalid visual state/position");
            }

            entity.Categorize();
            entity.IsValid = problems.Count == 0;
            entity.Validation = entity.IsValid ? "valid" : string.Join("; ", problems);
            return entity;
        }

        private static LocalPlayerResult ResolveLocalPlayer(
            ulong world,
            ulong reference,
            IReadOnlyList<Entity> entities)
        {
            Entity[] players = entities
                .Where(entity =>
                    entity.IsValid &&
                    string.Equals(
                        entity.ConfigName,
                        "dayzplayer",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (_localPlayerPathWorld != world)
            {
                _localPlayerPathWorld = world;
                _localPlayerPathEntity = 0;
                _localPlayerPath = null;
                Interlocked.Exchange(ref _nextLocalPlayerProbe, 0);
            }

            var confirmedPath = new LocalPlayerPath(
                LocalPlayerPathKind.ReferenceChild,
                DayZOffsets.World.LocalPlayerEntityReference,
                0,
                true);
            if (TryResolveConfirmedLocalPlayer(
                    reference,
                    players,
                    confirmedPath,
                    out LocalPlayerResult confirmedResult))
            {
                CacheLocalPlayerPath(
                    confirmedPath,
                    confirmedResult.EntityPointer,
                    confirmedResult.Position,
                    reference);
                return confirmedResult;
            }

            if (players.Length == 0)
                return new LocalPlayerResult(0, default, "unresolved: no parsed dayzplayer");

            if (_localPlayerPath is { } cachedPath &&
                cachedPath != confirmedPath &&
                TryResolveLocalPlayerPath(
                    world,
                    reference,
                    players,
                    cachedPath,
                    out Entity? cachedPlayer))
            {
                return ResultFromEntity(cachedPlayer!, cachedPath.Description);
            }

            _localPlayerPath = null;

            if (TryMatchPlayerPointer(
                    reference,
                    players,
                    out Entity? directPlayer,
                    out bool directAdjusted))
            {
                var path = new LocalPlayerPath(
                    LocalPlayerPathKind.WorldField,
                    DayZOffsets.World.LocalPlayer,
                    0,
                    directAdjusted);
                CacheLocalPlayerPath(
                    path,
                    directPlayer!.Ptr,
                    directPlayer.Position,
                    reference);
                return ResultFromEntity(directPlayer!, path.Description);
            }

            if (ShouldLog(ref _nextLocalPlayerProbe, LocalPlayerProbeIntervalMs))
            {
                if (TryDiscoverLocalPlayerPath(
                        world,
                        reference,
                        players,
                        out LocalPlayerPath discoveredPath,
                        out Entity? discoveredPlayer))
                {
                    CacheLocalPlayerPath(
                        discoveredPath,
                        discoveredPlayer!.Ptr,
                        discoveredPlayer.Position,
                        reference);
                    return ResultFromEntity(discoveredPlayer!, discoveredPath.Description);
                }

                LogUnresolvedLocalPlayerProbe(reference, players);
            }

            return SelectParsedPlayerFallback(players);
        }

        private static bool TryResolveConfirmedLocalPlayer(
            ulong reference,
            IReadOnlyList<Entity> players,
            LocalPlayerPath path,
            out LocalPlayerResult result)
        {
            result = default;
            if (!IsPlausiblePointer(reference) ||
                !TryReadPointer(reference + path.PrimaryOffset, out ulong storedValue) ||
                !TryApplyLocalOffset(storedValue, out ulong entityPointer))
            {
                return false;
            }

            if (TryFindPlayer(players, entityPointer, out Entity? tablePlayer))
            {
                result = ResultFromEntity(tablePlayer!, path.Description);
                return true;
            }

            if (!TryValidateDayZPlayerEntity(entityPointer, out Vector3 position))
                return false;

            result = new LocalPlayerResult(
                entityPointer,
                position,
                $"{path.Description} (directly validated; table pending)");
            return true;
        }

        private static bool TryValidateDayZPlayerEntity(
            ulong entityPointer,
            out Vector3 position)
        {
            position = default;
            if (!TryReadPointer(
                    entityPointer + DayZOffsets.Entity.Type,
                    out ulong typePointer) ||
                !TryReadArmaStringField(
                    typePointer,
                    DayZOffsets.HumanType.CategoryName,
                    out _,
                    out string category) ||
                !string.Equals(
                    category,
                    "dayzplayer",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TryReadEntityPosition(entityPointer, out position, out _, out _);
        }

        private static bool TryResolveLocalPlayerPath(
            ulong world,
            ulong reference,
            IReadOnlyList<Entity> players,
            LocalPlayerPath path,
            out Entity? player)
        {
            player = null;

            switch (path.Kind)
            {
                case LocalPlayerPathKind.WorldField:
                    if (!TryReadPointer(world + path.PrimaryOffset, out ulong worldValue))
                        return false;

                    return TryFindPathPlayer(worldValue, players, path.ApplyLocalOffset, out player);

                case LocalPlayerPathKind.ReferenceChild:
                    if (!IsPlausiblePointer(reference) ||
                        !TryReadPointer(reference + path.PrimaryOffset, out ulong childValue))
                    {
                        return false;
                    }

                    return TryFindPathPlayer(childValue, players, path.ApplyLocalOffset, out player);

                case LocalPlayerPathKind.ReferenceGrandchild:
                    if (!IsPlausiblePointer(reference) ||
                        !TryReadPointer(reference + path.PrimaryOffset, out ulong child) ||
                        !TryReadPointer(child + path.SecondaryOffset, out ulong grandchildValue))
                    {
                        return false;
                    }

                    return TryFindPathPlayer(
                        grandchildValue,
                        players,
                        path.ApplyLocalOffset,
                        out player);

                case LocalPlayerPathKind.PlayerFieldReference:
                    if (!IsPlausiblePointer(reference))
                        return false;

                    foreach (Entity candidate in players)
                    {
                        if (TryReadPointer(
                                candidate.Ptr + path.PrimaryOffset,
                                out ulong playerField) &&
                            playerField == reference)
                        {
                            player = candidate;
                            return true;
                        }
                    }

                    return false;

                default:
                    return false;
            }
        }

        private static bool TryDiscoverLocalPlayerPath(
            ulong world,
            ulong reference,
            IReadOnlyList<Entity> players,
            out LocalPlayerPath path,
            out Entity? player)
        {
            path = default;
            player = null;

            ulong probeRadius = (ulong)LocalPlayerWorldProbeRadius;
            ulong worldProbeStart =
                DayZOffsets.World.LocalPlayer - probeRadius;
            int worldProbeQwords =
                ((LocalPlayerWorldProbeRadius * 2) / sizeof(ulong)) + 1;

            if (TryReadQwords(
                    world + worldProbeStart,
                    worldProbeQwords,
                    out ulong[] worldValues))
            {
                int expectedIndex =
                    LocalPlayerWorldProbeRadius / sizeof(ulong);

                foreach (int index in Enumerable.Range(0, worldValues.Length)
                             .OrderBy(index => Math.Abs(index - expectedIndex)))
                {
                    if (!TryMatchPlayerPointer(
                            worldValues[index],
                            players,
                            out player,
                            out bool adjusted))
                    {
                        continue;
                    }

                    path = new LocalPlayerPath(
                        LocalPlayerPathKind.WorldField,
                        worldProbeStart + ((ulong)index * sizeof(ulong)),
                        0,
                        adjusted);
                    return true;
                }
            }

            if (IsPlausiblePointer(reference) &&
                TryReadQwords(
                    reference,
                    LocalPlayerObjectProbeSize / sizeof(ulong),
                    out ulong[] referenceValues))
            {
                for (int index = 0; index < referenceValues.Length; index++)
                {
                    if (!TryMatchPlayerPointer(
                            referenceValues[index],
                            players,
                            out player,
                            out bool adjusted))
                    {
                        continue;
                    }

                    path = new LocalPlayerPath(
                        LocalPlayerPathKind.ReferenceChild,
                        (ulong)index * sizeof(ulong),
                        0,
                        adjusted);
                    return true;
                }

                var probedChildren = new HashSet<ulong>();
                int childCount = 0;
                for (int firstIndex = 0;
                     firstIndex < referenceValues.Length &&
                     childCount < LocalPlayerSecondLevelPointerLimit;
                     firstIndex++)
                {
                    ulong child = referenceValues[firstIndex];
                    if (!IsPlausiblePointer(child) || !probedChildren.Add(child))
                        continue;

                    childCount++;
                    if (!TryReadQwords(
                            child,
                            LocalPlayerSecondLevelProbeSize / sizeof(ulong),
                            out ulong[] childValues))
                    {
                        continue;
                    }

                    for (int secondIndex = 0; secondIndex < childValues.Length; secondIndex++)
                    {
                        if (!TryMatchPlayerPointer(
                                childValues[secondIndex],
                                players,
                                out player,
                                out bool adjusted))
                        {
                            continue;
                        }

                        path = new LocalPlayerPath(
                            LocalPlayerPathKind.ReferenceGrandchild,
                            (ulong)firstIndex * sizeof(ulong),
                            (ulong)secondIndex * sizeof(ulong),
                            adjusted);
                        return true;
                    }
                }
            }

            if (IsPlausiblePointer(reference))
            {
                foreach (Entity candidate in players)
                {
                    if (!TryReadQwords(
                            candidate.Ptr,
                            LocalPlayerObjectProbeSize / sizeof(ulong),
                            out ulong[] playerValues))
                    {
                        continue;
                    }

                    for (int index = 0; index < playerValues.Length; index++)
                    {
                        if (playerValues[index] != reference)
                            continue;

                        player = candidate;
                        path = new LocalPlayerPath(
                            LocalPlayerPathKind.PlayerFieldReference,
                            (ulong)index * sizeof(ulong),
                            0,
                            false);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryMatchPlayerPointer(
            ulong storedValue,
            IReadOnlyList<Entity> players,
            out Entity? player,
            out bool adjusted)
        {
            adjusted = false;
            if (TryFindPlayer(players, storedValue, out player))
                return true;

            if (!TryApplyLocalOffset(storedValue, out ulong adjustedValue) ||
                !TryFindPlayer(players, adjustedValue, out player))
            {
                return false;
            }

            adjusted = true;
            return true;
        }

        private static bool TryFindPathPlayer(
            ulong storedValue,
            IReadOnlyList<Entity> players,
            bool applyLocalOffset,
            out Entity? player)
        {
            if (applyLocalOffset)
            {
                if (!TryApplyLocalOffset(storedValue, out storedValue))
                {
                    player = null;
                    return false;
                }
            }

            return TryFindPlayer(players, storedValue, out player);
        }

        private static bool TryFindPlayer(
            IReadOnlyList<Entity> players,
            ulong pointer,
            out Entity? player)
        {
            foreach (Entity candidate in players)
            {
                if (candidate.Ptr == pointer)
                {
                    player = candidate;
                    return true;
                }
            }

            player = null;
            return false;
        }

        private static bool TryApplyLocalOffset(ulong pointer, out ulong adjusted)
        {
            adjusted = 0;
            if (!IsPlausiblePointer(pointer))
                return false;

            long signedPointer = (long)pointer;
            long signedAdjusted;
            try
            {
                signedAdjusted = checked(signedPointer + DayZOffsets.World.LocalOffset);
            }
            catch (OverflowException)
            {
                return false;
            }

            adjusted = (ulong)signedAdjusted;
            return IsPlausiblePointer(adjusted);
        }

        private static bool TryReadQwords(
            ulong address,
            int count,
            out ulong[] values)
        {
            values = Array.Empty<ulong>();
            if (!IsPlausiblePointer(address) || count <= 0)
                return false;

            try
            {
                values = DmaMemory.ReadArray<ulong>(address, count) ?? Array.Empty<ulong>();
                return values.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static LocalPlayerResult SelectParsedPlayerFallback(
            IReadOnlyList<Entity> players)
        {
            DayZCamera? camera;
            lock (_frameLock) { camera = _latestCamera; }
            if (camera is { IsValid: true })
            {
                Entity? nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (Entity candidate in players)
                {
                    float x = camera.InvertedViewTranslation.X - candidate.Position.X;
                    float z = camera.InvertedViewTranslation.Z - candidate.Position.Z;
                    float distance = MathF.Sqrt((x * x) + (z * z));
                    if (distance < nearestDistance)
                    {
                        nearest = candidate;
                        nearestDistance = distance;
                    }
                }

                if (nearest is not null && nearestDistance <= 3f)
                {
                    return ResultFromEntity(
                        nearest,
                        $"fallback: parsed dayzplayer nearest camera ({nearestDistance:F2}m)");
                }
            }

            Entity fallback = players
                .OrderBy(player =>
                    string.Equals(
                        player.SourceTable,
                        "Near",
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(player => player.SourceIndex)
                .First();

            string reason = players.Count == 1
                ? "fallback: only parsed dayzplayer"
                : $"fallback: first parsed dayzplayer ({players.Count} candidates)";
            return ResultFromEntity(fallback, reason);
        }

        private static LocalPlayerResult ResultFromEntity(
            Entity player,
            string resolution)
            => new(player.Ptr, player.Position, resolution);

        private static void CacheLocalPlayerPath(
            LocalPlayerPath path,
            ulong entityPointer,
            Vector3 position,
            ulong reference)
        {
            bool changed =
                _localPlayerPath != path ||
                _localPlayerPathEntity != entityPointer;
            _localPlayerPath = path;
            _localPlayerPathEntity = entityPointer;
            if (!changed)
                return;

            Logger.Info(
                $"[DayZ/LocalPlayerProbe] resolved reference=0x{reference:X} " +
                $"entity=0x{entityPointer:X} path=\"{path.Description}\" " +
                $"position={FormatVector(position)}");
        }

        private static void LogUnresolvedLocalPlayerProbe(
            ulong reference,
            IReadOnlyList<Entity> players)
        {
            string candidates = string.Join(
                ", ",
                players.Take(4).Select(player =>
                    $"0x{player.Ptr:X}@{player.SourceTable}[{player.SourceIndex}]" +
                    $"{FormatVector(player.Position)}"));
            string referencePointers = DescribePointerSlots(reference, 16);

            Logger.Warn(
                $"[DayZ/LocalPlayerProbe] unresolved reference=0x{reference:X}; " +
                $"tested World+0x{DayZOffsets.World.LocalPlayer - (ulong)LocalPlayerWorldProbeRadius:X}" +
                $"..0x{DayZOffsets.World.LocalPlayer + (ulong)LocalPlayerWorldProbeRadius:X}, " +
                $"reference depth <= 2, and player-owned fields through " +
                $"+0x{LocalPlayerObjectProbeSize:X}; candidates=[{candidates}]; " +
                $"referencePointers=[{referencePointers}]");
        }

        private static string DescribePointerSlots(ulong address, int maximumSlots)
        {
            if (!TryReadQwords(address, 64, out ulong[] values))
                return "unreadable";

            return string.Join(
                ", ",
                values
                    .Select((value, index) => new { value, index })
                    .Where(slot => IsPlausiblePointer(slot.value))
                    .Take(maximumSlots)
                    .Select(slot =>
                        $"+0x{slot.index * sizeof(ulong):X}=0x{slot.value:X}"));
        }

        private static TableState ReadFlatTable(
            ulong world,
            ulong listOffset,
            ulong countOffset,
            int maximumCount)
        {
            TryReadPointer(world + listOffset, out ulong pointer);
            if (!TryReadValue(world + countOffset, out int rawCount))
                return new TableState(pointer, 0, 0);

            int count = Math.Clamp(rawCount, 0, maximumCount);
            if (count != 0 && !IsPlausiblePointer(pointer))
                count = 0;

            return new TableState(pointer, count, count);
        }

        private static TableState ReadStructuredTable(
            ulong world,
            ulong listOffset,
            ulong allocatedCountOffset,
            ulong validCountOffset,
            int maximumCount)
        {
            TryReadPointer(world + listOffset, out ulong pointer);

            int allocatedCount = 0;
            if (TryReadValue(world + allocatedCountOffset, out int rawAllocatedCount))
                allocatedCount = Math.Clamp(rawAllocatedCount, 0, maximumCount);

            int validCount = -1;
            if (TryReadValue(world + validCountOffset, out int rawValidCount) &&
                rawValidCount >= 0 &&
                rawValidCount <= allocatedCount)
            {
                validCount = rawValidCount;
            }

            if (allocatedCount != 0 && !IsPlausiblePointer(pointer))
            {
                allocatedCount = 0;
                validCount = 0;
            }

            return new TableState(pointer, allocatedCount, validCount);
        }

        public sealed class DayZCamera
        {
            public ulong Pointer;
            public uint ViewMatrixSelector;
            public Vector3 ViewportSize;
            public Vector3 ProjectionD1;
            public Vector3 ProjectionD2;
            public Vector3 InvertedViewRight;
            public Vector3 InvertedViewUp;
            public Vector3 InvertedViewForward;
            public Vector3 InvertedViewTranslation;
            public bool IsValid;
        }

        public static DayZCamera ReadCamera(ulong cameraPointer)
        {
            var camera = new DayZCamera { Pointer = cameraPointer };

            TryReadValue(cameraPointer + DayZOffsets.Camera.ViewMatrix, out camera.ViewMatrixSelector);
            TryReadValue(cameraPointer + DayZOffsets.Camera.ViewportSize, out camera.ViewportSize);
            TryReadValue(cameraPointer + DayZOffsets.Camera.ProjectionD1Unverified, out camera.ProjectionD1);
            TryReadValue(cameraPointer + DayZOffsets.Camera.ProjectionD2, out camera.ProjectionD2);
            TryReadValue(cameraPointer + DayZOffsets.Camera.InvertedViewRight, out camera.InvertedViewRight);
            TryReadValue(cameraPointer + DayZOffsets.Camera.InvertedViewUp, out camera.InvertedViewUp);
            TryReadValue(cameraPointer + DayZOffsets.Camera.InvertedViewForward, out camera.InvertedViewForward);
            TryReadValue(cameraPointer + DayZOffsets.Camera.InvertedViewTranslation, out camera.InvertedViewTranslation);

            camera.IsValid =
                IsFinite(camera.ViewportSize) &&
                camera.ViewportSize.X > 0f &&
                camera.ViewportSize.Y > 0f &&
                IsFinite(camera.ProjectionD1) &&
                IsFinite(camera.ProjectionD2) &&
                MathF.Abs(camera.ProjectionD1.X) > 0.0001f &&
                MathF.Abs(camera.ProjectionD2.Y) > 0.0001f &&
                IsFinite(camera.InvertedViewRight) &&
                IsFinite(camera.InvertedViewUp) &&
                IsFinite(camera.InvertedViewForward) &&
                IsFinite(camera.InvertedViewTranslation);

            return camera;
        }

        public static bool WorldToScreenDayZ(
            DayZCamera? camera,
            Vector3 worldPosition,
            Vector2 screenSize,
            out Vector2 screenPosition)
        {
            screenPosition = Vector2.Zero;
            if (camera is null || !camera.IsValid || !IsPlausiblePosition(worldPosition))
                return false;

            Vector3 relative = worldPosition - camera.InvertedViewTranslation;
            float x = Vector3.Dot(relative, camera.InvertedViewRight);
            float y = Vector3.Dot(relative, camera.InvertedViewUp);
            float z = Vector3.Dot(relative, camera.InvertedViewForward);

            if (!float.IsFinite(z) || z <= 0.1f)
                return false;

            float centerX = camera.ViewportSize.X > 0f ? camera.ViewportSize.X : screenSize.X * 0.5f;
            float centerY = camera.ViewportSize.Y > 0f ? camera.ViewportSize.Y : screenSize.Y * 0.5f;
            float projectedX = centerX * (1f + (x / camera.ProjectionD1.X / z));
            float projectedY = centerY * (1f - (y / camera.ProjectionD2.Y / z));

            if (!float.IsFinite(projectedX) || !float.IsFinite(projectedY))
                return false;

            screenPosition = new Vector2(projectedX, projectedY);
            return true;
        }

        public sealed class Entity
        {
            public ulong Ptr;
            public ulong TypePtr;
            public ulong VisualStatePtr;
            public ulong ObjectNamePtr;
            public ulong CategoryNamePtr;
            public ulong CleanNamePtr;
            public uint NetworkId;
            public string SourceTable = "";
            public int SourceIndex;
            public string TypeName = "";
            public string ModelName = "";
            public string ConfigName = "";
            public string CleanName = "";
            public EntityType Category = EntityType.None;
            public Vector3 Position;
            public bool IsDead;
            public bool IsValid;
            public string PositionReadMode = "";
            public string Validation = "not parsed";

            public ulong NamePointer =>
                CleanNamePtr != 0 ? CleanNamePtr :
                ObjectNamePtr != 0 ? ObjectNamePtr :
                CategoryNamePtr;

            public string DisplayName =>
                !string.IsNullOrWhiteSpace(CleanName) ? CleanName :
                !string.IsNullOrWhiteSpace(TypeName) ? TypeName :
                ConfigName;

            public void Categorize()
            {
                string config = ConfigName.Trim().ToLowerInvariant();
                string model = ModelName.Trim().ToLowerInvariant();
                string name = $"{TypeName} {CleanName}".ToLowerInvariant();

                if (config == "dayzplayer" || name.Contains("survivor"))
                {
                    Category = EntityType.Player;
                    return;
                }

                if (config == "dayzinfected" || name.Contains("infected"))
                {
                    Category = EntityType.Zombie;
                    return;
                }

                if (config == "car") { Category = EntityType.Car; return; }
                if (config == "boat") { Category = EntityType.Boat; return; }
                if (config == "dayzanimal") { Category = EntityType.Animal; return; }
                if (model.Contains("backpacks")) { Category = EntityType.Backpack; return; }
                if (config == "clothing") { Category = EntityType.Clothing; return; }
                if (model.Contains("food") || name.Contains("food")) { Category = EntityType.Food; return; }
                if (model.Contains("ammunition") || name.Contains("ammo")) { Category = EntityType.Ammo; return; }
                if (model.Contains("firearms") || config == "weapon") { Category = EntityType.Weapon; return; }
                if (config == "itemoptics") { Category = EntityType.Optics; return; }
                if (model.Contains("camping")) { Category = EntityType.Base; return; }
                if (model.Contains("melee")) { Category = EntityType.Melee; return; }
                if (model.Contains("explosives")) { Category = EntityType.Explosives; return; }

                Category = EntityType.GroundItem;
            }

            internal static EntityGatherResult GatherEntities(
                string tableName,
                EntityTableMembership membership,
                ulong tablePointer,
                int count,
                bool logSamples)
            {
                var result = new EntityGatherResult(tableName);
                if (!IsPlausiblePointer(tablePointer) || count <= 0)
                    return result;

                ulong[]? pointers;
                try
                {
                    pointers = DmaMemory.ReadArray<ulong>(tablePointer, count);
                }
                catch (Exception ex)
                {
                    MaybeLogError($"{tableName} table", ex);
                    return result;
                }

                if (pointers is null || pointers.Length == 0)
                    return result;

                int sampleLimit = logSamples ? Math.Min(DiagnosticSampleCount, pointers.Length) : 0;
                for (int index = 0; index < pointers.Length; index++)
                {
                    ulong pointer = pointers[index];
                    bool isSample = index < sampleLimit;
                    if (!IsPlausiblePointer(pointer))
                    {
                        result.RejectedPointers++;
                        if (isSample)
                            result.Samples.Add(new EntityCandidateSample(tableName, index, pointer));
                        continue;
                    }

                    result.Candidates.Add(new EntityCandidateOccurrence(
                        pointer,
                        tableName,
                        index,
                        membership));
                    if (isSample)
                        result.Samples.Add(new EntityCandidateSample(tableName, index, pointer));
                }

                return result;
            }

            internal static EntityGatherResult GatherStructuredEntities(
                string tableName,
                EntityTableMembership membership,
                ulong tablePointer,
                int allocatedCount,
                int candidateValidCount,
                bool logSamples)
            {
                int expectedCount = candidateValidCount >= 0
                    ? candidateValidCount
                    : allocatedCount;
                var result = new EntityGatherResult(tableName)
                {
                    IsStructured = true,
                    TablePointer = tablePointer,
                    AllocatedCount = allocatedCount,
                    CandidateValidCount = candidateValidCount
                };
                result.Candidates.Capacity = Math.Min(Math.Max(expectedCount, 0), 1_024);

                if (!IsPlausiblePointer(tablePointer) || allocatedCount <= 0)
                    return result;

                int requestedBytes;
                try
                {
                    requestedBytes = checked(
                        allocatedCount * DayZOffsets.StructuredEntityTable.EntryStride);
                }
                catch (OverflowException)
                {
                    MaybeLogError($"{tableName} structured table size", new InvalidOperationException(
                        $"Allocated count {allocatedCount} overflows the table byte size."));
                    return result;
                }

                long readStart = logSamples ? Stopwatch.GetTimestamp() : 0;
                byte[]? tableBytes;
                try
                {
                    tableBytes = DmaMemory.ReadBytes(tablePointer, (uint)requestedBytes);
                }
                catch (Exception ex)
                {
                    MaybeLogError($"{tableName} structured table", ex);
                    return result;
                }

                if (tableBytes is null ||
                    tableBytes.Length < DayZOffsets.StructuredEntityTable.EntryStride)
                {
                    return result;
                }

                int availableEntries = Math.Min(
                    allocatedCount,
                    tableBytes.Length / DayZOffsets.StructuredEntityTable.EntryStride);
                int activeEntries = 0;
                int invalidPointers = 0;
                int duplicatePointers = 0;
                int loggedEntities = 0;
                var seenPointers = new HashSet<ulong>();

                // Scan the full allocated region until second-PC diagnostics confirm
                // that the candidate +0x10 count always matches the active flags.
                // Trusting an unverified count here could silently hide valid entries.
                for (int index = 0; index < availableEntries; index++)
                {
                    int entryOffset =
                        index * DayZOffsets.StructuredEntityTable.EntryStride;
                    ReadOnlySpan<byte> entry = tableBytes.AsSpan(
                        entryOffset,
                        DayZOffsets.StructuredEntityTable.EntryStride);

                    ushort flag = BinaryPrimitives.ReadUInt16LittleEndian(
                        entry.Slice(DayZOffsets.StructuredEntityTable.ValidFlag, sizeof(ushort)));
                    ulong entityPointer = BinaryPrimitives.ReadUInt64LittleEndian(
                        entry.Slice(DayZOffsets.StructuredEntityTable.EntityPointer, sizeof(ulong)));
                    ulong metadata = BinaryPrimitives.ReadUInt64LittleEndian(
                        entry.Slice(DayZOffsets.StructuredEntityTable.Metadata, sizeof(ulong)));

                    if (logSamples && index < DiagnosticSampleCount)
                    {
                        LogStructuredEntryDiagnostic(
                            tableName,
                            index,
                            flag,
                            entityPointer,
                            metadata);
                    }

                    if (flag != DayZOffsets.StructuredEntityTable.ActiveFlag)
                        continue;

                    activeEntries++;
                    if (!IsPlausiblePointer(entityPointer))
                    {
                        invalidPointers++;
                        result.RejectedPointers++;
                        continue;
                    }

                    if (!seenPointers.Add(entityPointer))
                    {
                        duplicatePointers++;
                        continue;
                    }

                    if (logSamples && loggedEntities++ < DiagnosticSampleCount)
                    {
                        result.Samples.Add(new EntityCandidateSample(
                            tableName,
                            index,
                            entityPointer));
                    }

                    result.Candidates.Add(new EntityCandidateOccurrence(
                        entityPointer,
                        tableName,
                        index,
                        membership));
                }

                result.ScannedEntries = availableEntries;
                result.ActiveEntries = activeEntries;
                result.InvalidPointers = invalidPointers;
                result.DuplicatePointers = duplicatePointers;
                result.TableBytes = tableBytes.Length;
                result.BulkReadMs = logSamples
                    ? Stopwatch.GetElapsedTime(readStart).TotalMilliseconds
                    : 0;
                return result;
            }
        }

        public enum EntityType
        {
            None,
            Player,
            Zombie,
            Car,
            Boat,
            Animal,
            Clothing,
            Weapon,
            Backpack,
            Food,
            Ammo,
            Rare,
            Optics,
            Base,
            Melee,
            Explosives,
            GroundItem
        }

        private static bool TryReadEntityPosition(
            ulong entityPointer,
            out Vector3 position,
            out ulong visualStatePointer,
            out string readMode)
        {
            position = default;
            visualStatePointer = 0;
            readMode = "";

            if (!TryReadPointer(
                    entityPointer + DayZOffsets.Entity.VisualState,
                    out visualStatePointer))
            {
                if (!TryReadPointer(
                        entityPointer + DayZOffsets.Entity.FutureVisualState,
                        out visualStatePointer))
                {
                    return false;
                }
            }

            // DayZ's transform begins at VisualState + 0x8, but its translation
            // lands at VisualState + 0x2C (0x24 bytes into that transform).
            if (TryReadValue(
                    visualStatePointer + DayZOffsets.VisualState.Position,
                    out position) &&
                IsPlausiblePosition(position))
            {
                readMode = "visual-state+0x2C";
                return true;
            }

            // Compatibility fallback if +0x8 is a Transform pointer on another build.
            if (TryReadPointer(
                    visualStatePointer + DayZOffsets.VisualState.Transform,
                    out ulong transformPointer) &&
                TryReadValue(
                    transformPointer + DayZOffsets.VisualState.PositionWithinTransform,
                    out position) &&
                IsPlausiblePosition(position))
            {
                readMode = "transform-pointer";
                return true;
            }

            // Diagnostic fallback for the previous interpretation. This should
            // normally be rejected once the correct +0x2C read succeeds.
            if (TryReadValue(
                    visualStatePointer +
                    DayZOffsets.VisualState.Transform +
                    DayZOffsets.VisualState.Position,
                    out position) &&
                IsPlausiblePosition(position))
            {
                readMode = "legacy+0x34";
                return true;
            }

            position = default;
            return false;
        }

        private static bool TryReadEntityPositionFallback(
            ulong visualStatePointer,
            out Vector3 position,
            out string readMode)
        {
            position = default;
            readMode = "";

            // Compatibility fallback if +0x8 is a Transform pointer on another build.
            if (TryReadPointer(
                    visualStatePointer + DayZOffsets.VisualState.Transform,
                    out ulong transformPointer) &&
                TryReadValue(
                    transformPointer + DayZOffsets.VisualState.PositionWithinTransform,
                    out position) &&
                IsPlausiblePosition(position))
            {
                readMode = "transform-pointer";
                return true;
            }

            // Retain the previous +0x34 interpretation as the final compatibility path.
            if (TryReadValue(
                    visualStatePointer +
                    DayZOffsets.VisualState.Transform +
                    DayZOffsets.VisualState.Position,
                    out position) &&
                IsPlausiblePosition(position))
            {
                readMode = "legacy+0x34";
                return true;
            }

            position = default;
            return false;
        }

        private static bool TryReadArmaStringField(
            ulong objectPointer,
            ulong fieldOffset,
            out ulong stringPointer,
            out string value)
        {
            stringPointer = 0;
            value = "";

            ulong fieldAddress = objectPointer + fieldOffset;
            if (TryReadPointer(fieldAddress, out stringPointer) &&
                TryReadArmaStringObject(stringPointer, out value))
            {
                return true;
            }

            // Compatibility fallback for an inline ArmaString field.
            stringPointer = fieldAddress;
            return TryReadArmaStringObject(stringPointer, out value);
        }

        private static bool TryReadArmaStringObject(ulong stringPointer, out string value)
        {
            value = "";
            if (!TryReadValue(
                    stringPointer + DayZOffsets.ArmaString.Length,
                    out int length) ||
                length <= 0 ||
                length > DayZOffsets.ArmaString.MaxLength)
            {
                return false;
            }

            byte[]? bytes;
            try
            {
                bytes = DmaMemory.ReadBytes(
                    stringPointer + DayZOffsets.ArmaString.Data,
                    (uint)length);
            }
            catch
            {
                return false;
            }

            if (bytes is null || bytes.Length < length)
                return false;

            string decoded = Encoding.UTF8.GetString(bytes, 0, length).TrimEnd('\0');
            if (!IsPlausibleText(decoded))
                return false;

            value = decoded;
            return true;
        }

        private static bool TryReadPointer(ulong address, out ulong pointer)
        {
            pointer = 0;
            return TryReadValue(address, out pointer) && IsPlausiblePointer(pointer);
        }

        private static bool TryReadValue<T>(ulong address, out T value) where T : unmanaged
        {
            value = default;
            if (!DmaMemory.IsAttached)
                return false;

            // Instrumented only here (not in TryReadPointer) since TryReadPointer delegates through this method.
            long start = Stopwatch.GetTimestamp();
            try
            {
                return DmaMemory.Read(address, out value);
            }
            catch
            {
                return false;
            }
            finally
            {
                long delta = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref _directReadTotalTicks, delta);
                Interlocked.Increment(ref _directReadCount);
            }
        }

        private static bool IsPlausiblePointer(ulong pointer)
            => pointer >= 0x10_000UL && pointer <= 0x0000_7FFF_FFFF_FFFFUL;

        private static bool IsPlausiblePosition(Vector3 position)
        {
            const float maximumCoordinate = 100_000f;
            return IsFinite(position) &&
                   position != Vector3.Zero &&
                   MathF.Abs(position.X) <= maximumCoordinate &&
                   MathF.Abs(position.Y) <= maximumCoordinate &&
                   MathF.Abs(position.Z) <= maximumCoordinate;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) &&
               float.IsFinite(value.Y) &&
               float.IsFinite(value.Z);

        private static bool IsPlausibleText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (char character in value)
            {
                if (char.IsControl(character) && !char.IsWhiteSpace(character))
                    return false;
            }

            return true;
        }

        private static void MaybeLogWorldSnapshot(DayZSnapshot snapshot)
        {
            if (!ShouldLog(ref _nextWorldDiagnostic, DiagnosticIntervalMs))
                return;

            Logger.Info(
                $"[DayZ/Diag] VMM={snapshot.VmmReady} DMA={snapshot.DmaAttached} " +
                $"WorldResolved={snapshot.Attached} " +
                $"PID={snapshot.ProcessId} ModuleBase=0x{snapshot.ModuleBase:X}");
            Logger.Info(
                $"[DayZ/Diag] WorldAddress=0x{snapshot.WorldAddress:X} " +
                $"World=0x{snapshot.World:X} Camera=0x{snapshot.Camera:X}");
            Logger.Info(
                $"[DayZ/Diag] LocalReference=0x{snapshot.LocalPlayerReference:X} " +
                $"LocalEntity=0x{snapshot.LocalPlayer:X} " +
                $"PlayerOnRaw=0x{snapshot.PlayerOn:X} " +
                $"Position={FormatVector(snapshot.LocalPlayerPosition)} " +
                $"Resolution=\"{snapshot.LocalPlayerResolution}\"");
            Logger.Info(
                $"[DayZ/Diag] Near table=0x{snapshot.NearTable:X} count={snapshot.NearCount} | " +
                $"Far table=0x{snapshot.FarTable:X} count={snapshot.FarCount}");
            Logger.Info(
                $"[DayZ/Diag] Slow table=0x{snapshot.SlowTable:X} " +
                $"allocated={snapshot.SlowAllocatedCount} valid={FormatCandidateCount(snapshot.SlowCount)} | " +
                $"Item table=0x{snapshot.ItemTable:X} " +
                $"allocated={snapshot.ItemAllocatedCount} valid={FormatCandidateCount(snapshot.ItemCount)}");
        }

        private static string FormatCandidateCount(int count)
            => count >= 0 ? count.ToString() : "unknown";

        private static void LogEntityDiagnostic(Entity entity)
        {
            Logger.Info(
                $"[DayZ/Entity] table={entity.SourceTable} index={entity.SourceIndex} " +
                $"entity=0x{entity.Ptr:X} type=0x{entity.TypePtr:X} " +
                $"object=\"{entity.TypeName}\" category=\"{entity.ConfigName}\" " +
                $"clean=\"{entity.CleanName}\" visual=0x{entity.VisualStatePtr:X} " +
                $"position={FormatVector(entity.Position)} positionMode={entity.PositionReadMode} " +
                $"networkId={entity.NetworkId} " +
                $"validation=\"{entity.Validation}\"");
        }

        private static void LogStructuredEntryDiagnostic(
            string tableName,
            int index,
            ushort flag,
            ulong entityPointer,
            ulong metadata)
        {
            Logger.Info(
                $"[DayZ/TableEntry] table={tableName} index={index} " +
                $"flag=0x{flag:X} entity=0x{entityPointer:X} metadata=0x{metadata:X}");
        }

        private static void LogGatherDiagnostics(
            IReadOnlyList<EntityGatherResult> gatherResults,
            IReadOnlyDictionary<ulong, Entity> parsedByPointer)
        {
            foreach (EntityGatherResult result in gatherResults)
            {
                if (!result.IsStructured || result.TableBytes == 0)
                    continue;

                int parsed = result.Candidates.Count(candidate =>
                    parsedByPointer.ContainsKey(candidate.Pointer));
                int invalidEntities = result.Candidates.Count - parsed;
                string candidateCount = result.CandidateValidCount >= 0
                    ? result.CandidateValidCount.ToString()
                    : "unknown";
                string countValidation = result.CandidateValidCount < 0
                    ? "unverified"
                    : result.CandidateValidCount == result.ActiveEntries
                        ? "match"
                        : "mismatch";

                Logger.Info(
                    $"[DayZ/Table] table={result.TableName} pointer=0x{result.TablePointer:X} " +
                    $"allocated={result.AllocatedCount} candidateValid={candidateCount} " +
                    $"scanned={result.ScannedEntries} activeFlags={result.ActiveEntries} " +
                    $"parsed={parsed} invalidPointers={result.InvalidPointers} " +
                    $"duplicates={result.DuplicatePointers} invalidEntities={invalidEntities} " +
                    $"countValidation={countValidation} bytes={result.TableBytes} " +
                    $"bulkRead={result.BulkReadMs:F2}ms");
            }
        }

        private static void LogRepresentativeEntities(
            IReadOnlyList<EntityGatherResult> gatherResults,
            IReadOnlyDictionary<ulong, Entity> parsedByPointer)
        {
            foreach (EntityCandidateSample sample in gatherResults.SelectMany(
                         result => result.Samples))
            {
                if (parsedByPointer.TryGetValue(sample.Pointer, out Entity? entity))
                {
                    LogEntityDiagnostic(entity);
                    continue;
                }

                LogEntityDiagnostic(new Entity
                {
                    Ptr = sample.Pointer,
                    SourceTable = sample.SourceTable,
                    SourceIndex = sample.SourceIndex,
                    Validation = IsPlausiblePointer(sample.Pointer)
                        ? "rejected during batched parse"
                        : "invalid entity pointer"
                });
            }
        }

        private static void TrackItems(
            IReadOnlyList<Entity> items,
            bool logSamples,
            long pass)
        {
            int logged = 0;

            foreach (var item in items)
            {
                var current = new ItemTrace(
                    item.NetworkId,
                    item.TypePtr,
                    item.NamePointer,
                    item.DisplayName,
                    item.Position,
                    item.SourceTable,
                    item.SourceIndex,
                    pass);

                if (ItemTraces.TryGetValue(item.Ptr, out var previous))
                {
                    if (previous.NetworkId != current.NetworkId ||
                        previous.TypePointer != current.TypePointer)
                    {
                        Logger.Warn(
                            $"[DayZ/ItemTrace] identity changed at entity=0x{item.Ptr:X}: " +
                            $"network {previous.NetworkId}->{current.NetworkId}, " +
                            $"type 0x{previous.TypePointer:X}->0x{current.TypePointer:X}");
                    }

                    if (!string.Equals(previous.Name, current.Name, StringComparison.Ordinal))
                    {
                        Logger.Warn(
                            $"[DayZ/ItemTrace] name changed at entity=0x{item.Ptr:X}: " +
                            $"\"{previous.Name}\" -> \"{current.Name}\" " +
                            $"namePtr 0x{previous.NamePointer:X}->0x{current.NamePointer:X}");
                    }
                }

                ItemTraces[item.Ptr] = current;

                if (logSamples && logged++ < DiagnosticSampleCount)
                {
                    Logger.Info(
                        $"[DayZ/ItemTrace] entity=0x{item.Ptr:X} networkId={item.NetworkId} " +
                        $"type=0x{item.TypePtr:X} namePtr=0x{item.NamePointer:X} " +
                        $"name=\"{item.DisplayName}\" position={FormatVector(item.Position)} " +
                        $"source={item.SourceTable}[{item.SourceIndex}]");
                }
            }

            if (pass % 50 != 0 || ItemTraces.Count == 0)
                return;

            long staleBefore = pass - 20;
            foreach (ulong pointer in ItemTraces
                         .Where(pair => pair.Value.LastSeenPass < staleBefore)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                ItemTraces.Remove(pointer);
            }
        }

        private static void MaybeLogError(string subsystem, Exception exception)
        {
            if (ShouldLog(ref _nextErrorDiagnostic, 1_000))
                Logger.Warn($"[DayZ/{subsystem}] {exception.GetType().Name}: {exception.Message}");
        }

        private static bool ShouldLog(ref long nextTimestamp, int intervalMilliseconds)
        {
            long now = Environment.TickCount64;
            long next = Volatile.Read(ref nextTimestamp);
            if (now < next)
                return false;

            return Interlocked.CompareExchange(
                ref nextTimestamp,
                now + intervalMilliseconds,
                next) == next;
        }

        private static string FormatVector(Vector3 vector)
            => $"({vector.X:F2},{vector.Y:F2},{vector.Z:F2})";

        private static string FormatSignedOffset(long offset)
            => offset < 0
                ? $"-0x{Math.Abs(offset):X}"
                : $"+0x{offset:X}";

        private enum LocalPlayerPathKind
        {
            WorldField,
            ReferenceChild,
            ReferenceGrandchild,
            PlayerFieldReference
        }

        private readonly record struct LocalPlayerPath(
            LocalPlayerPathKind Kind,
            ulong PrimaryOffset,
            ulong SecondaryOffset,
            bool ApplyLocalOffset)
        {
            private string StoredEntity =>
                ApplyLocalOffset
                    ? $"stored{FormatSignedOffset(DayZOffsets.World.LocalOffset)} -> entity"
                    : "entity";

            public string Description => Kind switch
            {
                LocalPlayerPathKind.WorldField =>
                    $"World+0x{PrimaryOffset:X} -> {StoredEntity}",
                LocalPlayerPathKind.ReferenceChild =>
                    $"World+0x{DayZOffsets.World.LocalPlayer:X} -> reference; " +
                    $"reference+0x{PrimaryOffset:X} -> {StoredEntity}",
                LocalPlayerPathKind.ReferenceGrandchild =>
                    $"World+0x{DayZOffsets.World.LocalPlayer:X} -> reference; " +
                    $"reference+0x{PrimaryOffset:X} -> child; " +
                    $"child+0x{SecondaryOffset:X} -> {StoredEntity}",
                LocalPlayerPathKind.PlayerFieldReference =>
                    $"World+0x{DayZOffsets.World.LocalPlayer:X} -> reference; " +
                    $"player+0x{PrimaryOffset:X} == reference",
                _ => "unresolved"
            };
        }

        private readonly record struct LocalPlayerResult(
            ulong EntityPointer,
            Vector3 Position,
            string Resolution);

        private readonly record struct TableState(
            ulong Pointer,
            int AllocatedCount,
            int ValidCount);

        [Flags]
        internal enum EntityTableMembership
        {
            None = 0,
            Near = 1 << 0,
            Far = 1 << 1,
            Slow = 1 << 2,
            Item = 1 << 3
        }

        internal readonly record struct EntityCandidateOccurrence(
            ulong Pointer,
            string SourceTable,
            int SourceIndex,
            EntityTableMembership Membership);

        internal readonly record struct EntityCandidateSample(
            string SourceTable,
            int SourceIndex,
            ulong Pointer);

        internal sealed class EntityGatherResult
        {
            public EntityGatherResult(string tableName)
            {
                TableName = tableName;
            }

            public string TableName { get; }
            public List<EntityCandidateOccurrence> Candidates { get; } = new();
            public List<EntityCandidateSample> Samples { get; } = new();
            public bool IsStructured { get; init; }
            public ulong TablePointer { get; init; }
            public int AllocatedCount { get; init; }
            public int CandidateValidCount { get; init; } = -1;
            public int RejectedPointers { get; set; }
            public int ScannedEntries { get; set; }
            public int ActiveEntries { get; set; }
            public int InvalidPointers { get; set; }
            public int DuplicatePointers { get; set; }
            public int TableBytes { get; set; }
            public double BulkReadMs { get; set; }
        }

        private sealed class EntityCandidate
        {
            public EntityCandidate(
                ulong pointer,
                string sourceTable,
                int sourceIndex,
                EntityTableMembership memberships)
            {
                Pointer = pointer;
                SourceTable = sourceTable;
                SourceIndex = sourceIndex;
                Memberships = memberships;
            }

            public ulong Pointer { get; }
            public string SourceTable { get; }
            public int SourceIndex { get; }
            public EntityTableMembership Memberships { get; set; }
            public bool IsItemMember =>
                (Memberships & EntityTableMembership.Item) != 0;
        }

        private sealed class EntityReadState
        {
            public EntityReadState(EntityCandidate candidate)
            {
                Candidate = candidate;
            }

            public EntityCandidate Candidate { get; }
            public bool TypePrepared { get; set; }
            public bool VisualStatePrepared { get; set; }
            public bool FutureVisualStatePrepared { get; set; }
            public bool NetworkIdPrepared { get; set; }
            public bool IsDeadPrepared { get; set; }
            public bool EntityDeadPrepared { get; set; }
            public ulong TypePointer { get; set; }
            public ulong VisualStatePointer { get; set; }
            public uint NetworkId { get; set; }
            public bool IsDead { get; set; }
            public bool UsedFutureVisualState { get; set; }
            public bool PositionPrepared { get; set; }
        }

        private sealed class EntityScatterMetrics
        {
            public long Pass { get; init; }
            public int Candidates { get; init; }
            public int Deduplicated { get; init; }
            public int Chunks { get; set; }
            public int MetadataPrepared { get; set; }
            public int MetadataOk { get; set; }
            public int VisualStateOk { get; set; }
            public int FutureVisualFallbacks { get; set; }
            public int PositionsPrepared { get; set; }
            public int PositionsOk { get; set; }
            public int PositionFallbacks { get; set; }
            public int Rejected { get; set; }
            public double PrepareMs { get; set; }
            public double MetadataExecuteMs { get; set; }
            public double PositionExecuteMs { get; set; }
            public double ParseMs { get; set; }
            public double TotalMs { get; set; }
            public Dictionary<ulong, Entity> ParsedByPointer { get; } = new();
        }

        private sealed record ItemTrace(
            uint NetworkId,
            ulong TypePointer,
            ulong NamePointer,
            string Name,
            Vector3 Position,
            string SourceTable,
            int SourceIndex,
            long LastSeenPass);
    }
}
