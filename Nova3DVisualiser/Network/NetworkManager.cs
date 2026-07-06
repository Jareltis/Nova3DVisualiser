using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Nova3DVisualiser.Logging;

namespace Nova3DVisualiser.Network;

public class NetworkManager
{
    private TcpClient? _client;            // CLIENT mode: this peer's link to the server
    private TcpListener? _server;
    private NetworkStream? _stream;        // CLIENT mode: stream of _client

    // SERVER mode: every accepted connection, keyed by a stable connId (so the server keeps ALL
    // clients, not just the last one).
    private readonly ConcurrentDictionary<int, (TcpClient client, NetworkStream stream)> _conns = new();
    private int _nextConnId = 0;
    private readonly ConcurrentQueue<int> _disconnected = new();   // connIds to clean up on the main thread

    public int LastSenderConnId { get; private set; }              // the connId of the packet being handled
    public event Action<int>? OnClientDisconnected;                // raised on the main thread (ProcessEvents)

    private readonly ConcurrentQueue<(INetworkPacket packet, int senderId, int connId)> _packetQueue = new();

    // ---- UDP fast-path (plan E, stage E1) — ADDITIVE + DORMANT. Best-effort, sequence-filtered
    // (latest-wins), with automatic TCP fallback. Bound alongside TCP; no gameplay consumer rewires
    // onto it yet (transforms/physics-sync still flow over TCP). _udp == null means UDP couldn't stand
    // up and the transport degrades to TCP-only.
    private UdpClient? _udp;                                                    // both modes; null = TCP-only
    private IPEndPoint? _serverUdpEndpoint;                                     // CLIENT mode: where to send datagrams
    private readonly ConcurrentDictionary<int, IPEndPoint> _udpEndpoints = new();   // SERVER mode: senderId -> learned remote endpoint
    private int _udpSeq;                                                        // atomic per-sender send counter
    private readonly UdpSeqFilter _udpSeqFilter = new();                        // receive-side latest-wins filter

    // ---- Observability counters (S5). Incremented from background read/send tasks → Interlocked. Live
    // gauges (_conns.Count, _packetQueue.Count) are read directly in the summary, not counted.
    private long _packetsReceived, _bytesReceived, _packetsSent, _bytesSent, _framesRejected, _connectionsAccepted;
    private long _packetsDropped;   // S6: shed by the per-peer rate limit or the global queue bound
    private void CountRecv(int frameBytes) { Interlocked.Increment(ref _packetsReceived); Interlocked.Add(ref _bytesReceived, frameBytes); }
    private void CountSent(int frameBytes) { Interlocked.Increment(ref _packetsSent); Interlocked.Add(ref _bytesSent, frameBytes); }

    // ---- Per-peer rate limit (S6): token bucket keyed by TCP connId (server-assigned, trustworthy).
    private readonly PeerRateLimiter _peerLimiter = new(NetLimits.PeerRatePerSec, NetLimits.PeerRateBurst);

    // Periodic activity summary (emitted from ProcessEvents on the main thread; deltas vs the last snapshot).
    private const long SummaryIntervalMs = 30000;
    private long _lastSummaryTick;
    private long _prevRecvPkts, _prevRecvBytes, _prevSentPkts, _prevRejected, _prevDropped;

    public void StartServer(int port)
    {
        _server = new TcpListener(IPAddress.Any, port);
        _server.Start();
        Task.Run(AcceptClientsAsync);

        // Additive UDP fast-path bound on the SAME numeric port. If the bind throws, stay TCP-only.
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            Task.Run(UdpReadLoop);
            Logger.Info($"UDP bound on :{port}");
        }
        catch (Exception ex)
        {
            Logger.Error("UDP bind failed — transport is TCP-only", ex);
            _udp = null;
        }
    }

    public void Connect(string ip, int port)
    {
        _client = new TcpClient();
        _client.Connect(ip, port);
        _stream = _client.GetStream();
        Task.Run(() => ReadLoop(_client, _stream, connId: 0));   // client's single link to the server

        // Additive UDP fast-path. Derive the server address from the CONNECTED TCP socket so a hostname
        // in `ip` resolves to the SAME address the TCP link uses. If setup fails, stay TCP-only.
        try
        {
            var remote = (IPEndPoint)_client.Client.RemoteEndPoint!;
            _serverUdpEndpoint = new IPEndPoint(remote.Address, port);
            _udp = new UdpClient();
            Task.Run(UdpReadLoop);
            Logger.Info($"UDP ready -> server {remote.Address}:{port}");
        }
        catch (Exception ex)
        {
            Logger.Error("UDP setup failed — transport is TCP-only", ex);
            _udp = null;
            _serverUdpEndpoint = null;
        }
    }

    // Frames a packet exactly as the read side expects: TypeId(4) + SenderId(4) + Length(4) + payload.
    private static byte[] BuildFrame<T>(T packet, int senderId) where T : INetworkPacket
    {
        int packetTypeId = PacketManager.GetId(packet.GetType());

        using var packetMs = new MemoryStream();
        using var packetWriter = new BinaryWriter(packetMs);
        packet.Serialize(packetWriter);
        byte[] packetData = packetMs.ToArray();

        using var finalMs = new MemoryStream();
        using var writer = new BinaryWriter(finalMs);
        writer.Write(packetTypeId);
        writer.Write(senderId);
        writer.Write(packetData.Length);
        writer.Write(packetData);
        return finalMs.ToArray();
    }

    public void SendPacket<T>(T packet, int senderId) where T : INetworkPacket
    {
        byte[] finalBytes = BuildFrame(packet, senderId);

        // CLIENT: one stream to the server. SERVER: fan out to every connection.
        if (_stream != null && _conns.IsEmpty)
        {
            try { _stream.Write(finalBytes); CountSent(finalBytes.Length); } catch (Exception ex) { Logger.Error("SendPacket failed", ex); }
            return;
        }

        foreach (var kv in _conns.ToArray())   // snapshot — connections may drop mid-send
        {
            try { kv.Value.stream.Write(finalBytes); CountSent(finalBytes.Length); }
            catch (Exception ex) { Logger.Error($"SendPacket to conn {kv.Key} failed", ex); _disconnected.Enqueue(kv.Key); }
        }
    }

    // SERVER: write to ONE connection only (used to answer a world request without re-syncing others).
    public void SendPacketTo<T>(int connId, T packet, int senderId) where T : INetworkPacket
    {
        if (!_conns.TryGetValue(connId, out var conn)) return;   // no-op if the connId is gone
        byte[] finalBytes = BuildFrame(packet, senderId);
        try { conn.stream.Write(finalBytes); }
        catch (Exception ex) { Logger.Error($"SendPacketTo conn {connId} failed", ex); _disconnected.Enqueue(connId); }
    }

    // Best-effort UDP send with automatic TCP fallback (so the caller never loses the ability to send).
    // Later stages opt specific hot packets (transforms / physics-sync) onto this; nothing uses it yet.
    // A UDP-delivered packet arrives with LastSenderConnId == -1 (see UdpReadLoop) so game code can tell
    // which transport carried it.
    public void SendPacketUnreliable<T>(T packet, int senderId) where T : INetworkPacket
    {
        if (_udp == null) { SendPacket(packet, senderId); return; }   // UDP unavailable → TCP

        int seq = Interlocked.Increment(ref _udpSeq);
        byte[] frame = UdpFraming.BuildUdpFrame(packet, senderId, seq);

        if (_serverUdpEndpoint != null)   // CLIENT: one datagram to the server
        {
            try { _udp.Send(frame, frame.Length, _serverUdpEndpoint); CountSent(frame.Length); }
            catch (Exception ex) { Logger.Error("SendPacketUnreliable (client) failed — TCP fallback", ex); SendPacket(packet, senderId); }
            return;
        }

        // SERVER: fan out to every LEARNED endpoint. If none is learned yet (no client has spoken UDP),
        // there's nowhere to reach, so fall back to TCP for this call.
        if (_udpEndpoints.IsEmpty) { SendPacket(packet, senderId); return; }

        // A peer whose UDP endpoint isn't learned yet is briefly skipped and self-heals within ~1 frame
        // once its first UDP datagram arrives — acceptable for the continuous, loss-tolerant streams this
        // path carries. Each send is isolated so a bad endpoint can't stop the others.
        foreach (var ep in _udpEndpoints.Values)
        {
            try { _udp.Send(frame, frame.Length, ep); CountSent(frame.Length); }
            catch (Exception ex) { Logger.Error("SendPacketUnreliable (server) to an endpoint failed", ex); }
        }
    }

    // Group variant: send MANY packets that belong to ONE logical frame (a chunked physics-sync flush)
    // sharing ONE sequence number, so the receive-side seq filter (monotonic non-decreasing) accepts every
    // chunk regardless of intra-flush reorder. Same send-target + TCP-fallback logic as the single variant;
    // the ONLY difference is the shared seq — do NOT increment per packet inside the group.
    public void SendPacketUnreliableGroup<T>(IReadOnlyList<T> packets, int senderId) where T : INetworkPacket
    {
        if (packets == null || packets.Count == 0) return;

        if (_udp == null)   // UDP unavailable → send each reliably over TCP
        {
            foreach (var p in packets) SendPacket(p, senderId);
            return;
        }

        int seq = Interlocked.Increment(ref _udpSeq);   // ONE seq for the WHOLE group

        // SERVER with no learned endpoint yet: nowhere to reach, so TCP-fallback the whole group.
        if (_serverUdpEndpoint == null && _udpEndpoints.IsEmpty)
        {
            foreach (var p in packets) SendPacket(p, senderId);
            return;
        }

        foreach (var packet in packets)
        {
            byte[] frame = UdpFraming.BuildUdpFrame(packet, senderId, seq);   // SAME seq for every chunk

            if (_serverUdpEndpoint != null)   // CLIENT: one datagram to the server
            {
                try { _udp.Send(frame, frame.Length, _serverUdpEndpoint); CountSent(frame.Length); }
                catch (Exception ex) { Logger.Error("SendPacketUnreliableGroup (client) failed — TCP fallback", ex); SendPacket(packet, senderId); }
                continue;
            }

            // SERVER: fan each frame to every learned endpoint, isolated so a bad endpoint can't stop the others.
            foreach (var ep in _udpEndpoints.Values)
            {
                try { _udp.Send(frame, frame.Length, ep); CountSent(frame.Length); }
                catch (Exception ex) { Logger.Error("SendPacketUnreliableGroup (server) to an endpoint failed", ex); }
            }
        }
    }

    // Best-effort UDP receive loop (both modes). Mirrors ReadLoop's error posture: a transient
    // SocketException doesn't kill the loop; ObjectDisposedException (shutdown) exits it cleanly.
    private async Task UdpReadLoop()
    {
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync();
            }
            catch (ObjectDisposedException) { break; }                           // socket closed on shutdown
            catch (SocketException ex) { Logger.Error("UdpReadLoop socket error", ex); continue; }   // transient — keep going
            catch (Exception ex) { Logger.Error("UdpReadLoop error", ex); break; }

            if (!UdpFraming.TryParseUdpFrame(result.Buffer, result.Buffer.Length, out int typeId, out int senderId, out int seq, out var packet))
            {
                Interlocked.Increment(ref _framesRejected);
                Logger.Anomaly("udp-parse-fail", $"bad UDP datagram from {result.RemoteEndPoint}");
                continue;   // malformed / short / unknown typeId — drop (rate-limiter coalesces a flood)
            }
            CountRecv(result.Buffer.Length);   // a successfully parsed datagram

            // SERVER (client leaves _serverUdpEndpoint set): learn/refresh where this sender's datagrams
            // come from, so SendPacketUnreliable can fan back out to it.
            if (_serverUdpEndpoint == null)
                _udpEndpoints[senderId] = result.RemoteEndPoint;

            // Latest-wins: drop stale/duplicate BEFORE enqueue.
            if (!_udpSeqFilter.Accept(senderId, typeId, seq)) continue;

            // S6: global queue bound (memory). No per-peer limiter here — a UDP datagram's senderId is
            // attacker-controlled/spoofable, so keying a per-peer bucket by it is ineffective (and the
            // limiter is int/connId-keyed). The global queue bound + the seq-filter cover UDP flood memory.
            if (NetLimits.IsQueueFull(_packetQueue.Count))
            {
                Interlocked.Increment(ref _packetsDropped);
                Logger.Anomaly("queue-full", $"packet queue full ({NetLimits.MaxQueuedPackets}); dropping packet from {result.RemoteEndPoint}");
                continue;
            }

            // Same queue as the TCP path; connId = -1 marks "arrived via UDP" (handlers see it as
            // LastSenderConnId == -1). ProcessEvents dispatches it unchanged — handlers don't care which
            // transport delivered the packet.
            _packetQueue.Enqueue((packet!, senderId, -1));
        }
    }

    // Shared read loop for the client's link AND each server connection. Header: TypeId(4) +
    // SenderId(4) + Length(4) = 12 bytes, then the payload.
    private async Task ReadLoop(TcpClient client, NetworkStream stream, int connId)
    {
        byte[] header = new byte[12];
        EndPoint? peer = null;
        try { peer = client.Client.RemoteEndPoint; } catch { /* socket may already be down */ }

        while (client.Connected)
        {
            try
            {
                int read = await stream.ReadAsync(header, 0, 12);
                if (read == 0) break;

                using var ms = new MemoryStream(header);
                using var reader = new BinaryReader(ms);

                int typeId = reader.ReadInt32();
                int senderId = reader.ReadInt32();
                int length = reader.ReadInt32();

                // Reject an out-of-range (or negative) frame BEFORE allocating — a malformed header must
                // not force a multi-GB alloc / OOM. Dropping the connection matches the existing catch path.
                if (!NetLimits.IsFrameLengthValid(length))
                {
                    Interlocked.Increment(ref _framesRejected);
                    Logger.Anomaly("frame-oversize", $"oversized frame ({length} B) from {peer}; dropping connection {connId}");
                    break;
                }

                byte[] data = new byte[length];
                int totalRead = 0;
                while (totalRead < length)
                {
                    totalRead += await stream.ReadAsync(data, totalRead, length - totalRead);
                }

                INetworkPacket packet = PacketManager.CreateInstance(typeId);

                using var dataMs = new MemoryStream(data);
                using var dataReader = new BinaryReader(dataMs);
                packet.Deserialize(dataReader);

                // S6: per-peer rate limit (fairness) then the global queue bound (memory). A drop sheds the
                // packet only — a legit peer that briefly bursts is NOT disconnected (the caps are generous).
                if (!_peerLimiter.Allow(connId, Environment.TickCount64))
                {
                    Interlocked.Increment(ref _packetsDropped);
                    Logger.Anomaly("peer-flood", $"peer {peer} exceeded {NetLimits.PeerRatePerSec}/s; dropping packet");
                }
                else if (NetLimits.IsQueueFull(_packetQueue.Count))
                {
                    Interlocked.Increment(ref _packetsDropped);
                    Logger.Anomaly("queue-full", $"packet queue full ({NetLimits.MaxQueuedPackets}); dropping packet from {peer}");
                }
                else
                {
                    _packetQueue.Enqueue((packet, senderId, connId));
                    CountRecv(12 + length);
                }
            }
            catch (System.IO.InvalidDataException ex)
            {
                // A deserializer rejected an out-of-range count/len (S1) — a malformed packet, not a
                // transport failure. Coalesced anomaly, then drop this connection. (Other exceptions,
                // e.g. an unknown type id, fall through to the generic catch as a genuine error.)
                Interlocked.Increment(ref _framesRejected);
                Logger.Anomaly("malformed-packet", $"malformed packet from {peer}: {ex.Message}");
                break;
            }
            catch (Exception ex) { Logger.Error("ReadLoop error", ex); break; }
        }

        _conns.TryRemove(connId, out _);
        _peerLimiter.Forget(connId);   // S6: drop this peer's token bucket
        _disconnected.Enqueue(connId);
    }

    private async Task AcceptClientsAsync()
    {
        while (true)
        {
            var client = await _server!.AcceptTcpClientAsync();

            // S6: connection cap (accept-storm bound). Refuse the new client but keep the listener running.
            if (_conns.Count >= NetLimits.MaxConnections)
            {
                EndPoint? refused = null;
                try { refused = client.Client.RemoteEndPoint; } catch { }
                Logger.Anomaly("conn-cap", $"connection cap ({NetLimits.MaxConnections}) reached; refusing {refused}");
                try { client.Close(); } catch { }
                continue;
            }

            int id = Interlocked.Increment(ref _nextConnId);
            Interlocked.Increment(ref _connectionsAccepted);
            var stream = client.GetStream();
            _conns[id] = (client, stream);
            _ = Task.Run(() => ReadLoop(client, stream, id));
        }
    }

    public void ProcessEvents()
    {
        while (_packetQueue.TryDequeue(out var item))
        {
            LastSenderConnId = item.connId;   // expose the originating connection to handlers
            PacketManager.InvokeHandler(item.packet, item.senderId);
        }

        while (_disconnected.TryDequeue(out int cid)) OnClientDisconnected?.Invoke(cid);

        MaybeLogSummary();
    }

    // Every SummaryIntervalMs, emit ONE INFO line with live gauges + last-interval deltas — but only when
    // there was activity, so an idle server stays quiet. Runs on the main thread (ProcessEvents), so the
    // snapshot fields need no locking.
    private void MaybeLogSummary()
    {
        long now = Environment.TickCount64;

        long recvPkts  = Interlocked.Read(ref _packetsReceived);
        long recvBytes = Interlocked.Read(ref _bytesReceived);
        long sentPkts  = Interlocked.Read(ref _packetsSent);
        long rejected  = Interlocked.Read(ref _framesRejected);
        long dropped   = Interlocked.Read(ref _packetsDropped);

        if (_lastSummaryTick == 0)   // seed on first call — no summary yet
        {
            _lastSummaryTick = now;
            _prevRecvPkts = recvPkts; _prevRecvBytes = recvBytes; _prevSentPkts = sentPkts; _prevRejected = rejected; _prevDropped = dropped;
            return;
        }
        if (now - _lastSummaryTick < SummaryIntervalMs) return;

        long dRecvPkts = recvPkts - _prevRecvPkts;
        long dRecvBytes = recvBytes - _prevRecvBytes;
        long dSentPkts = sentPkts - _prevSentPkts;
        long dRejected = rejected - _prevRejected;
        long dDropped = dropped - _prevDropped;

        _lastSummaryTick = now;
        _prevRecvPkts = recvPkts; _prevRecvBytes = recvBytes; _prevSentPkts = sentPkts; _prevRejected = rejected; _prevDropped = dropped;

        if (dRecvPkts == 0 && dSentPkts == 0 && dRejected == 0 && dDropped == 0) return;   // idle interval → stay quiet

        double dRecvMB = dRecvBytes / (1024.0 * 1024.0);
        Logger.Info($"net: {_conns.Count} conns, {_packetQueue.Count} queued | last 30s: recv {dRecvPkts} pkts / {dRecvMB:F1} MB, sent {dSentPkts}, rejected {dRejected}, dropped {dDropped} | totals: recv {recvPkts}, rejected {rejected}, dropped {dropped}");
    }
}
