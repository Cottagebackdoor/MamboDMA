using System.Numerics;

namespace MamboDMA.Games.DayZ
{
    public static class DayZSnapshots
    {
        private static DayZSnapshot _current = DayZSnapshot.Empty;

        public static DayZSnapshot Current => Volatile.Read(ref _current);

        public static void Publish(DayZSnapshot s) => Volatile.Write(ref _current, s);

        public static void Mutate(Func<DayZSnapshot, DayZSnapshot> mutate)
            => Publish(mutate(Current));
    }    
    // Use named arguments when constructing this positional record. Issue #6 may
    // replace it with explicit properties during the unified snapshot overhaul.
    public record DayZSnapshot(
        bool VmmReady,
        bool DmaAttached,
        bool Attached,
        uint ProcessId,
        ulong ModuleBase,
        ulong WorldAddress,
        ulong World,
        ulong NetworkAddress,
        ulong Network,
        ulong NetworkManagerAddress,
        ulong NetworkManager,
        ulong Camera,
        ulong LocalPlayerReference,
        ulong LocalPlayer,
        string LocalPlayerResolution,
        ulong PlayerOn,
        Vector3 LocalPlayerPosition,
        ulong NearTable,
        int NearCount,
        ulong FarTable,
        int FarCount,
        ulong SlowTable,
        int SlowAllocatedCount,
        int SlowCount,
        ulong ItemTable,
        int ItemAllocatedCount,
        int ItemCount,
        int Players,
        int Zombies,
        int Cars
    )
    {
        public static readonly DayZSnapshot Empty = new(
            VmmReady: false,
            DmaAttached: false,
            Attached: false,
            ProcessId: 0,
            ModuleBase: 0,
            WorldAddress: 0,
            World: 0,
            NetworkAddress: 0,
            Network: 0,
            NetworkManagerAddress: 0,
            NetworkManager: 0,
            Camera: 0,
            LocalPlayerReference: 0,
            LocalPlayer: 0,
            LocalPlayerResolution: "unresolved",
            PlayerOn: 0,
            LocalPlayerPosition: default,
            NearTable: 0,
            NearCount: 0,
            FarTable: 0,
            FarCount: 0,
            SlowTable: 0,
            SlowAllocatedCount: 0,
            SlowCount: 0,
            ItemTable: 0,
            ItemAllocatedCount: 0,
            ItemCount: 0,
            Players: 0,
            Zombies: 0,
            Cars: 0
        );
    }
}
