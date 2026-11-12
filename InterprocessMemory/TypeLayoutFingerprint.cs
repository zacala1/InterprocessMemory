using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace InterprocessMemory
{
    internal readonly record struct TypeLayoutFingerprint(ulong Low, ulong High)
    {
        // A fingerprint depends on the type only, and the managed descriptor measures offsets with dynamic methods.
        private static readonly ConcurrentDictionary<Type, TypeLayoutFingerprint> s_cache = new();

        public static TypeLayoutFingerprint Create<T>() where T : unmanaged =>
            s_cache.GetOrAdd(typeof(T), static _ => Compute<T>());

        private static TypeLayoutFingerprint Compute<T>() where T : unmanaged
        {
            var descriptor = new StringBuilder(256);
            try
            {
                AppendType(descriptor, typeof(T), new HashSet<Type>());
            }
            catch (ArgumentException)
            {
                // Marshal.SizeOf/OffsetOf reject generic types (ValueTuple, KeyValuePair, ...), at any
                // nesting depth, although they satisfy the unmanaged constraint. Describe those with
                // the managed layout instead. Types the marshaller accepts keep the descriptor above,
                // so their fingerprint does not change and processes built from different releases
                // can still open the same region.
                descriptor.Clear();
                AppendManagedType(descriptor, typeof(T), new HashSet<Type>());
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString()));
            return new TypeLayoutFingerprint(
                BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8)));
        }

        private static void AppendType(StringBuilder target, Type type, HashSet<Type> active)
        {
            target.Append(type.Assembly.GetName().Name)
                .Append('|').Append(type.FullName)
                .Append('|').Append(Marshal.SizeOf(type));

            var layout = type.StructLayoutAttribute;
            target.Append('|').Append((int)(layout?.Value ?? LayoutKind.Auto))
                .Append('|').Append(layout?.Pack ?? 0)
                .Append('|').Append(layout?.Size ?? 0);

            if (type.IsPrimitive || type.IsEnum || !active.Add(type))
                return;

            var fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => new
                {
                    Field = field,
                    Offset = checked((int)Marshal.OffsetOf(type, field.Name))
                })
                .OrderBy(item => item.Offset)
                .ThenBy(item => item.Field.Name, StringComparer.Ordinal);

            foreach (var item in fields)
            {
                target.Append(";f:").Append(item.Offset)
                    .Append(':').Append(item.Field.Name)
                    .Append(':').Append(item.Field.FieldType.FullName);
                AppendType(target, item.Field.FieldType, active);
            }

            active.Remove(type);
        }

        // Unsafe.SizeOf<T>() is the size the containers actually copy (bool = 1 byte), and it works for
        // generic types. Unsafe.SizeOf has no generic constraint, so it can be bound to any Type.
        private static readonly MethodInfo s_sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;

        private static int ManagedSizeOf(Type type) =>
            (int)s_sizeOf.MakeGenericMethod(type).Invoke(null, null)!;

        // Version-independent type name. Type.FullName of a constructed generic type embeds the
        // assembly-qualified name (including Version=) of every type argument, which differs between
        // .NET releases and would make processes on different runtimes disagree.
        private static void AppendTypeName(StringBuilder target, Type type)
        {
            target.Append(type.Assembly.GetName().Name).Append(':');
            if (!type.IsConstructedGenericType)
            {
                target.Append(type.FullName);
                return;
            }

            target.Append(type.GetGenericTypeDefinition().FullName).Append('[');
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    target.Append(',');
                AppendTypeName(target, arguments[i]);
            }
            target.Append(']');
        }

        private static void AppendManagedType(StringBuilder target, Type type, HashSet<Type> active)
        {
            AppendTypeName(target, type);
            target.Append('|').Append(ManagedSizeOf(type));

            var layout = type.StructLayoutAttribute;
            target.Append('|').Append((int)(layout?.Value ?? LayoutKind.Auto))
                .Append('|').Append(layout?.Pack ?? 0)
                .Append('|').Append(layout?.Size ?? 0);

            if (type.IsPrimitive || type.IsEnum || !active.Add(type))
                return;

            // Declaration order (metadata token), the offset of every field as the runtime laid the type out,
            // the total size and the layout kind together pin down the memory layout without Marshal.OffsetOf.
            // The offsets matter for LayoutKind.Auto, where the runtime chooses them: two processes on
            // runtimes that choose differently would otherwise agree on a fingerprint and read each other's
            // fields from the wrong places.
            FieldInfo[] fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(field => field.MetadataToken)
                .ToArray();
            int[] offsets = ManagedFieldOffsets(type, fields);

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                int? explicitOffset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
                target.Append(";f:").Append(field.Name).Append('@')
                    .Append(explicitOffset?.ToString() ?? "-").Append('=').Append(offsets[i]).Append(':');
                AppendManagedType(target, field.FieldType, active);
            }

            active.Remove(type);
        }

        /// <summary>The offset of each field of <paramref name="type"/> inside the type, as the runtime laid it out.</summary>
        internal static int[] ManagedFieldOffsets(Type type, FieldInfo[] fields)
        {
            var offsets = new int[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                offsets[i] = MeasureFieldOffset(type, fields[i]);
            return offsets;
        }

        // There is no API for the offset of a field of an auto layout type (Marshal.OffsetOf rejects them), so
        // it is measured: a dynamic method takes the address of the field of a local and of the local itself
        // and returns the difference.
        private static int MeasureFieldOffset(Type type, FieldInfo field)
        {
            var method = new DynamicMethod(
                "FieldOffset", typeof(int), Type.EmptyTypes, typeof(TypeLayoutFingerprint).Module, skipVisibility: true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder local = il.DeclareLocal(type);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ret);
            return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
        }
    }
}
