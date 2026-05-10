using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// IVideoTransport implementation using raw UDP sockets.
/// Designed for same-LAN communication (Quest ↔ PC in research lab).
///
/// Frame protocol (supports frames up to ~3.8 MB):
///   Header: [frameId:2B][chunkIdx:1B][totalChunks:1B] = 4 bytes
///   Payload: up to CHUNK_PAYLOAD bytes
///
/// On the receiver side, chunks are reassembled per frameId.
/// Incomplete frames are discarded when a new frameId arrives.
///
/// Sender design: Send() enqueues chunks into a background thread that paces
/// them 2 ms apart.  Without pacing, a single 30 KB JPEG was sent as one large
/// UDP burst (~24 ms), monopolising the WiFi medium and starving the audio TCP
/// stream.  With 4 KB chunks and 2 ms spacing the medium is free for audio
/// packets between every chunk.
/// </summary>
public class UdpVideoTransport : IVideoTransport
{
    // 1400 B fits within the 1500-byte Ethernet MTU (1400 + 4 header + 8 UDP + 20 IP = 1432 B).
    // Larger chunks trigger IP fragmentation: a single lost fragment drops the whole UDP datagram,
    // multiplying effective loss rate.  1400 B is the industry-standard safe size.
    private const int CHUNK_PAYLOAD  = 1400;
    private const int HEADER_SIZE    = 4;
    private const int CHUNK_INTERVAL_MS = 2; // ms between consecutive chunk sends

    // ── Sender ────────────────────────────────────────────────────────────

    private UdpClient  senderClient;
    private IPEndPoint senderEndpoint;
    private ushort     frameCounter;

    private readonly ConcurrentQueue<byte[]> chunkQueue = new();
    private Thread       senderThread;
    private volatile bool senderRunning;

    public void StartSender(string remoteIp, int port)
    {
        StopSender();
        senderEndpoint = new IPEndPoint(IPAddress.Parse(remoteIp), port);
        senderClient   = new UdpClient();
        senderClient.Client.SendBufferSize = 512 * 1024;
        frameCounter  = 0;
        senderRunning = true;
        senderThread  = new Thread(SenderLoop) { IsBackground = true, Name = "UdpVideoSender" };
        senderThread.Start();
        Debug.Log($"[UdpVideoTransport] Sender started → {remoteIp}:{port}");
    }

    public void StopSender()
    {
        senderRunning = false;
        while (chunkQueue.TryDequeue(out _)) { }
        senderThread?.Join(500);
        senderThread = null;
        senderClient?.Close();
        senderClient = null;
    }

    public void Send(byte[] jpeg)
    {
        if (senderClient == null || jpeg == null || jpeg.Length == 0) return;

        int totalChunks = (jpeg.Length + CHUNK_PAYLOAD - 1) / CHUNK_PAYLOAD;
        if (totalChunks > 255) { Debug.LogWarning("[UdpVideoTransport] Frame too large, skipping."); return; }

        ushort frameId = frameCounter++;

        // Drop any not-yet-sent chunks from the previous frame so the receiver
        // always gets the freshest data rather than a backlog of stale frames.
        while (chunkQueue.TryDequeue(out _)) { }

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * CHUNK_PAYLOAD;
            int len    = Math.Min(CHUNK_PAYLOAD, jpeg.Length - offset);
            var packet = new byte[HEADER_SIZE + len];

            packet[0] = (byte)(frameId >> 8);
            packet[1] = (byte)(frameId & 0xFF);
            packet[2] = (byte)i;
            packet[3] = (byte)totalChunks;

            Buffer.BlockCopy(jpeg, offset, packet, HEADER_SIZE, len);
            chunkQueue.Enqueue(packet);
        }
    }

    private void SenderLoop()
    {
        while (senderRunning)
        {
            if (chunkQueue.TryDequeue(out byte[] packet))
            {
                try { senderClient?.Send(packet, packet.Length, senderEndpoint); }
                catch (SocketException ex) { Debug.LogWarning($"[UdpVideoTransport] Send error: {ex.Message}"); }
                Thread.Sleep(CHUNK_INTERVAL_MS);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    // ── Receiver ──────────────────────────────────────────────────────────

    private UdpClient    receiverClient;
    private Thread       receiverThread;
    private volatile bool receiverRunning;

    private ushort assemblyFrameId;
    private int    assemblyExpectedChunks;
    private int    assemblyReceivedCount;
    private byte[][] assemblyChunks;
    private int[]    assemblyChunkLengths;

    private readonly ConcurrentQueue<byte[]> frameQueue = new();

    public void StartReceiver(int port)
    {
        StopReceiver();
        receiverClient = new UdpClient(port);
        receiverClient.Client.ReceiveBufferSize = 1024 * 1024;
        receiverRunning = true;

        receiverThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "UdpVideoReceiver"
        };
        receiverThread.Start();
        Debug.Log($"[UdpVideoTransport] Receiver started on port {port}");
    }

    public void StopReceiver()
    {
        receiverRunning = false;
        receiverClient?.Close();
        receiverClient = null;
        receiverThread?.Join(500);
        receiverThread = null;
        while (frameQueue.TryDequeue(out _)) { }
        Debug.Log("[UdpVideoTransport] Receiver stopped.");
    }

    public bool TryDequeue(out byte[] jpeg)
    {
        jpeg = null;
        while (frameQueue.TryDequeue(out var frame))
            jpeg = frame;
        return jpeg != null;
    }

    private void ReceiveLoop()
    {
        var anyEp = new IPEndPoint(IPAddress.Any, 0);

        while (receiverRunning)
        {
            try
            {
                byte[] data = receiverClient.Receive(ref anyEp);
                if (data.Length < HEADER_SIZE) continue;

                ushort frameId     = (ushort)((data[0] << 8) | data[1]);
                int    chunkIdx    = data[2];
                int    totalChunks = data[3];
                int    payloadLen  = data.Length - HEADER_SIZE;

                if (frameId != assemblyFrameId || assemblyChunks == null || totalChunks != assemblyExpectedChunks)
                {
                    assemblyFrameId        = frameId;
                    assemblyExpectedChunks = totalChunks;
                    assemblyReceivedCount  = 0;
                    assemblyChunks         = new byte[totalChunks][];
                    assemblyChunkLengths   = new int[totalChunks];
                }

                if (chunkIdx >= totalChunks) continue;
                if (assemblyChunks[chunkIdx] != null) continue;

                assemblyChunks[chunkIdx]       = new byte[payloadLen];
                assemblyChunkLengths[chunkIdx] = payloadLen;
                Buffer.BlockCopy(data, HEADER_SIZE, assemblyChunks[chunkIdx], 0, payloadLen);
                assemblyReceivedCount++;

                if (assemblyReceivedCount == assemblyExpectedChunks)
                {
                    int totalLen = 0;
                    for (int i = 0; i < assemblyExpectedChunks; i++)
                        totalLen += assemblyChunkLengths[i];

                    var jpeg   = new byte[totalLen];
                    int offset = 0;
                    for (int i = 0; i < assemblyExpectedChunks; i++)
                    {
                        Buffer.BlockCopy(assemblyChunks[i], 0, jpeg, offset, assemblyChunkLengths[i]);
                        offset += assemblyChunkLengths[i];
                    }

                    frameQueue.Enqueue(jpeg);
                    assemblyChunks = null;
                }
            }
            catch (SocketException)         { if (!receiverRunning) break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────

    public static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
