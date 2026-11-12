# Changelog

## 0.1.0 (not released yet)

First version of the package. It is built from this repository and has not been published to NuGet.org.
The version numbers 2.x to 4.1.0 that were used before belong to builds that were not published either.
From here on the package is numbered from 0.1.0.

### Included

- `MemoryRegion`: raw bytes in named shared memory (a named `MemoryMappedFile` on Windows, a file in
  `/dev/shm` on Linux), with a process-wide reader/writer lock that recovers a lock left behind by a
  terminated process. Region names may start with `Global\` or `Local\` on Windows.
- `StructuredMemory<TSchema>`: named, typed fields with schema versions and a layout fingerprint for struct
  fields. `StructuredMemoryOptions.StoreSchemaDescriptor` stores the list of fields in a region that the call
  creates, `StructuredMemory.OpenStored` opens such a region without the schema, and a schema mismatch names the
  fields that differ.
- `SharedArray<T>`: a fixed-length array of `unmanaged` elements.
- `SingleProducerByteStream` and `SingleProducerQueue<T>`: one producer, one consumer.
- `ConcurrentQueue<T>` and `ConcurrentMessageQueue`: several producers and consumers.
- Builds for `net8.0` and `net10.0`, on Windows and Linux.

### Compatibility

- The region format is version 3. Regions written by the builds numbered 3.0.0 to 4.1.0 can be opened; regions
  of the older format (2.x) are refused without being changed and have to be created again.
- A region that stores its field list is larger than the schema needs, so the builds from before the stored list
  (numbered 4.0 and 4.1) refuse it. Update every process that uses the region before turning the option on.
- **Do not mix the build numbered 3.0.0 with any other build in processes that share a region on Linux.** That
  build compares `Process.StartTime`, which differs between observers, so it takes the write lock away from
  every live owner, including one that runs a later build. Update all processes that use a region together.
