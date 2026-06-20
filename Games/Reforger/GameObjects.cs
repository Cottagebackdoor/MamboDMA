using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using MamboDMA;

namespace ArmaReforgerFeeder
{
    public static class GameObjects
    {
        public readonly struct VehicleDto
        {
            public readonly ulong Ptr;
            public readonly string Type;
            public readonly string Name;
            public readonly string Faction;
            public readonly Vector3f Position;
            public readonly int Distance;
            
            public VehicleDto(ulong p, string type, string name, string fac, Vector3f pos, int dist)
            {
                Ptr = p;
                Type = type;
                Name = name;
                Faction = fac;
                Position = pos;
                Distance = dist;
            }
        }

        public readonly struct ItemDto
        {
            public readonly ulong Ptr;
            public readonly string Kind;
            public readonly string Type;
            public readonly string Name;
            public readonly Vector3f Position;
            public readonly int Distance;
            
            public ItemDto(ulong p, string kind, string type, string name, Vector3f pos, int dist)
            {
                Ptr = p;
                Kind = kind;
                Type = type;
                Name = name;
                Position = pos;
                Distance = dist;
            }
        }

        // Public accessors
        public static VehicleDto[] LatestVehicles { get; private set; } = Array.Empty<VehicleDto>();
        public static ItemDto[] LatestItems { get; private set; } = Array.Empty<ItemDto>();
        public static ItemDto[] LatestItemsAll { get; private set; } = Array.Empty<ItemDto>();

        // Configuration
        public static float MaxDrawDistance = 600f;
        public static int MaxScan = 32768;
        public static int VehiclesHz = 10;
        public static int ItemsHz = 5;
        public static float VehicleClusterRadiusM = 8.0f;
        public static bool VehicleClusterUseCentroid = true;

        private static Thread? _vehThread, _itemThread;
        private static volatile bool _run;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPtr(ulong v) => v > 0x10000 && v < 0x0000800000000000UL;

        public static void Start()
        {
            if (_run) return;
            _run = true;
            
            _vehThread = new Thread(VehicleLoop)
            {
                IsBackground = true,
                Name = "GameObjects.Vehicles",
                Priority = ThreadPriority.BelowNormal
            };
            
            _itemThread = new Thread(ItemLoop)
            {
                IsBackground = true,
                Name = "GameObjects.Items",
                Priority = ThreadPriority.BelowNormal
            };
            
            _vehThread.Start();
            _itemThread.Start();
        }

        public static void Stop()
        {
            _run = false;
            try
            {
                _vehThread?.Join(200);
                _itemThread?.Join(200);
            }
            catch { }
            _vehThread = _itemThread = null;
        }

        private static void VehicleLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            int sleepMs = Math.Max(10, 1000 / Math.Max(1, VehiclesHz));

            while (_run)
            {
                sw.Restart();
                try
                {
                    var vehicles = ScanVehicles();
                    LatestVehicles = vehicles;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameObjects.Vehicles] Error: {ex.Message}");
                }
                
                int remain = sleepMs - (int)sw.ElapsedMilliseconds;
                if (remain > 1) Thread.Sleep(remain);
            }
        }

        private static void ItemLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            int sleepMs = Math.Max(10, 1000 / Math.Max(1, ItemsHz));

            while (_run)
            {
                sw.Restart();
                try
                {
                    var items = ScanItems();
                    LatestItemsAll = items;
                    LatestItems = items.OrderBy(i => i.Distance).Take(1024).ToArray();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameObjects.Items] Error: {ex.Message}");
                }
                
                int remain = sleepMs - (int)sw.ElapsedMilliseconds;
                if (remain > 1) Thread.Sleep(remain);
            }
        }

        private static VehicleDto[] ScanVehicles()
        {
            if (!TryGetWorld(out ulong gw)) return Array.Empty<VehicleDto>();

            // Try to get vehicle-specific list first, fallback to best general list
            if (!EntityListManager.TryGetBestListForType(gw, EntityListManager.EntityType.Vehicle, out ulong list, out int count))
            {
                if (!EntityListManager.TryGetBestList(gw, out list, out count))
                    return Array.Empty<VehicleDto>();
            }

            if (list == 0 || count <= 0) return Array.Empty<VehicleDto>();

            int take = Math.Min(count, MaxScan);
            var ents = ReadEntityArray(list, count, take);
            if (ents.Length == 0) return Array.Empty<VehicleDto>();

            //Game.UpdateCamera();
            var cam = Game.Camera.Position;
            float maxSq = MaxDrawDistance * MaxDrawDistance;

            var posTmp = new Vector3f[ents.Length];
            var prefab = new ulong[ents.Length];
            var facComp = new ulong[ents.Length];

            // First scatter: positions and component pointers
            using (var scatter = DmaMemory.Scatter())
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    ulong e = ents[i];
                    if (e == 0) continue;

                    scatter.PrepareReadValue<Vector3f>(e + Off.EntityPosition);
                    scatter.PrepareReadValue<ulong>(e + Off.PrefabMgrVic);
                    scatter.PrepareReadValue<ulong>(e + Off.FactionComponent);
                }
                scatter.Execute();

                for (int i = 0; i < ents.Length; i++)
                {
                    ulong e = ents[i];
                    if (e == 0) continue;
                    scatter.ReadValue(e + Off.EntityPosition, out posTmp[i]);
                    scatter.ReadValue(e + Off.PrefabMgrVic, out prefab[i]);
                    scatter.ReadValue(e + Off.FactionComponent, out facComp[i]);
                }
            }

            var outVehicles = new List<VehicleDto>(256);

            for (int i = 0; i < ents.Length; i++)
            {
                ulong e = ents[i];
                if (e == 0) continue;

                float dx = posTmp[i].X - cam.X;
                float dy = posTmp[i].Y - cam.Y;
                float dz = posTmp[i].Z - cam.Z;
                float dsq = dx * dx + dy * dy + dz * dz;
                
                if (dsq > maxSq) continue;

                int dist = (int)MathF.Sqrt(dsq);

                string typePath = TryReadPrefabType(prefab[i]);
                string name = TryReadPrefabName(prefab[i]);
                string faction = TryReadFaction(facComp[i]);

                if (string.IsNullOrWhiteSpace(typePath)) continue;
                if (string.IsNullOrWhiteSpace(name)) 
                    name = NiceFromPath(typePath) ?? NiceFromType(typePath) ?? "Unknown Vehicle";

                // Filter out non-vehicles
                if (!LooksLikeVehicle(typePath, name)) continue;
                if (IsVehiclePart(typePath, name)) continue;

                outVehicles.Add(new VehicleDto(e, typePath, name, faction ?? "", posTmp[i], dist));
            }

            return outVehicles.OrderBy(v => v.Distance).Take(512).ToArray();
        }

        private static ItemDto[] ScanItems()
        {
            if (!TryGetWorld(out ulong gw)) return Array.Empty<ItemDto>();

            // Try item-specific list first
            if (!EntityListManager.TryGetBestListForType(gw, EntityListManager.EntityType.Item, out ulong list, out int count))
            {
                if (!EntityListManager.TryGetBestList(gw, out list, out count))
                    return Array.Empty<ItemDto>();
            }

            if (list == 0 || count <= 0) return Array.Empty<ItemDto>();

            int take = Math.Min(count, MaxScan);
            var ents = ReadEntityArray(list, count, take);
            if (ents.Length == 0) return Array.Empty<ItemDto>();

            //Game.UpdateCamera();
            var cam = Game.Camera.Position;
            float maxSq = MaxDrawDistance * MaxDrawDistance;

            var posTmp = new Vector3f[ents.Length];
            var prefab = new ulong[ents.Length];

            using (var scatter = DmaMemory.Scatter())
            {
                for (int i = 0; i < ents.Length; i++)
                {
                    ulong e = ents[i];
                    if (e == 0) continue;

                    scatter.PrepareReadValue<Vector3f>(e + Off.EntityPosition);
                    scatter.PrepareReadValue<ulong>(e + Off.PrefabMgrVic);
                }
                scatter.Execute();

                for (int i = 0; i < ents.Length; i++)
                {
                    ulong e = ents[i];
                    if (e == 0) continue;
                    scatter.ReadValue(e + Off.EntityPosition, out posTmp[i]);
                    scatter.ReadValue(e + Off.PrefabMgrVic, out prefab[i]);
                }
            }

            var outList = new List<ItemDto>(512);

            for (int i = 0; i < ents.Length; i++)
            {
                ulong e = ents[i];
                if (e == 0) continue;

                float dx = posTmp[i].X - cam.X;
                float dy = posTmp[i].Y - cam.Y;
                float dz = posTmp[i].Z - cam.Z;
                float dsq = dx * dx + dy * dy + dz * dz;
                
                if (dsq > maxSq) continue;

                int dist = (int)MathF.Sqrt(dsq);

                string typePath = TryReadPrefabType(prefab[i]);
                string name = TryReadPrefabName(prefab[i]);
                
                var kind = ClassifyItemKind(typePath, name);
                if (kind == null) continue;

                if (string.IsNullOrWhiteSpace(name))
                    name = NiceFromPath(typePath) ?? NiceFromType(typePath) ?? "Unknown Item";

                outList.Add(new ItemDto(e, kind, typePath ?? "", name, posTmp[i], dist));
            }

            return outList.OrderBy(i => i.Distance).Take(2048).ToArray();
        }

        private static bool TryGetWorld(out ulong gw)
        {
            gw = 0;
            if (!DmaMemory.Read(DmaMemory.Base + Off.Game, out ulong game) || game == 0) return false;
            if (!DmaMemory.Read(game + Off.GameWorld, out gw) || gw == 0) return false;
            return true;
        }

        private static ulong[] ReadEntityArray(ulong list, int count, int cap)
        {
            if (count <= cap)
                return DmaMemory.ReadArray<ulong>(list, count) ?? Array.Empty<ulong>();

            int take = cap;
            var ents = new ulong[take];
            double stride = (double)count / take;

            using var scatter = DmaMemory.Scatter();
            
            for (int i = 0; i < take; i++)
            {
                int srcIndex = (int)(i * stride);
                if (srcIndex >= count) srcIndex = count - 1;
                
                scatter.PrepareReadValue<ulong>(list + (ulong)(srcIndex * 8));
            }

            scatter.Execute();
            for (int i = 0; i < take; i++)
            {
                int srcIndex = (int)(i * stride);
                if (srcIndex >= count) srcIndex = count - 1;
                scatter.ReadValue(list + (ulong)(srcIndex * 8), out ents[i]);
            }
            return ents;
        }

        private static string TryReadPrefabType(ulong prefabMgr)
        {
            if (prefabMgr == 0) return null;
            if (!DmaMemory.Read(prefabMgr + Off.PrefabDataClassVic, out ulong cls) || cls == 0) return null;

            if (DmaMemory.Read(cls + Off.PrefabModelNamePtr, out ulong pathPtr) && pathPtr != 0)
            {
                var s = DmaMemory.ReadString(pathPtr, 256, Encoding.ASCII);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            
            if (DmaMemory.Read(cls + Off.PrefabDataType, out ulong typePtr) && typePtr != 0)
            {
                var s = DmaMemory.ReadString(typePtr, 128, Encoding.ASCII);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            
            return DmaMemory.TryGetPath(cls, out var path) ? path : null;
        }

        private static string TryReadPrefabName(ulong prefabMgr)
        {
            var path = TryReadPrefabType(prefabMgr);
            return NiceFromPath(path);
        }

        private static string TryReadFaction(ulong factionComp)
        {
            if (factionComp == 0) return null;
            if (!DmaMemory.Read(factionComp + Off.FactionComponentDataClass, out ulong fClass) || fClass == 0) return null;
            if (!DmaMemory.Read(fClass + Off.FactionComponentDataType, out ulong fType) || fType == 0) return null;
            return DmaMemory.ReadString(fType, 64, Encoding.ASCII);
        }

        private static string NiceFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string file = slash >= 0 ? path[(slash + 1)..] : path;
            
            int dot = file.LastIndexOf('.');
            if (dot > 0) file = file[..dot];
            
            file = file.Replace('_', ' ').Replace('-', ' ');
            
            if (file.Length == 0) return null;
            if (file.Length <= 1) return file.ToUpperInvariant();
            
            return char.ToUpperInvariant(file[0]) + file[1..];
        }

        private static string NiceFromType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            
            string s = type;
            s = s.Replace("Class", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("Component", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("Entity", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("SCR_", "", StringComparison.OrdinalIgnoreCase)
                 .Trim();
                 
            return s.Length == 0 ? type : s;
        }

        private static readonly string[] VehicleTokens = 
        {
            "Vehicle", "Car", "Truck", "Tank", "APC", "IFV", "MBT",
            "BTR", "BMP", "BRDM", "HMMWV", "Humvee", "UAZ", "Ural",
            "Heli", "Helicopter", "Mi8", "UH1H", "Plane", "Jet",
            "Aircraft", "Boat", "Ship", "Wheeled"
        };

        private static readonly string[] VehiclePartTokens =
        {
            "Light", "Indicator", "Reflector", "Brakelight", "Headlight",
            "Wheel", "Door", "Hatch", "Turret", "Seat", "Chassis",
            "Hull", "Track", "Mount", "Rotor", "Part", "Component"
        };

        private static bool LooksLikeVehicle(string type, string name)
        {
            string combined = $"{type} {name}".ToLowerInvariant();
            return ContainsAny(combined, VehicleTokens);
        }

        private static bool IsVehiclePart(string type, string name)
        {
            string combined = $"{type} {name}".ToLowerInvariant();
            return ContainsAny(combined, VehiclePartTokens);
        }

        private static string ClassifyItemKind(string type, string name)
        {
            string t = type ?? "";
            string n = name ?? "";
            
            if (ContainsAny(t, "Magazine", "Mag", "Clip") || ContainsAny(n, "Magazine", "Mag", "Clip"))
                return "Magazine";
                
            if (ContainsAny(t, "Weapon", "Rifle", "Pistol", "SMG", "MG", "Launcher", "Grenade", "Shotgun") ||
                ContainsAny(n, "Rifle", "Pistol", "SMG", "MG", "Launcher", "Grenade", "Shotgun"))
                return "Weapon";
                
            if (ContainsAny(t, "Item", "Ammo", "Equipment", "Consumable") ||
                ContainsAny(n, "Ammo", "Bandage", "Med", "Food", "Water"))
                return "Item";
                
            return null;
        }

        private static bool ContainsAny(string s, params string[] tokens)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var tok in tokens)
                if (s.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
