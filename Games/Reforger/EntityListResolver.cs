using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MamboDMA;

namespace ArmaReforgerFeeder
{
    /// <summary>
    /// Manages multiple entity lists from the game world.
    /// CRITICAL: Arma Reforger splits entities across MULTIPLE lists - we must combine them!
    /// </summary>
    internal static class EntityListManager
    {
        private struct ListCandidate
        {
            public uint ListOffset;
            public uint CountOffset;
            public string Name;
            public ListCandidate(uint list, uint count, string name) 
            { 
                ListOffset = list; 
                CountOffset = count; 
                Name = name;
            }
        }

        // All known entity list candidates
        private static readonly ListCandidate[] Candidates = new[]
        {
            new ListCandidate(0x128, 0x134, "Primary"),
            new ListCandidate(0x138, 0x144, "Secondary"),
            new ListCandidate(0x140, 0x14C, "Tertiary"),
            new ListCandidate(0x148, 0x154, "Quaternary"),
            new ListCandidate(0x120, 0x12C, "PrePrimary"),
            new ListCandidate(0x118, 0x124, "Legacy"),
            new ListCandidate(0x150, 0x15C, "Extended1"),
            new ListCandidate(0x158, 0x164, "Extended2"),
            new ListCandidate(0x160, 0x16C, "Extended3"),
            new ListCandidate(0x168, 0x174, "Extended4"),
            new ListCandidate(0x110, 0x11C, "Early1"),
            new ListCandidate(0x108, 0x114, "Early2"),
        };

        public class EntityListInfo
        {
            public ulong ListPtr;
            public int Count;
            public uint ListOffset;
            public uint CountOffset;
            public string Name;
            public int Score;
            public EntityType DominantType;
        }

        public enum EntityType
        {
            Unknown,
            Character,
            Vehicle,
            Item,
            Generic,
            Invalid
        }

        private static volatile EntityListInfo[] _validLists = Array.Empty<EntityListInfo>();
        private static volatile bool _cached;
        private static readonly object _lock = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidPointer(ulong ptr) => ptr > 0x10000UL && ptr < 0x0000800000000000UL;

        /// <summary>
        /// Get all valid entity lists from the world. Returns cached results if available.
        /// </summary>
        public static EntityListInfo[] GetAllLists(ulong worldPtr, bool forceRefresh = false)
        {
            if (!forceRefresh && _cached && _validLists.Length > 0)
                return _validLists;

            lock (_lock)
            {
                if (!forceRefresh && _cached && _validLists.Length > 0)
                    return _validLists;

                var discovered = DiscoverAllLists(worldPtr);
                _validLists = discovered;
                _cached = true;
                return discovered;
            }
        }

        /// <summary>
        /// CRITICAL: Get ALL entities from ALL valid lists for a specific type.
        /// Arma Reforger splits entities across multiple lists!
        /// </summary>
        public static ulong[] GetAllEntitiesForType(ulong worldPtr, EntityType targetType, int maxTotal = 32768)
        {
            var lists = GetAllLists(worldPtr);
            
            // Filter to lists that contain this type
            var relevantLists = lists.Where(l => 
                l.DominantType == targetType || 
                l.DominantType == EntityType.Unknown ||
                l.Score > 50  // High score means good mix
            ).ToArray();

            if (relevantLists.Length == 0)
            {
                // Fallback: use all lists
                relevantLists = lists;
            }

            //Console.WriteLine($"[EntityListManager] Combining {relevantLists.Length} lists for {targetType}");

            var allEntities = new HashSet<ulong>(); // Use HashSet to auto-dedupe
            
            foreach (var list in relevantLists)
            {
                //Console.WriteLine($"  -> Reading {list.Name}: {list.Count} entities");
                
                int toRead = Math.Min(list.Count, maxTotal - allEntities.Count);
                if (toRead <= 0) break;

                var entities = DmaMemory.ReadArray<ulong>(list.ListPtr, toRead);
                if (entities != null)
                {
                    foreach (var ent in entities)
                    {
                        if (IsValidPointer(ent))
                            allEntities.Add(ent);
                    }
                }
            }

            //Console.WriteLine($"[EntityListManager] Combined total: {allEntities.Count} unique entities");
            return allEntities.ToArray();
        }

        /// <summary>
        /// Get entities from the best single list (old behavior - less reliable)
        /// </summary>
        public static bool TryGetBestListForType(ulong worldPtr, EntityType targetType, out ulong listPtr, out int count)
        {
            var lists = GetAllLists(worldPtr);
            
            var match = lists.FirstOrDefault(l => l.DominantType == targetType);
            
            if (match == null)
                match = lists.OrderByDescending(l => l.Score).FirstOrDefault();

            if (match != null)
            {
                listPtr = match.ListPtr;
                count = match.Count;
                //Console.WriteLine($"[EntityListManager] Best single list for {targetType}: {match.Name} ({count} entities)");
                return true;
            }

            listPtr = 0;
            count = 0;
            return false;
        }

        /// <summary>
        /// Get the best general-purpose list (old behavior)
        /// </summary>
        public static bool TryGetBestList(ulong worldPtr, out ulong listPtr, out int count)
        {
            var lists = GetAllLists(worldPtr);
            
            var best = lists.OrderByDescending(l => l.DominantType == EntityType.Character ? 1 : 0)
                           .ThenByDescending(l => l.Score)
                           .FirstOrDefault();

            if (best != null)
            {
                listPtr = best.ListPtr;
                count = best.Count;
                return true;
            }

            listPtr = 0;
            count = 0;
            return false;
        }

        /// <summary>
        /// Reset cache, forcing re-discovery on next request.
        /// </summary>
        public static void ResetCache()
        {
            lock (_lock)
            {
                _cached = false;
                _validLists = Array.Empty<EntityListInfo>();
            }
        }

        private static EntityListInfo[] DiscoverAllLists(ulong worldPtr)
        {
            var validLists = new List<EntityListInfo>();

            foreach (var candidate in Candidates)
            {
                if (!DmaMemory.Read(worldPtr + candidate.ListOffset, out ulong listPtr)) continue;
                if (!IsValidPointer(listPtr)) continue;

                if (!DmaMemory.Read(worldPtr + candidate.CountOffset, out int count)) continue;
                if (count <= 0 || count > 200_000) continue;

                var (score, type) = AnalyzeEntityList(listPtr, count);
                
                if (score > int.MinValue)
                {
                    validLists.Add(new EntityListInfo
                    {
                        ListPtr = listPtr,
                        Count = count,
                        ListOffset = candidate.ListOffset,
                        CountOffset = candidate.CountOffset,
                        Name = candidate.Name,
                        Score = score,
                        DominantType = type
                    });
                }
            }

            return validLists.OrderByDescending(l => l.Score).ToArray();
        }

        private static (int score, EntityType dominantType) AnalyzeEntityList(ulong listPtr, int count)
        {
            int sampleSize = Math.Min(count, 256);
            var entities = DmaMemory.ReadArray<ulong>(listPtr, sampleSize) ?? Array.Empty<ulong>();
            
            if (entities.Length == 0) return (int.MinValue, EntityType.Invalid);

            int characterCount = 0;
            int vehicleCount = 0;
            int itemCount = 0;
            int genericCount = 0;
            int validCount = 0;
            int invalidCount = 0;
            var validIndices = new List<int>(entities.Length);

            using (var scatter = DmaMemory.Scatter(useCache: true))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!IsValidPointer(entities[i]))
                    {
                        invalidCount++;
                        continue;
                    }

                    validIndices.Add(i);
                    scatter.PrepareReadValue<ulong>(entities[i] + Off.PrefabMgr);
                }

                scatter.Execute();

                foreach (int i in validIndices)
                {
                    if (!scatter.ReadValue(entities[i] + Off.PrefabMgr, out ulong prefabMgr) || prefabMgr == 0)
                    {
                        invalidCount++;
                        continue;
                    }

                    string typeStr = TryReadPrefabType(prefabMgr);
                    if (string.IsNullOrEmpty(typeStr))
                    {
                        invalidCount++;
                        continue;
                    }

                    validCount++;
                    if (IsCharacterEntity(typeStr))
                        characterCount++;
                    else if (IsVehicleEntity(typeStr))
                        vehicleCount++;
                    else if (IsItemEntity(typeStr))
                        itemCount++;
                    else if (IsGenericEntity(typeStr))
                        genericCount++;
                }
            }

            int maxCount = Math.Max(Math.Max(characterCount, vehicleCount), Math.Max(itemCount, genericCount));
            EntityType dominant = EntityType.Unknown;
            
            if (maxCount == characterCount && characterCount > 0)
                dominant = EntityType.Character;
            else if (maxCount == vehicleCount && vehicleCount > 0)
                dominant = EntityType.Vehicle;
            else if (maxCount == itemCount && itemCount > 0)
                dominant = EntityType.Item;
            else if (maxCount == genericCount && genericCount > 0)
                dominant = EntityType.Generic;

            int score = (characterCount * 10) + (vehicleCount * 8) + (itemCount * 6) - (genericCount * 3) - (invalidCount * 5);
            
            if (validCount > 0)
            {
                float validRatio = (float)validCount / entities.Length;
                if (validRatio > 0.5f) score += 20;
                if (validRatio > 0.8f) score += 30;
            }

            return (score, dominant);
        }

        private static string TryReadPrefabType(ulong prefabMgr)
        {
            if (!DmaMemory.Read(prefabMgr + Off.PrefabDataClass, out ulong dataClass) || dataClass == 0)
                return string.Empty;

            if (!DmaMemory.Read(dataClass + Off.PrefabDataType, out ulong typePtr) || typePtr == 0)
                return string.Empty;

            var str = DmaMemory.ReadString(typePtr, 128, System.Text.Encoding.ASCII);
            if (string.IsNullOrWhiteSpace(str))
                str = DmaMemory.ReadString(typePtr, 128, System.Text.Encoding.UTF8);

            return str ?? string.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCharacterEntity(string type)
        {
            return type.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Chimera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Soldier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Human", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsVehicleEntity(string type)
        {
            return type.IndexOf("Vehicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Car", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Truck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Heli", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Helicopter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Plane", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Boat", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsItemEntity(string type)
        {
            return type.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Magazine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Equipment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Consumable", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsGenericEntity(string type)
        {
            return type.IndexOf("GenericEntity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("GameEntity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void DebugPrintAllLists(ulong worldPtr)
        {
            Console.WriteLine("[EntityListManager] Analyzing all candidates:");
            
            var lists = GetAllLists(worldPtr, forceRefresh: true);
            
            foreach (var list in lists)
            {
                Console.WriteLine($"  [{list.Name}] +0x{list.ListOffset:X}/+0x{list.CountOffset:X}: " +
                                $"count={list.Count}, score={list.Score}, type={list.DominantType}, ptr=0x{list.ListPtr:X}");
            }
            
            Console.WriteLine($"\nTotal valid lists found: {lists.Length}");
            
            // Show combined totals
            var allChars = GetAllEntitiesForType(worldPtr, EntityType.Character);
            var allVehs = GetAllEntitiesForType(worldPtr, EntityType.Vehicle);
            var allItems = GetAllEntitiesForType(worldPtr, EntityType.Item);
            
            Console.WriteLine($"\nCombined entity counts:");
            Console.WriteLine($"  Characters: {allChars.Length}");
            Console.WriteLine($"  Vehicles: {allVehs.Length}");
            Console.WriteLine($"  Items: {allItems.Length}");
        }
    }
}
