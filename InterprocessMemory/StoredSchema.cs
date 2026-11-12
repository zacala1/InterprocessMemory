using System;
using System.Collections.Generic;
using System.IO;

namespace InterprocessMemory
{
    /// <summary>
    /// One field of the field list that a <see cref="StructuredMemory{TSchema}"/> region stores about itself
    /// (<see cref="StructuredMemoryOptions.StoreSchemaDescriptor"/>).
    /// </summary>
    /// <param name="Name">The name of the field.</param>
    /// <param name="TypeCode">The type of the field, or of its elements for an array.</param>
    /// <param name="ElementSize">The size of one element in bytes. For a blob or a UTF-8 string, 1.</param>
    /// <param name="ArrayLength">The number of elements; 1 for a scalar. For a fixed-length string, the number of
    /// characters; for a blob or a UTF-8 string, the size of the data plus its 4 byte length prefix.</param>
    /// <param name="Alignment">The alignment of the field within the field data.</param>
    /// <param name="Offset">The offset of the field from the start of the field data, which follows the 64 byte
    /// schema header of the region.</param>
    /// <param name="StructTypeName">The full name of the struct type of a <see cref="FieldTypeCode.Struct"/>
    /// field, when the creator of the region recorded it; otherwise <c>null</c>.</param>
    /// <param name="StructFingerprint">The layout fingerprint of that struct type as 32 hexadecimal digits, or
    /// <c>null</c>. A reader can compare it with the same value of a type it has; it is not part of the schema
    /// hash.</param>
    public sealed record StoredField(
        string Name,
        FieldTypeCode TypeCode,
        int ElementSize,
        int ArrayLength,
        int Alignment,
        long Offset,
        string? StructTypeName,
        string? StructFingerprint)
    {
        /// <summary>The size of the whole field in bytes.</summary>
        public long Size => (long)ElementSize * ArrayLength;

        internal TypeLayoutFingerprint? Fingerprint { get; init; }

        internal static StoredField From(DescriptorField field) => new(
            field.Name, field.TypeCode, field.ElementSize, field.ArrayLength, field.Alignment, field.Offset,
            field.StructTypeName,
            field.StructFingerprint is { } fingerprint ? $"{fingerprint.High:x16}{fingerprint.Low:x16}" : null)
        {
            Fingerprint = field.StructFingerprint
        };

        internal FieldDefinition ToDefinition() => new()
        {
            Name = Name,
            TypeCode = TypeCode,
            ElementSize = ElementSize,
            ArrayLength = ArrayLength,
            Alignment = Alignment,
            StructTypeName = StructTypeName,
            StructFingerprint = Fingerprint is { } fingerprint ? () => fingerprint : null
        };
    }

    /// <summary>
    /// The schema of a region as its creator stored it. <see cref="StructuredMemory.OpenStored"/> builds one
    /// from the region, so that a process without the schema in its code can use the region.
    /// </summary>
    public readonly struct StoredSchema : IVersionedSchema
    {
        private readonly IReadOnlyList<StoredField>? _fields;

        internal StoredSchema(int version, IReadOnlyList<StoredField> fields)
        {
            Version = version;
            _fields = fields;
        }

        /// <summary>The version of the schema that created the region.</summary>
        public int Version { get; }

        /// <summary>The fields in the order of their layout.</summary>
        public IReadOnlyList<StoredField> Fields => _fields ?? Array.Empty<StoredField>();

        /// <inheritdoc />
        public IEnumerable<FieldDefinition> GetFields()
        {
            foreach (StoredField field in Fields)
                yield return field.ToDefinition();
        }

        /// <summary>A stored schema is the schema of exactly one version.</summary>
        public bool IsCompatibleWith(int otherVersion) => otherVersion == Version;
    }

    /// <summary>
    /// Opens structured memory without the schema, from the field list its creator stored.
    /// </summary>
    public static class StructuredMemory
    {
        /// <summary>
        /// Opens an existing structured-memory region by the field list that its creator stored in it
        /// (<see cref="StructuredMemoryOptions.StoreSchemaDescriptor"/>), so the caller does not need the schema.
        /// The fields are in <see cref="StructuredMemory{TSchema}.Schema"/>; they are read and written with the
        /// ordinary accessors (<c>Read&lt;T&gt;</c>, <c>ReadString</c>, ...) by the name and with the type that the
        /// list gives.
        /// </summary>
        /// <param name="name">The name of the region.</param>
        /// <exception cref="InvalidOperationException">The region does not store its field list.</exception>
        /// <exception cref="InvalidDataException">The stored list is damaged, or does not describe the region.</exception>
        public static StructuredMemory<StoredSchema> OpenStored(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));

            // The same mapping serves for reading the list and for using the region: the region is opened once.
            var region = MemoryRegion.OpenExisting(
                name, new MemoryRegionOptions { EnableStatistics = false }, RegionKind.StructuredMemory);
            try
            {
                StoredSchema schema = StructuredMemory<StoredSchema>.ReadStoredSchema(region, name);
                StructuredMemory<StoredSchema> memory;
                try
                {
                    memory = new StructuredMemory<StoredSchema>(
                        name, schema, create: false, SchemaCompatibility.Strict, options: null, existing: region);
                }
                catch (ArgumentException ex)
                {
                    // The layout of a field that cannot be laid out (a type code and an element size that disagree,
                    // a blob without room for data): the descriptor says what the schema of a real region never would.
                    throw new InvalidDataException(
                        $"The stored schema descriptor of region '{name}' describes a field that cannot be used: {ex.Message}", ex);
                }

                try
                {
                    memory.VerifyStoredOffsets(schema.Fields);
                }
                catch
                {
                    memory.Dispose();
                    throw;
                }

                return memory;
            }
            catch
            {
                region.Dispose();
                throw;
            }
        }
    }
}
