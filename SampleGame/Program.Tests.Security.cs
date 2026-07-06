using System.IO;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using SampleGame.NetworkPackets;
using SampleGame.Textures;
using SampleGame.Worlds;

namespace SampleGame;

partial class Program
{
    // Security hardening self-test (stages S1 + S2): exercises ONLY the pure guards — no sockets, no real
    // filesystem. S1 = wire-driven allocation caps (NetLimits + the deserializers' reject-before-allocate).
    // S2 = received-file name sanitization (ReceivedFile.SafeName / SafeCombine).
    static void SecuritySelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== SECURITY SELF-TEST ===");

        bool ok = true;
        void Check(bool cond, string what)
        {
            if (!cond) { ok = false; Console.WriteLine($"  FAIL: {what}"); }
            else Console.WriteLine($"  ok: {what}");
        }

        // ---- S1a: NetLimits predicates ----
        Check(!NetLimits.IsFrameLengthValid(-1), "frame length -1 rejected");
        Check(!NetLimits.IsFrameLengthValid(NetLimits.MaxFrameBytes + 1), "frame length > max rejected");
        Check(NetLimits.IsFrameLengthValid(0), "frame length 0 accepted");
        Check(NetLimits.IsFrameLengthValid(1024), "frame length 1024 accepted");

        Check(!NetLimits.IsCollectionCountValid(-1), "collection count -1 rejected");
        Check(!NetLimits.IsCollectionCountValid(NetLimits.MaxCollectionEntries + 1), "collection count > max rejected");
        Check(NetLimits.IsCollectionCountValid(0), "collection count 0 accepted");
        Check(NetLimits.IsCollectionCountValid(16), "collection count 16 accepted");

        // ---- S1b: deserializers reject an absurd count/len WITHOUT allocating (throw InvalidDataException) ----
        // Helper: does deserializing `buffer` into a fresh packet throw InvalidDataException?
        static bool ThrowsInvalidData(byte[] buffer, INetworkPacket packet)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(buffer));
                packet.Deserialize(r);
                return false;   // no throw → the guard did NOT fire
            }
            catch (InvalidDataException) { return true; }
            catch { return false; }   // any OTHER exception is not the guard we asserted
        }

        // PhysicsSyncPacket: first int32 is a huge claimed entry count.
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) w.Write(100_000_000);
            Check(ThrowsInvalidData(ms.ToArray(), new PhysicsSyncPacket()), "PhysicsSync rejects absurd entry count (no alloc)");
        }

        // WorldSyncPacket: ConfigJson string, then a huge mesh `count`.
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write("{}");           // ConfigJson (length-prefixed)
                w.Write(100_000_000);    // mesh count
            }
            Check(ThrowsInvalidData(ms.ToArray(), new WorldSyncPacket()), "WorldSync rejects absurd mesh count (no alloc)");
        }

        // TextureChunkPacket: name, Index, Total, then a huge byte `len`.
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write("evil");   // TextureName
                w.Write(0);        // Index
                w.Write(1);        // Total
                w.Write(100_000_000);   // Data length
            }
            Check(ThrowsInvalidData(ms.ToArray(), new TextureChunkPacket()), "TextureChunk rejects absurd byte length (no alloc)");
        }

        // ---- S2a: ReceivedFile rejects hostile names (returns null) ----
        string[] hostile = { "../../evil.exe", "..\\..\\evil", "/etc/passwd", "C:\\Windows\\x.png", "a/b.png", "sub\\c.png", "", "   " };
        foreach (var h in hostile)
        {
            Check(ReceivedFile.SafeName(h, ".png") == null, $"SafeName rejects '{h}'");
            Check(ReceivedFile.SafeCombine(Path.Combine(Path.GetTempPath(), "received"), h, ".png") == null, $"SafeCombine rejects '{h}'");
        }

        // ---- S2b: ReceivedFile accepts + confines a legit name ----
        Check(ReceivedFile.SafeName("brick.png", ".png") == "brick.png", "SafeName keeps 'brick.png'");
        Check(ReceivedFile.SafeName("brick", ".png") == "brick.png", "SafeName appends '.png' to 'brick'");

        {
            string baseFolder = Path.Combine(Path.GetTempPath(), "received");
            string? combined = ReceivedFile.SafeCombine(baseFolder, "brick.png", ".png");
            string root = Path.GetFullPath(baseFolder);
            bool confined = combined != null
                && combined.StartsWith(root, System.StringComparison.Ordinal)
                && combined.EndsWith("brick.png", System.StringComparison.Ordinal);
            Check(confined, "SafeCombine confines 'brick.png' under the base folder");
        }

        // ---- S3a: PngDecoder.IsSizeValid — bomb / giant-allocation size guard ----
        Check(!PngDecoder.IsSizeValid(0, 10), "PNG size (0,10) rejected");
        Check(!PngDecoder.IsSizeValid(10, 0), "PNG size (10,0) rejected");
        Check(!PngDecoder.IsSizeValid(PngDecoder.MaxDimension + 1, 10), "PNG size (MaxDimension+1,10) rejected");
        Check(!PngDecoder.IsSizeValid(10, PngDecoder.MaxDimension + 1), "PNG size (10,MaxDimension+1) rejected");
        Check(!PngDecoder.IsSizeValid(8192, 8192), "PNG size (8192,8192)=67M > MaxPixels rejected");
        Check(PngDecoder.IsSizeValid(256, 256), "PNG size (256,256) accepted");
        Check(PngDecoder.IsSizeValid(PngDecoder.MaxDimension, 1), "PNG size (MaxDimension,1) accepted (per-side edge)");
        Check(PngDecoder.IsSizeValid(4000, 4000), "PNG size (4000,4000)=16M == MaxPixels accepted (pixel edge)");

        // ---- S3b: PngDecoder.Decode REJECTS an oversized IHDR before allocating (throws InvalidDataException) ----
        {
            var png = new System.Collections.Generic.List<byte>();
            void BE32(int v) { png.Add((byte)(v >> 24)); png.Add((byte)(v >> 16)); png.Add((byte)(v >> 8)); png.Add((byte)v); }
            void Chunk(string type, System.Action body) { png.AddRange(System.Text.Encoding.ASCII.GetBytes(type)); body(); BE32(0); }  // + 4-byte CRC placeholder (not verified)

            png.AddRange(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });   // signature
            BE32(13); Chunk("IHDR", () =>                                    // IHDR data length = 13
            {
                BE32(100000); BE32(100000);   // width, height — far over the caps
                png.Add(8); png.Add(6);       // bitDepth 8, colourType 6 (RGBA) — otherwise-valid
                png.Add(0); png.Add(0); png.Add(0);   // compression, filter, interlace
            });
            BE32(2); Chunk("IDAT", () => { png.Add(0); png.Add(0); });      // dummy IDAT (never reached — IHDR throws first)
            BE32(0); Chunk("IEND", () => { });

            bool threw;
            try { PngDecoder.Decode(png.ToArray()); threw = false; }
            catch (InvalidDataException) { threw = true; }
            catch { threw = false; }   // any OTHER exception is not the size guard we asserted
            Check(threw, "PNG Decode rejects an oversized IHDR (100000x100000) with InvalidDataException, no alloc");
        }

        // ---- S4a: NetLimits.IsChunkTotalValid — caps packet.Total (the reassembly parts array) ----
        Check(!NetLimits.IsChunkTotalValid(0), "chunk total 0 rejected");
        Check(!NetLimits.IsChunkTotalValid(-1), "chunk total -1 rejected");
        Check(!NetLimits.IsChunkTotalValid(NetLimits.MaxChunkParts + 1), "chunk total > max rejected");
        Check(NetLimits.IsChunkTotalValid(1), "chunk total 1 accepted");
        Check(NetLimits.IsChunkTotalValid(4), "chunk total 4 accepted");
        Check(NetLimits.IsChunkTotalValid(NetLimits.MaxChunkParts), "chunk total == max accepted");

        // ---- S4b: ChunkReassembler with TINY injected limits (maxParts 8, maxConcurrent 2, maxBufferedBytes 100) ----
        {
            static byte[] Part(int n) => new byte[n];

            // Completes in index order.
            {
                var ra = new ChunkReassembler<byte[]>(8, 2, 100);
                var o0 = ra.Accept("a", 0, 3, Part(10), 10, out _, out _);
                var o1 = ra.Accept("a", 1, 3, Part(11), 11, out _, out _);
                var o2 = ra.Accept("a", 2, 3, Part(12), 12, out var done, out _);
                bool ordered = done != null && done.Length == 3 && done[0].Length == 10 && done[1].Length == 11 && done[2].Length == 12;
                Check(o0 == ChunkOutcome.Pending && o1 == ChunkOutcome.Pending, "reassembler: first two parts Pending");
                Check(o2 == ChunkOutcome.Completed && ordered, "reassembler: third part Completes with 3 parts in index order");
            }

            // Oversized total is rejected (nothing stored); a later valid start for the same name still works.
            {
                var ra = new ChunkReassembler<byte[]>(8, 2, 100);
                var ob = ra.Accept("b", 0, 9, Part(10), 10, out _, out var rb);   // total 9 > maxParts 8
                Check(ob == ChunkOutcome.Rejected && rb != null && ra.ActiveCount == 0, "reassembler: oversized total rejected, nothing stored");
                var ob2 = ra.Accept("b", 0, 2, Part(10), 10, out _, out _);       // valid start still works
                Check(ob2 == ChunkOutcome.Pending && ra.ActiveCount == 1, "reassembler: valid start after reject works");
            }

            // Out-of-range index is rejected (nothing stored).
            {
                var ra = new ChunkReassembler<byte[]>(8, 2, 100);
                var oc = ra.Accept("c", 5, 3, Part(10), 10, out _, out var rc);   // index 5 >= total 3
                Check(oc == ChunkOutcome.Rejected && rc != null && ra.ActiveCount == 0, "reassembler: out-of-range index rejected, nothing stored");
            }

            // Concurrency LRU eviction: x, y (both partial), then z evicts the oldest (x).
            {
                var ra = new ChunkReassembler<byte[]>(8, 2, 100);
                ra.Accept("x", 0, 2, Part(5), 5, out _, out _);   // x partial
                ra.Accept("y", 0, 2, Part(5), 5, out _, out _);   // y partial  → {x,y}
                ra.Accept("z", 0, 2, Part(5), 5, out _, out _);   // new name over cap → evicts LRU x → {y,z}
                Check(ra.ActiveCount == 2, "reassembler: concurrency cap holds at 2");
                var oy = ra.Accept("y", 1, 2, Part(5), 5, out _, out _);
                var oz = ra.Accept("z", 1, 2, Part(5), 5, out _, out _);
                Check(oy == ChunkOutcome.Completed && oz == ChunkOutcome.Completed, "reassembler: y and z persisted (completed)");
                var ox = ra.Accept("x", 1, 2, Part(5), 5, out _, out _);   // x was evicted → restarts fresh
                Check(ox == ChunkOutcome.Pending, "reassembler: x was evicted (restarts fresh, not completed)");
            }

            // Bytes-cap LRU eviction: cap 100; a(60) then b(60) evicts a; b survives; total stays within cap.
            {
                var ra = new ChunkReassembler<byte[]>(8, 5, 100);
                ra.Accept("a", 0, 2, Part(60), 60, out _, out _);            // a buffers 60
                ra.Accept("b", 0, 2, Part(60), 60, out _, out _);            // 60+60>100 → evict a; b buffers 60
                Check(ra.TotalBufferedBytes <= 100, "reassembler: total buffered within byte cap after eviction");
                var ob = ra.Accept("b", 1, 2, Part(30), 30, out _, out _);   // b completes (60+30 ≤ 100)
                Check(ob == ChunkOutcome.Completed, "reassembler: newer reassembly (b) survived and completed");
                var oa = ra.Accept("a", 1, 2, Part(10), 10, out _, out _);   // a was evicted → restarts fresh
                Check(oa == ChunkOutcome.Pending, "reassembler: a was byte-cap evicted (restarts fresh)");
                var obig = ra.Accept("big", 0, 2, Part(200), 200, out _, out var rbig);   // single part > cap
                Check(obig == ChunkOutcome.Rejected && rbig != null, "reassembler: single part over byte cap rejected");
            }
        }

        // ---- S5a: Logger.ShouldRotate — size-based rotation threshold ----
        Check(!Logger.ShouldRotate(0), "ShouldRotate(0) false");
        Check(!Logger.ShouldRotate(Logger.MaxLogBytes - 1), "ShouldRotate(max-1) false");
        Check(Logger.ShouldRotate(Logger.MaxLogBytes), "ShouldRotate(max) true");
        Check(Logger.ShouldRotate(Logger.MaxLogBytes + 1), "ShouldRotate(max+1) true");

        // ---- S5b: LogRateLimiter — first allow, cooldown suppress, then emit with the suppressed count ----
        {
            var rl = new LogRateLimiter();   // default cooldown
            long cd = rl.CooldownMs;
            bool a0 = rl.ShouldLog("k", 0, out int s0);
            bool a1 = rl.ShouldLog("k", 1, out _);
            bool a2 = rl.ShouldLog("k", 2, out _);
            bool a3 = rl.ShouldLog("k", cd, out int s3);        // window elapsed → emit, report suppressed
            Check(a0 && s0 == 0, "ratelimiter: first occurrence allowed, suppressed 0");
            Check(!a1 && !a2, "ratelimiter: within-cooldown duplicates suppressed");
            Check(a3 && s3 == 2, "ratelimiter: emit after cooldown reports 2 suppressed");
            bool a4 = rl.ShouldLog("k", cd + 1, out _);         // immediately after emit → within cooldown, count reset
            Check(!a4, "ratelimiter: suppressed count reset after emit (next dup suppressed)");
            bool k2 = rl.ShouldLog("k2", 1, out int sk2);       // distinct key independent of k's cooldown
            Check(k2 && sk2 == 0, "ratelimiter: distinct key independent (allowed while k in cooldown)");
        }

        // ---- S6a: NetLimits.IsQueueFull — global inbound queue bound ----
        Check(!NetLimits.IsQueueFull(0), "IsQueueFull(0) false");
        Check(!NetLimits.IsQueueFull(NetLimits.MaxQueuedPackets - 1), "IsQueueFull(max-1) false");
        Check(NetLimits.IsQueueFull(NetLimits.MaxQueuedPackets), "IsQueueFull(max) true");
        Check(NetLimits.IsQueueFull(NetLimits.MaxQueuedPackets + 1), "IsQueueFull(max+1) true");

        // ---- S6b: PeerRateLimiter — token bucket (rate 10/s, burst 5), injected clock ----
        {
            var rl = new PeerRateLimiter(ratePerSec: 10, burst: 5);
            // Fresh key at t=0: the first `burst` (5) succeed, the 6th fails (bucket drained).
            bool all5 = true;
            for (int i = 0; i < 5; i++) if (!rl.Allow(1, 0)) all5 = false;
            bool sixth = rl.Allow(1, 0);
            Check(all5 && !sixth, "ratelimiter(peer): first burst allowed, next over-rate dropped");

            // Advance 100 ms (1000/rate = one token) → one more allowed.
            Check(rl.Allow(1, 100), "ratelimiter(peer): refills after elapsed time");

            // Distinct key independent: key 2 is a fresh full bucket.
            Check(rl.Allow(2, 0), "ratelimiter(peer): distinct key independent (fresh bucket)");

            // Forget resets a peer's bucket.
            var rl3 = new PeerRateLimiter(ratePerSec: 10, burst: 2);
            rl3.Allow(3, 0); rl3.Allow(3, 0);            // drain the 2 tokens
            bool drained = !rl3.Allow(3, 0);             // 3rd fails
            rl3.Forget(3);
            bool afterForget = rl3.Allow(3, 0);          // fresh full bucket → succeeds
            Check(drained && afterForget, "ratelimiter(peer): Forget resets a peer's bucket");
        }

        Console.WriteLine(ok ? "SECURITY TEST PASSED" : "SECURITY TEST FAILED");
    }
}
