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
/// </summary>
public class UdpVideoTransport : IVideoTransport
{
    private const int CHUNK_PAYLOAD = 60000; // fits well within 64KB UDP limit on LAN
    private const int HEADER_SIZE   = 4;

    // ── Sender ────────────────────────────────────────────────────────────

    private UdpClient  senderClient;
    private IPEndPoint senderEndpoint;
    private ushort     frameCounter;

    public void StartSender(string remoteIp, int port)
    {
        StopSender();
        senderEndpoint = new IPEndPoint(IPAddress.Parse(remoteIp), port);
        senderClient   = new UdpClient();
        // Allow large send buffer for burst frames
        senderClient.Client.SendBufferSize = 512 * 1024;
        frameCounter = 0;
        Debug.Log($"[UdpVideoTransport] Sender started → {remoteIp}:{port}");
    }

    public void StopSender()
    {
        if (senderClient != null)
        {
            senderClient.Close();
            senderClient = null;
            Debug.Log("[UdpVideoTransport] Sender stopped.");
        }
    }

    public void Send(byte[] jpeg)
    {
        if (senderClient == null || jpeg == null || jpeg.Length == 0) return;

        ushort frameId     = frameCounter++;
        int    totalChunks = (jpeg.Length + CHUNK_PAYLOAD - 1) / CHUNK_PAYLOAD;
        if (totalChunks > 255) { Debug.LogWarning("[UdpVideoTransport] Frame too large, skipping."); return; }

        for (int i = 0; i < totalChunks; i++)
        {
            int offset  = i * CHUNK_PAYLOAD;
            int len     = Math.Min(CHUNK_PAYLOAD, jpeg.Length - offset);
            var packet  = new byte[HEADER_SIZE + len];

            // Header
            packet[0] = (byte)(frameId >> 8);
            packet[1] = (byte)(frameId & 0xFF);
            packet[2] = (byte)i;
            packet[3] = (byte)totalChunks;

            // Payload
            Buffer.BlockCopy(jpeg, offset, packet, HEADER_SIZE, len);

            try
            {
                senderClient.Send(packet, packet.Length, senderEndpoint);
            }
            catch (SocketException ex)
            {
                Debug.LogWarning($"[UdpVideoTransport] Send error: {ex.Message}");
                return;
            }
        }
    }

    // ── Receiver ──────────────────────────────────────────────────────────

    private UdpClient  receiverClient;
    private Thread     receiverThread;
    private volatile bool receiverRunning;

    // Reassembly state
    private ushort assemblyFrameId;
    private int    assemblyExpectedChunks;
    private int    assemblyReceivedCount;
    private byte[][] assemblyChunks;
    private int[]    assemblyChunkLengths;

    // Output queue — only the latest complete frame matters
    private readonly ConcurrentQueue<byte[]> frameQueue = new();

    public void StartReceiver(int port)
    {
        StopReceiver();
        receiverClient = new UdpClient(port);
        receiverClient.Client.ReceiveBufferSize = 1024 * 1024; // 1 MB buffer
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
        if (receiverClient != null)
        {
            receiverClient.Close();
            receiverClient = null;
        }
        if (receiverThread != null && receiverThread.IsAlive)
        {
            receiverThread.Join(500);
            receiverThread = null;
        }
        // Drain queue
        while (frameQueue.TryDequeue(out _)) { }
        Debug.Log("[UdpVideoTransport] Receiver stopped.");
    }

    public bool TryDequeue(out byte[] jpeg)
    {
        jpeg = null;
        // Drain to get the latest frame (skip stale ones)
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

                // New frame — reset assembly
                if (frameId != assemblyFrameId || assemblyChunks == null || totalChunks != assemblyExpectedChunks)
                {
                    assemblyFrameId        = frameId;
                    assemblyExpectedChunks = totalChunks;
                    assemblyReceivedCount  = 0;
                    assemblyChunks         = new byte[totalChunks][];
                    assemblyChunkLengths   = new int[totalChunks];
                }

                if (chunkIdx >= totalChunks) continue;
                if (assemblyChunks[chunkIdx] != null) continue; // duplicate

                assemblyChunks[chunkIdx]       = new byte[payloadLen];
                assemblyChunkLengths[chunkIdx] = payloadLen;
                Buffer.BlockCopy(data, HEADER_SIZE, assemblyChunks[chunkIdx], 0, payloadLen);
                assemblyReceivedCount++;

                // All chunks received — reassemble
                if (assemblyReceivedCount == assemblyExpectedChunks)
                {
                    int totalLen = 0;
                    for (int i = 0; i < assemblyExpectedChunks; i++)
                        totalLen += assemblyChunkLengths[i];

                    var jpeg = new byte[totalLen];
                    int offset = 0;
                    for (int i = 0; i < assemblyExpectedChunks; i++)
                    {
                        Buffer.BlockCopy(assemblyChunks[i], 0, jpeg, offset, assemblyChunkLengths[i]);
                        offset += assemblyChunkLengths[i];
                    }

                    frameQueue.Enqueue(jpeg);
                    assemblyChunks = null; // ready for next frame
                }
            }
            catch (SocketException)
            {
                // Expected on StopReceiver() → socket closed
                if (!receiverRunning) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────

    /// <summary>Get this machine's local IPv4 address (for LAN communication).</summary>
    public static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Connect to a public address to determine the outgoing interface
            // (no actual traffic is sent for UDP)
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
