using Nova3DVisualiser;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using SampleGame.NetworkPackets;

namespace SampleGame;

partial class Program
{
    // Network transport self-test (plan E, stage E1): exercises ONLY the pure engine UDP pieces —
    // UdpFraming.BuildUdpFrame / TryParseUdpFrame and UdpSeqFilter. No real sockets, no threads,
    // fully deterministic. The live UDP socket standing up is a manual live-check, not part of this.
    static void UdpSelfTest()
    {
        Logger.Init(AppPaths.LogsFolder);
        Console.WriteLine("=== UDP SELF-TEST ===");

        // The pure framing needs the packet types registered (GetId / CreateInstance). RegisterPacket is
        // idempotent, so this is safe even if something registered them already.
        PacketManager.RegisterPacket<TransformPacket>();
        PacketManager.RegisterPacket<PhysicsSyncPacket>();

        bool ok = true;
        void Check(bool cond, string what)
        {
            if (!cond) { ok = false; Console.WriteLine($"  FAIL: {what}"); }
            else Console.WriteLine($"  ok: {what}");
        }

        // ---- 1) FRAME ROUND-TRIP: TransformPacket (fixed-size payload) ----
        {
            var pos = new Vector3(1.5f, -2.25f, 3.75f);
            var rot = new Vector3(0.1f, 0.2f, -0.3f);
            var sent = new TransformPacket(pos, rot);
            const int senderId = 7;
            const int seq = 42;

            byte[] frame = UdpFraming.BuildUdpFrame(sent, senderId, seq);
            bool parsed = UdpFraming.TryParseUdpFrame(frame, frame.Length, out int typeId, out int gotSender, out int gotSeq, out var packet);

            Check(parsed, "transform frame parses");
            Check(typeId == PacketManager.GetId<TransformPacket>(), "transform typeId matches GetId<TransformPacket>");
            Check(gotSender == senderId, "transform senderId round-trips");
            Check(gotSeq == seq, "transform seq round-trips");
            if (packet is TransformPacket tp)
            {
                Check(tp.Pos == pos, "transform Pos round-trips");
                Check(tp.Rot == rot, "transform Rot round-trips");
            }
            else Check(false, "parsed packet is a TransformPacket");
        }

        // ---- 2) FRAME ROUND-TRIP: PhysicsSyncPacket (variable-length payload) ----
        {
            var sent = new PhysicsSyncPacket
            {
                Ids = new[] { 3, 9 },
                Positions = new[] { new Vector3(10f, 11f, 12f), new Vector3(-1f, -2f, -3f) },
                LinVel = new[] { new Vector3(0.5f, 0f, -0.5f), new Vector3(1f, 2f, 3f) },
                Rotations = new[] { new Vector3(0.01f, 0.02f, 0.03f), new Vector3(0.3f, 0.2f, 0.1f) },
                AngVel = new[] { new Vector3(0.9f, 0.8f, 0.7f), new Vector3(-0.1f, -0.2f, -0.3f) },
            };
            const int senderId = 2;
            const int seq = 5;

            byte[] frame = UdpFraming.BuildUdpFrame(sent, senderId, seq);
            bool parsed = UdpFraming.TryParseUdpFrame(frame, frame.Length, out int typeId, out int gotSender, out int gotSeq, out var packet);

            Check(parsed, "physics frame parses");
            Check(typeId == PacketManager.GetId<PhysicsSyncPacket>(), "physics typeId matches GetId<PhysicsSyncPacket>");
            Check(gotSender == senderId && gotSeq == seq, "physics senderId+seq round-trip");
            if (packet is PhysicsSyncPacket pp)
            {
                bool same = pp.Ids.Length == 2 && pp.Ids[0] == 3 && pp.Ids[1] == 9
                    && pp.Positions[0] == sent.Positions[0] && pp.Positions[1] == sent.Positions[1]
                    && pp.LinVel[0] == sent.LinVel[0] && pp.LinVel[1] == sent.LinVel[1]
                    && pp.Rotations[0] == sent.Rotations[0] && pp.Rotations[1] == sent.Rotations[1]
                    && pp.AngVel[0] == sent.AngVel[0] && pp.AngVel[1] == sent.AngVel[1];
                Check(same, "physics ids/positions/vels survive the round-trip");
            }
            else Check(false, "parsed packet is a PhysicsSyncPacket");
        }

        // ---- 3) MALFORMED: a too-short buffer returns false and does not throw ----
        {
            bool threw = false, parsed = true;
            try
            {
                var shortBuf = new byte[5];   // < 12-byte header
                parsed = UdpFraming.TryParseUdpFrame(shortBuf, shortBuf.Length, out _, out _, out _, out var packet);
                Check(packet == null, "malformed → null packet");
            }
            catch { threw = true; }
            Check(!threw, "malformed buffer does not throw");
            Check(!parsed, "malformed buffer returns false");
        }

        // ---- 4) SEQ FILTER: latest-wins, monotonic NON-DECREASING, keyed per (sender, type) ----
        {
            var filter = new UdpSeqFilter();

            Check(filter.Accept(1, 100, 1), "(1,100) seq 1 accepted (first)");
            Check(filter.Accept(1, 100, 2), "(1,100) seq 2 accepted (newer)");
            Check(filter.Accept(1, 100, 2), "(1,100) seq 2 again accepted (same-seq, chunked-flush semantics)");
            Check(!filter.Accept(1, 100, 1), "(1,100) seq 1 rejected (strictly older)");
            Check(filter.Accept(1, 100, 3), "(1,100) seq 3 accepted (newer)");

            // Chunked flush: every datagram of one flush shares ONE seq, so all pass regardless of reorder;
            // a straggler from an OLDER flush is still dropped; the next flush advances.
            Check(filter.Accept(3, 300, 7), "(3,300) seq 7 accepted (first chunk of a flush)");
            Check(filter.Accept(3, 300, 7), "(3,300) seq 7 accepted again (another chunk, same seq)");
            Check(!filter.Accept(3, 300, 6), "(3,300) seq 6 rejected (straggler from an older flush)");
            Check(filter.Accept(3, 300, 8), "(3,300) seq 8 accepted (next flush advances)");

            // Independence across keys: a different sender / type starts fresh even though (1,100) is ahead.
            Check(filter.Accept(2, 100, 1), "(2,100) seq 1 accepted — per-sender keying");
            Check(filter.Accept(1, 200, 1), "(1,200) seq 1 accepted — per-type keying");
        }

        Console.WriteLine(ok ? "UDP TEST PASSED" : "UDP TEST FAILED");
    }
}
