namespace MamboDMA.Games.DayZ
{
    /// <summary>
    /// DayZ 1.29 offsets. Values marked "dump.log" come from the repository dump.
    /// Keep object-relative offsets grouped by their containing object.
    /// </summary>
    public static class DayZOffsets
    {
        public static class Module
        {
            public const ulong World = 0x4264028;
            public const ulong Network = 0x100FBD0;
            public const ulong NetworkManager = 0x100FC10;
            public const ulong Tick = 0xFF4998;
            public const ulong Landscape = 0x42672D0;
            public const ulong FovContext = 0x1008CE0;
            public const ulong ScopeFovContext = 0x4264920;
        }

        public static class World
        {
            public const ulong Camera = 0x1B8;
            public const ulong BulletList = 0xE00;
            public const ulong BulletListSize = 0xE08;
            public const ulong GrassOffline = 0xBF0;
            public const ulong GrassOnline = 0xC00;

            public const ulong NearEntityList = 0xF48;
            public const ulong NearTableSize = 0xF50;
            public const ulong FarEntityList = 0x1090;
            public const ulong FarTableSize = 0x1098;
            public const ulong SlowEntityList = 0x2010;
            public const ulong SlowTableSize = 0x2018;
            public const ulong ItemList = 0x2060;
            public const ulong ItemListSize = 0x2068;

            public const ulong LocalPlayer = 0x2960;
            public const ulong PlayerOn = 0x2968;
            public const ulong EyeAccommodation = 0x296C;
            public const ulong Hour = 0x2970;
            public const ulong Day = 0x2974;
            public const ulong DayTime = 0x2978;
            public const ulong WeatherController = 0x7460;

            // Runtime validation:
            // World + LocalPlayer -> reference
            // reference + LocalPlayerEntityReference -> entity subobject
            // entity subobject + LocalOffset -> entity base
            public const ulong LocalPlayerEntityReference = 0x8;

            // dump.log represents this signed displacement as 0xFFFFFFFFFFFFFF58.
            public const long LocalOffset = -0xA8;
        }

        public static class Entity
        {
            public const ulong Type = 0x180;
            public const ulong FutureVisualState = 0x120;
            public const ulong VisualState = 0x1C8;
            public const ulong IsDead = 0xE2;
            public const ulong EntityDead = 0x15D;
            public const ulong NetworkId = 0x6DC;
            public const ulong LodShape = 0x200;
        }

        public static class HumanType
        {
            public const ulong ObjectName = 0x70;
            public const ulong CleanNameInternal = 0x98;
            public const ulong CategoryName = 0xD0;
            public const ulong RealClassName = 0xD0;
            public const ulong CleanName = 0x518;

            // Retained from the old implementation for classification diagnostics.
            // This value is not present in dump.log and must be treated as unverified.
            public const ulong ModelNameUnverified = 0x88;
        }

        public static class VisualState
        {
            public const ulong Transform = 0x8;
            public const ulong InverseTransform = 0xA4;

            // World position/translation is stored at VisualState + 0x2C.
            // Transform starts at +0x8, so this is offset +0x24 within it.
            public const ulong Position = 0x2C;
            public const ulong PositionWithinTransform = Position - Transform;
        }

        public static class Camera
        {
            public const ulong ViewMatrix = 0x4;
            public const ulong InvertedViewRight = 0x8;
            public const ulong InvertedViewUp = 0x14;
            public const ulong InvertedViewForward = 0x20;
            public const ulong InvertedViewTranslation = 0x2C;
            public const ulong ViewportSize = 0x58;

            // ProjectionD1 is retained because the existing W2S calculation needs the
            // first projection vector. ProjectionD2 is confirmed by dump.log.
            public const ulong ProjectionD1Unverified = 0xD0;
            public const ulong ProjectionD2 = 0xDC;
        }

        public static class Player
        {
            public const ulong Inventory = 0x650;
            public const ulong Skeleton = 0x7E0;
            public const ulong InputController = 0x7E8;
            public const ulong StatsContainer = 0x6F0;
            public const ulong DamageManager = 0x700;
        }

        public static class Infected
        {
            public const ulong Skeleton = 0x670;
        }

        public static class Inventory
        {
            public const ulong ItemInventory = 0x650;
            public const ulong NestedInventory = 0x650;
            public const ulong NestedCargo = 0x148;
            public const ulong NestedCargoCount = 0x44;
            public const ulong Hands = 0x1B0;
            public const ulong ItemQuality = 0x194;
            public const ulong SlotCountAlt = 0x158;
        }

        public static class Network
        {
            public const ulong ManagerNetworkClient = 0x50;
            public const ulong Crosshair = 0xA0;
            public const ulong PlayerName = 0xF8;
            public const ulong ServerName = 0x308;
            public const ulong Ping = 0x33C;
            public const ulong GameVersion = 0x350;
            public const ulong ClientIdSize = 0x170;
        }

        public static class PlayerIdentity
        {
            public const ulong Name = 0xB0;
        }

        public static class Animation
        {
            public const ulong AnimClass2 = 0x90;
            public const ulong MatrixB = 0x54;
            public const ulong MatrixArray = 0xBE8;
            public const ulong AnimationComponent = 0x118;
        }

        public static class Weapon
        {
            public const ulong WeaponInfoTable = 0x6A8;
            public const ulong WeaponInfoSize = 0x6B4;
            public const ulong AmmoCapacityA = 0x6B0;
            public const ulong AmmoCapacityB = 0x6B4;
            public const ulong AmmoMagazineCount = 0x6AC;
            public const ulong AttachmentsArray = 0x150;
            public const ulong AttachmentsSize = 0x15C;
            public const ulong ChamberedPointer = 0x1B0;
            public const ulong ChamberArray = 0x6A8;
            public const ulong ChamberEntrySize = 0x100;
        }

        public static class Ammo
        {
            public const ulong Hit = 0x370;
            public const ulong InitSpeed = 0x38C;
            public const ulong FuseDistance = 0x3AC;
            public const ulong AirFriction = 0x3DC;
            public const ulong Caliber = 0x3C0;
            public const ulong Dispersion = 0x3CC;
        }

        public static class AmmoType
        {
            public const ulong Dispersion = 0x3A4;
            public const ulong AirFriction = 0x3B4;
            public const ulong InitSpeed = 0x38C;
        }

        public static class Magazine
        {
            public const ulong MaxAmmo = 0x3A4;
            public const ulong AmmoCount = 0x3B0;
        }

        public static class ArmaString
        {
            public const ulong Length = 0x8;
            public const ulong Data = 0x10;
            public const int MaxLength = 256;
        }
    }
}
