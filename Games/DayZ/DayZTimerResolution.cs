// MamboDMA/Games/DayZ/TimerResolution.cs
using System.Runtime.InteropServices;
using System.Threading;

namespace MamboDMA.Games.DayZ
{
    internal static class TimerResolution
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uMilliseconds);
        private static int _ref;
        public static void Enable1ms() { if (Interlocked.Increment(ref _ref) == 1) timeBeginPeriod(1); }
        public static void Disable1ms(){ if (Interlocked.Decrement(ref _ref) == 0) timeEndPeriod(1); }
    }
}
