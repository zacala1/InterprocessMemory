using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;

namespace InterprocessMemory
{
    /// <summary>
    /// Single-copy atomic access to 1, 2, 4 and 8 byte values in shared memory.
    ///
    /// <para><see cref="MemoryRegion.Write"/> and <see cref="MemoryRegion.Read"/> copy through
    /// <c>Span&lt;byte&gt;.CopyTo</c>, whose small-size path is not one store for every length: a two byte
    /// copy is a one byte store followed by a two byte store, so another process can observe a
    /// half-written value. A typed volatile access is always one load or store of exactly the
    /// element width (and, for 8 bytes on a 32-bit process, an interlocked one), which is what the
    /// lock-free element paths of <see cref="SharedArray{T}"/> and <see cref="StructuredMemory{TSchema}"/>
    /// need.</para>
    ///
    /// <para>The caller guarantees that the location is aligned to the value size.</para>
    /// </summary>
    internal static unsafe class AtomicAccess
    {
        /// <summary>True for the sizes that <see cref="Read{T}"/> and <see cref="Write{T}"/> support.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAtomicSize(int size) => size is 1 or 2 or 4 or 8;

        /// <summary>
        /// Returns the address of <paramref name="offset"/> in the data area of the region. The mapping
        /// does not move, so the pointer stays valid until the region is disposed; callers must not use
        /// it afterwards (the same contract as <see cref="MemoryRegion.GetMemory"/>).
        /// </summary>
        public static byte* GetPointer(IMemoryRegion region, long offset)
        {
            using MemoryHandle handle = region.GetMemory(offset, 1).Pin();
            return (byte*)handle.Pointer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(byte* location) where T : unmanaged
        {
            // sizeof(T) is a constant per instantiation, so the JIT keeps exactly one of these branches.
            if (sizeof(T) == 1)
            {
                byte value = Volatile.Read(ref *location);
                return Unsafe.As<byte, T>(ref value);
            }
            if (sizeof(T) == 2)
            {
                ushort value = Volatile.Read(ref *(ushort*)location);
                return Unsafe.As<ushort, T>(ref value);
            }
            if (sizeof(T) == 4)
            {
                uint value = Volatile.Read(ref *(uint*)location);
                return Unsafe.As<uint, T>(ref value);
            }
            if (sizeof(T) == 8)
            {
                ulong value = Volatile.Read(ref *(ulong*)location);
                return Unsafe.As<ulong, T>(ref value);
            }

            throw new NotSupportedException($"Atomic access needs a 1, 2, 4 or 8 byte type, not {sizeof(T)} bytes.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(byte* location, T value) where T : unmanaged
        {
            if (sizeof(T) == 1)
                Volatile.Write(ref *location, Unsafe.As<T, byte>(ref value));
            else if (sizeof(T) == 2)
                Volatile.Write(ref *(ushort*)location, Unsafe.As<T, ushort>(ref value));
            else if (sizeof(T) == 4)
                Volatile.Write(ref *(uint*)location, Unsafe.As<T, uint>(ref value));
            else if (sizeof(T) == 8)
                Volatile.Write(ref *(ulong*)location, Unsafe.As<T, ulong>(ref value));
            else
                throw new NotSupportedException($"Atomic access needs a 1, 2, 4 or 8 byte type, not {sizeof(T)} bytes.");
        }
    }
}
