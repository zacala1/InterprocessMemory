using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace InterprocessMemory
{
    internal readonly record struct TypeLayoutFingerprint(ulong Low, ulong High)
    {
        public static TypeLayoutFingerprint Create<T>() where T : unmanaged
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

            // Declaration order (metadata token), explicit FieldOffset values, the total size and the
            // layout kind together pin down the memory layout without Marshal.OffsetOf.
            var fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(field => field.MetadataToken);

            foreach (var field in fields)
            {
                int? explicitOffset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
                target.Append(";f:").Append(field.Name).Append('@')
                    .Append(explicitOffset?.ToString() ?? "-").Append(':');
                AppendManagedType(target, field.FieldType, active);
            }

            active.Remove(type);
        }
    }
}
