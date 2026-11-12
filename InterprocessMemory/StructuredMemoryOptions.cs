namespace InterprocessMemory
{
    /// <summary>
    /// Options for creating <see cref="StructuredMemory{TSchema}"/>.
    /// </summary>
    public sealed class StructuredMemoryOptions
    {
        /// <summary>
        /// Stores the field list (name, type, size and offset of every field) in the region, behind the field
        /// data, so that a process that does not have the schema can find out what the region holds. The default
        /// is <c>false</c>.
        /// <para>
        /// Only the process that creates the region can store it: the size of an existing region cannot change, and
        /// the option has no effect when the region already exists (see
        /// <see cref="StructuredMemory{TSchema}.HasStoredDescriptor"/>).
        /// </para>
        /// <para>
        /// A region that stores its descriptor is larger than the schema needs. Version 4.0 and 4.1 of this
        /// library compare the size exactly and refuse to open such a region ("size mismatch" or "Capacity
        /// mismatch"); they never read it wrongly. Update every process that uses the region before turning this
        /// on, and remove regions that exist from before (<see cref="MemoryRegion.Remove"/>).
        /// </para>
        /// <para>
        /// The descriptor is not protected against a process that can write the region: it is a check and a
        /// diagnostic, not access control.
        /// </para>
        /// </summary>
        public bool StoreSchemaDescriptor { get; set; }
    }
}
