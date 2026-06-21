#nullable enable

using System.Collections.Immutable;
using System.Numerics;

namespace MamboDMA.Games.DayZ
{
    // World-state snapshot. Replaces the previous positional record so that adding
    // fields no longer silently reorders constructor arguments at call sites.
    // Construct with object-initializer syntax; `with` expressions continue to work.
    public sealed record DayZSnapshot
    {
        public required bool VmmReady { get; init; }
        public required bool DmaAttached { get; init; }
        public required bool Attached { get; init; }
        public required uint ProcessId { get; init; }
        public required ulong ModuleBase { get; init; }
        public required ulong WorldAddress { get; init; }
        public required ulong World { get; init; }
        public required ulong NetworkAddress { get; init; }
        public required ulong Network { get; init; }
        public required ulong NetworkManagerAddress { get; init; }
        public required ulong NetworkManager { get; init; }
        public required ulong Camera { get; init; }
        public required ulong LocalPlayerReference { get; init; }
        public required ulong LocalPlayer { get; init; }
        public required string LocalPlayerResolution { get; init; }
        public required ulong PlayerOn { get; init; }
        public required Vector3 LocalPlayerPosition { get; init; }
        public required ulong NearTable { get; init; }
        public required int NearCount { get; init; }
        public required ulong FarTable { get; init; }
        public required int FarCount { get; init; }
        public required ulong SlowTable { get; init; }
        public required int SlowAllocatedCount { get; init; }
        public required int SlowCount { get; init; }
        public required ulong ItemTable { get; init; }
        public required int ItemAllocatedCount { get; init; }
        public required int ItemCount { get; init; }
        public required int Players { get; init; }
        public required int Zombies { get; init; }
        public required int Cars { get; init; }

        public static DayZSnapshot Empty { get; } = new()
        {
            VmmReady = false,
            DmaAttached = false,
            Attached = false,
            ProcessId = 0,
            ModuleBase = 0,
            WorldAddress = 0,
            World = 0,
            NetworkAddress = 0,
            Network = 0,
            NetworkManagerAddress = 0,
            NetworkManager = 0,
            Camera = 0,
            LocalPlayerReference = 0,
            LocalPlayer = 0,
            LocalPlayerResolution = "unresolved",
            PlayerOn = 0,
            LocalPlayerPosition = default,
            NearTable = 0,
            NearCount = 0,
            FarTable = 0,
            FarCount = 0,
            SlowTable = 0,
            SlowAllocatedCount = 0,
            SlowCount = 0,
            ItemTable = 0,
            ItemAllocatedCount = 0,
            ItemCount = 0,
            Players = 0,
            Zombies = 0,
            Cars = 0,
        };
    }

    // Unified frame snapshot. World, camera, and entities are published together
    // so consumers always see a triple from the same producer instant. This is the
    // sole consumer-facing snapshot; per-system holders are producer-private.
    public sealed record DayZFrameSnapshot
    {
        public required DayZSnapshot World { get; init; }
        public required DayZUpdater.DayZCamera? Camera { get; init; }
        public required ImmutableArray<DayZUpdater.Entity> Entities { get; init; }

        public static DayZFrameSnapshot Empty { get; } = new()
        {
            World = DayZSnapshot.Empty,
            Camera = null,
            Entities = ImmutableArray<DayZUpdater.Entity>.Empty,
        };
    }

    public static class DayZFrameSnapshots
    {
        private static DayZFrameSnapshot _current = DayZFrameSnapshot.Empty;

        public static DayZFrameSnapshot Current => Volatile.Read(ref _current);

        public static void Publish(DayZFrameSnapshot frame)
            => Volatile.Write(ref _current, frame);
    }
}
