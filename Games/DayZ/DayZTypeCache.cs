#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using MamboDMA.Services;

namespace MamboDMA.Games.DayZ
{
    internal static class DayZTypeCache
    {
        private const int OversizedThreshold = 1_000;

        private static readonly ConcurrentDictionary<ulong, TypeMetadata> _entries = new();
        private static long _hits;
        private static long _misses;
        private static long _inserts;
        private static long _warnedOversized;

        public readonly record struct TypeMetadata(
            string TypeName,
            string ModelName,
            string ConfigName,
            string CleanName,
            ulong ObjectNamePtr,
            ulong CategoryNamePtr,
            ulong CleanNamePtr,
            DayZUpdater.EntityType Category);

        public static int CurrentSize => _entries.Count;

        public static bool TryGet(ulong typePtr, out TypeMetadata metadata)
        {
            // Null Type pointers can't be keyed; treat as miss without counting.
            if (typePtr == 0)
            {
                metadata = default;
                return false;
            }
            if (_entries.TryGetValue(typePtr, out metadata))
            {
                Interlocked.Increment(ref _hits);
                return true;
            }
            Interlocked.Increment(ref _misses);
            return false;
        }

        public static void Insert(ulong typePtr, in TypeMetadata metadata)
        {
            if (typePtr == 0)
                return;
            if (_entries.TryAdd(typePtr, metadata))
            {
                Interlocked.Increment(ref _inserts);
                // No hard cap: surface pointer-reuse pathology once instead of silently dropping entries.
                if (_entries.Count > OversizedThreshold &&
                    Interlocked.CompareExchange(ref _warnedOversized, 1, 0) == 0)
                {
                    Logger.Warn(
                        $"[DayZ/TypeCache] size exceeded {OversizedThreshold} " +
                        $"(currentSize={_entries.Count}) — investigate pointer reuse or wrong cache key");
                }
            }
        }

        public static void Clear()
        {
            _entries.Clear();
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _misses, 0);
            Interlocked.Exchange(ref _inserts, 0);
            Interlocked.Exchange(ref _warnedOversized, 0);
        }

        public static (long hits, long misses, long inserts) SnapshotAndReset()
        {
            long hits = Interlocked.Exchange(ref _hits, 0);
            long misses = Interlocked.Exchange(ref _misses, 0);
            long inserts = Interlocked.Exchange(ref _inserts, 0);
            return (hits, misses, inserts);
        }
    }
}
