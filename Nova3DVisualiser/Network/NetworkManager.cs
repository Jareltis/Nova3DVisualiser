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

    public void StartServer(int port)
    {
        _server = new TcpListener(IPAddress.Any, port);
        _server.Start();
        Task.Run(AcceptClientsAsync);
    }

    public void Connect(string ip, int port)
    {
        _client = new TcpClient();
        _client.Connect(ip, port);
        _stream = _client.GetStream();
        Task.Run(() => ReadLoop(_client, _stream, connId: 0));   // client's single link to the server
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
            try { _stream.Write(finalBytes); } catch (Exception ex) { Logger.Error("SendPacket failed", ex); }
            return;
        }

        foreach (var kv in _conns.ToArray())   // snapshot — connections may drop mid-send
        {
            try { kv.Value.stream.Write(finalBytes); }
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

    // Shared read loop for the client's link AND each server connection. Header: TypeId(4) +
    // SenderId(4) + Length(4) = 12 bytes, then the payload.
    private async Task ReadLoop(TcpClient client, NetworkStream stream, int connId)
    {
        byte[] header = new byte[12];

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

                _packetQueue.Enqueue((packet, senderId, connId));
            }
            catch (Exception ex) { Logger.Error("ReadLoop error", ex); break; }
        }

        _conns.TryRemove(connId, out _);
        _disconnected.Enqueue(connId);
    }

    private async Task AcceptClientsAsync()
    {
        while (true)
        {
            var client = await _server!.AcceptTcpClientAsync();
            int id = Interlocked.Increment(ref _nextConnId);
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
    }
}
