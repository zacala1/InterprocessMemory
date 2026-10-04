using System;
using System.Numerics;

namespace InterprocessMemory
{
    internal static class PowerOfTwo
    {
        /// <summary>The largest power of two that fits in a positive <see cref="int"/>.</summary>
        public const int MaxInt32 = 1 << 30;

        /// <summary>
        /// Rounds a requested item count up to the next power of two, which the ring buffers need so
        /// they can wrap with a mask instead of a modulo.
        /// </summary>
        public static int RoundUp(int value, string paramName)
        {
            if (value <= 0 || value > MaxInt32)
                throw new ArgumentOutOfRangeException(
                    paramName,
                    value,
                    $"The value must be between 1 and {MaxInt32} so it can be rounded up to a power of two.");

            return (int)BitOperations.RoundUpToPowerOf2((uint)value);
        }
    }
}
