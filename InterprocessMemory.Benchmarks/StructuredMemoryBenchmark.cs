using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using InterprocessMemory;

namespace InterprocessMemory.Benchmarks;

public struct BenchmarkSchema : IMemorySchema
{
    public const string IntField = "IntValue";
    public const string DoubleField = "DoubleValue";
    public const string FloatArray = "FloatArray";
    public const string StringField = "StringValue";

    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<int>(IntField);
        yield return FieldDefinition.Scalar<double>(DoubleField);
        yield return FieldDefinition.Array<float>(FloatArray, 100);
        yield return FieldDefinition.String(StringField, 256);
    }
}

/// <summary>
/// Schema with 16-byte fields, which StructuredMemory locks automatically (auto-lock), and an 8-byte field,
/// which it does not lock.
/// </summary>
public struct LargeTypeSchema : IMemorySchema
{
    public const string GuidField = "GuidValue";
    public const string DecimalField = "DecimalValue";
    public const string DateTimeOffsetField = "DateTimeOffsetValue";
    public const string LongField = "LongValue"; // 8 bytes: no auto-lock (for comparison)

    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<Guid>(GuidField);
        yield return FieldDefinition.Scalar<decimal>(DecimalField);
        yield return FieldDefinition.Scalar<DateTimeOffset>(DateTimeOffsetField);
        yield return FieldDefinition.Scalar<long>(LongField);
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class StructuredMemoryBenchmark
{
    private StructuredMemory<BenchmarkSchema> _memory = null!;
    private float[] _floatArray = null!;
    private float[] _readFloatArray = null!;

    [GlobalSetup]
    public void Setup()
    {
        var schema = new BenchmarkSchema();
        _memory = new StructuredMemory<BenchmarkSchema>("Benchmark_Strict", schema);
        _floatArray = new float[100];
        _readFloatArray = new float[100];

        for (int i = 0; i < 100; i++)
            _floatArray[i] = i * 0.5f;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _memory?.Dispose();
    }

    [Benchmark(Description = "Write Scalar<int>")]
    public void WriteInt()
    {
        _memory.Write(BenchmarkSchema.IntField, 12345);
    }

    [Benchmark(Description = "Read Scalar<int>")]
    public int ReadInt()
    {
        return _memory.Read<int>(BenchmarkSchema.IntField);
    }

    [Benchmark(Description = "Write Scalar<double>")]
    public void WriteDouble()
    {
        _memory.Write(BenchmarkSchema.DoubleField, 3.14159265359);
    }

    [Benchmark(Description = "Read Scalar<double>")]
    public double ReadDouble()
    {
        return _memory.Read<double>(BenchmarkSchema.DoubleField);
    }

    [Benchmark(Description = "WriteArray<float> (100 elements)")]
    public void WriteFloatArray()
    {
        _memory.WriteArray<float>(BenchmarkSchema.FloatArray, _floatArray);
    }

    [Benchmark(Description = "ReadArray<float> (100 elements)")]
    public void ReadFloatArray()
    {
        _memory.ReadArray<float>(BenchmarkSchema.FloatArray, _readFloatArray);
    }

    [Benchmark(Description = "WriteString (32 chars)")]
    public void WriteString()
    {
        _memory.WriteString(BenchmarkSchema.StringField, "This is a benchmark test string.");
    }

    [Benchmark(Description = "ReadString")]
    public string ReadString()
    {
        return _memory.ReadString(BenchmarkSchema.StringField);
    }

    [Benchmark(Description = "Write with WriteLock")]
    public void WriteWithLock()
    {
        using var writeLock = _memory.AcquireWriteLock();
        _memory.Write(BenchmarkSchema.IntField, 12345);
    }

    [Benchmark(Description = "Read with ReadLock")]
    public int ReadWithLock()
    {
        using var readLock = _memory.AcquireReadLock();
        return _memory.Read<int>(BenchmarkSchema.IntField);
    }
}

/// <summary>
/// Measures the overhead of auto-lock for 16-byte types.
/// A long (8 bytes) is not auto-locked; a Guid, a decimal and a DateTimeOffset (16 bytes) are.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class AutoLockOverheadBenchmark
{
    private StructuredMemory<LargeTypeSchema> _memory = null!;
    private Guid _testGuid;
    private decimal _testDecimal;
    private DateTimeOffset _testDateTimeOffset;

    [GlobalSetup]
    public void Setup()
    {
        var schema = new LargeTypeSchema();
        _memory = new StructuredMemory<LargeTypeSchema>("Benchmark_AutoLock", schema);
        _testGuid = Guid.NewGuid();
        _testDecimal = 123456.789012m;
        _testDateTimeOffset = DateTimeOffset.UtcNow;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _memory?.Dispose();
    }

    // === 8 bytes (no auto-lock) - Baseline ===

    [Benchmark(Description = "Write long (8B, no auto-lock)", Baseline = true)]
    public void WriteLong()
    {
        _memory.Write(LargeTypeSchema.LongField, 123456789L);
    }

    [Benchmark(Description = "Read long (8B, no auto-lock)")]
    public long ReadLong()
    {
        return _memory.Read<long>(LargeTypeSchema.LongField);
    }

    // === 16 bytes (auto-lock) ===

    [Benchmark(Description = "Write Guid (16B, auto-lock)")]
    public void WriteGuid()
    {
        _memory.Write(LargeTypeSchema.GuidField, _testGuid);
    }

    [Benchmark(Description = "Read Guid (16B, auto-lock)")]
    public Guid ReadGuid()
    {
        return _memory.Read<Guid>(LargeTypeSchema.GuidField);
    }

    [Benchmark(Description = "Write decimal (16B, auto-lock)")]
    public void WriteDecimal()
    {
        _memory.Write(LargeTypeSchema.DecimalField, _testDecimal);
    }

    [Benchmark(Description = "Read decimal (16B, auto-lock)")]
    public decimal ReadDecimal()
    {
        return _memory.Read<decimal>(LargeTypeSchema.DecimalField);
    }

    [Benchmark(Description = "Write DateTimeOffset (16B, auto-lock)")]
    public void WriteDateTimeOffset()
    {
        _memory.Write(LargeTypeSchema.DateTimeOffsetField, _testDateTimeOffset);
    }

    [Benchmark(Description = "Read DateTimeOffset (16B, auto-lock)")]
    public DateTimeOffset ReadDateTimeOffset()
    {
        return _memory.Read<DateTimeOffset>(LargeTypeSchema.DateTimeOffsetField);
    }

    // === 16 bytes + explicit lock (auto-lock skipped) ===

    [Benchmark(Description = "Write Guid (16B, explicit lock)")]
    public void WriteGuidWithLock()
    {
        using var _ = _memory.AcquireWriteLock();
        _memory.Write(LargeTypeSchema.GuidField, _testGuid);
    }

    [Benchmark(Description = "Read Guid (16B, explicit lock)")]
    public Guid ReadGuidWithLock()
    {
        using var _ = _memory.AcquireReadLock();
        return _memory.Read<Guid>(LargeTypeSchema.GuidField);
    }
}
