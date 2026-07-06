namespace Nova3DVisualiser.Network;

// Hard caps on attacker-controlled sizes read off the wire, shared by the transport (ReadLoop) and the
// packet deserializers so both bound the SAME way. Without these, one malformed header (a huge Length or
// a huge claimed collection count) forces a multi-GB allocation and OOM-crashes the whole process. These
// are additive input guards: legitimate traffic is far below the caps, so behaviour is unchanged.
public static class NetLimits
{
    // Hard cap on one framed packet's payload. Generous vs a full WorldSync (world config + chunked meshes
    // + small inline textures); still stops the multi-GB OOM. Tunable.
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    // Cap on any count/array length read from the wire. Legit physics batches are ≤ MaxSyncEntries (20);
    // world object/mesh/texture counts are far below this. Bounds allocate-by-count.
    public const int MaxCollectionEntries = 65536;

    // ---- Chunk-reassembly caps (S4): bound the client-side mesh/texture reassembly buffers against a
    // hostile peer streaming malicious chunks (a huge Total → new T[Total] OOM, unbounded concurrent
    // reassemblies, unbounded buffered bytes). Legit assets are far under these.
    public const int MaxChunkParts = 16384;                     // caps packet.Total → the parts array (a 16 KB-chunked mesh up to ~256 MB); kills `new T[2_000_000_000]`
    public const int MaxConcurrentReassemblies = 32;            // distinct in-flight mesh/texture names before LRU eviction
    public const long MaxReassemblyBytes = 256L * 1024 * 1024;  // total buffered chunk bytes across ALL reassemblies before LRU eviction

    // ---- Per-peer + global resource limits (S6): a flood / connection storm can't exhaust memory/CPU or
    // let one peer starve others. Legit rates are far below every cap.
    public const int MaxQueuedPackets = 8192;       // global inbound queue depth before newest packets are dropped
    public const int MaxConnections = 16;           // concurrent TCP connections before new accepts are refused
    public const double PeerRatePerSec = 500;       // per-peer sustained packet rate (legit ≈ 60 transforms/s + paced chunks)
    public const double PeerRateBurst = 1000;       // per-peer burst capacity (absorbs a legit spike)

    // Pure, unit-testable predicates (guarded by `securitytest`).
    public static bool IsFrameLengthValid(int length) => length >= 0 && length <= MaxFrameBytes;
    public static bool IsCollectionCountValid(int count) => count >= 0 && count <= MaxCollectionEntries;
    public static bool IsChunkTotalValid(int total) => total > 0 && total <= MaxChunkParts;
    public static bool IsQueueFull(int depth) => depth >= MaxQueuedPackets;
}
