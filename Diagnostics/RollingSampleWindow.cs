#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MamboDMA.Diagnostics
{
    /// <summary>
    /// Allocation-free rolling sample aggregator for double-valued measurements
    /// (typically milliseconds). <see cref="Add(double)"/> is lock-free and
    /// AggressiveInlining; <see cref="SnapshotAndReset"/> locks briefly and
    /// stack-allocates the sort buffer.
    /// </summary>
    public sealed class RollingSampleWindow
    {
        public const int DefaultCapacity = 256;

        private readonly object _sync = new();
        private readonly double[] _samples;
        private readonly int _capacity;
        private long _writeIndex;

        public RollingSampleWindow(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _samples = new double[capacity];
        }

        /// <summary>
        /// Hot-path call. Lock-free, allocation-free. Overflow silently overwrites the oldest slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(double sample)
        {
            long index = Interlocked.Increment(ref _writeIndex) - 1;
            int slot = (int)((ulong)index % (ulong)_capacity);
            _samples[slot] = sample;
        }

        /// <summary>
        /// Compute count / avg / p95 / max over samples observed since the previous call, then reset.
        /// </summary>
        public Stats SnapshotAndReset()
        {
            lock (_sync)
            {
                // Atomic claim closes the Add/reset race; snapshot may observe a torn-after-reset
                // write from a winning Add — acceptable for diagnostic accuracy.
                long writeIndex = Interlocked.Exchange(ref _writeIndex, 0);
                if (writeIndex <= 0)
                    return Stats.Empty;

                int count = writeIndex > _capacity ? _capacity : (int)writeIndex;
                Span<double> buffer = stackalloc double[DefaultCapacity];
                if (count > buffer.Length)
                    count = buffer.Length;

                if (writeIndex <= _capacity)
                {
                    for (int i = 0; i < count; i++)
                        buffer[i] = _samples[i];
                }
                else
                {
                    int start = (int)((ulong)writeIndex % (ulong)_capacity);
                    for (int i = 0; i < count; i++)
                    {
                        int slot = (start + i) % _capacity;
                        buffer[i] = _samples[slot];
                    }
                }

                Span<double> live = buffer.Slice(0, count);
                double sum = 0;
                double max = live[0];
                for (int i = 0; i < count; i++)
                {
                    double v = live[i];
                    sum += v;
                    if (v > max) max = v;
                }
                double avg = sum / count;

                live.Sort();
                int p95Index = (int)Math.Ceiling(0.95 * count) - 1;
                if (p95Index < 0) p95Index = 0;
                if (p95Index >= count) p95Index = count - 1;
                double p95 = live[p95Index];

                return new Stats(count, avg, p95, max);
            }
        }

        public readonly record struct Stats(int Count, double Avg, double P95, double Max)
        {
            public static readonly Stats Empty = new(0, 0d, 0d, 0d);

            public override string ToString() => Count == 0
                ? "n=0"
                : $"n={Count} avg={Avg:F2} p95={P95:F2} max={Max:F2}";
        }
    }
}
