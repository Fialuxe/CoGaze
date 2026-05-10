using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Reliable audio transport over TCP.
///
/// Replaces UdpAudioTransport to eliminate packet loss on Wi-Fi.
/// On a local LAN, TCP retransmission completes in under 5 ms,
/// which is fully absorbed by VoiceCommunicator's 150 ms jitter buffer.
///
/// Framing: each message is prefixed with a 4-byte little-endian length,
/// followed by the payload bytes.
///
/// Sender reconnects automatically if the connection drops.
/// Receiver re-accepts after a client disconnects so the next session works
/// without restarting.
/// </summary>
public class TcpAudioTransport
{
    /// <summary>Invoked on the receiver background thread whenever a complete message arrives.</summary>
    public Action<byte[]> OnAudioReceived;

    // ── Sender (client) ───────────────────────────────────────────────────

    private TcpClient      senderClient;
    private NetworkStream  senderStream;
    private volatile bool  senderRunning;
    private Thread         senderThread;
    private readonly object sendLock = new object();

    // Pre-allocated send buffer: 4-byte header + max audio payload.
    // Reused every Send() call to avoid per-packet heap allocation.
    private readonly byte[] _sendBuf = new byte[4 + 4096];

    public void StartSender(string remoteIp, int remotePort)
    {
        StopSender();
        senderRunning = true;
        senderThread  = new Thread(() => ConnectLoop(remoteIp, remotePort))
            { IsBackground = true, Name = "TcpAudioSender" };
        senderThread.Start();
        Debug.Log($"[TcpAudioTransport] Sender → {remoteIp}:{remotePort}");
    }

    private void ConnectLoop(string ip, int port)
    {
        while (senderRunning)
        {
            try
            {
                var client = new TcpClient();
                client.NoDelay = true;
                client.Connect(ip, port);
                // TCP_QUICKACK (Linux/Android = 12): send ACKs immediately instead of
                // waiting up to 200 ms (delayed-ACK timer).  Without this, the remote
                // sender's TCP congestion window grows slowly, causing steady-state
                // throughput to drop below the audio rate and starving the buffer.
                SetQuickAck(client);

                lock (sendLock)
                {
                    senderClient = client;
                    senderStream = client.GetStream();
                }
                Debug.Log($"[TcpAudioTransport] Sender connected to {ip}:{port}");

                // Stay connected until StopSender or Send() clears senderStream on error
                while (senderRunning)
                {
                    lock (sendLock) { if (senderStream == null) break; }
                    Thread.Sleep(50);
                }
                if (!senderRunning) break;

                // senderStream cleared by a failed Send() — reconnect immediately.
                // 50 ms pause is enough for the OS to clean up the socket; the 80 ms
                // jitter buffer on the receiver absorbs this gap without audible dropout.
                Debug.LogWarning("[TcpAudioTransport] Sender connection lost — reconnecting");
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                if (!senderRunning) break;
                Debug.LogWarning($"[TcpAudioTransport] Connect failed: {ex.Message} — retry in 100 ms");
                Thread.Sleep(100);
            }
        }
    }

    public void StopSender()
    {
        senderRunning = false;
        lock (sendLock)
        {
            senderStream?.Close();
            senderClient?.Close();
            senderStream = null;
            senderClient = null;
        }
        senderThread?.Join(500);
        senderThread = null;
    }

    public void Send(byte[] data)
    {
        if (data == null || data.Length == 0) return;
        lock (sendLock)
        {
            if (senderStream == null) return;
            if (4 + data.Length > _sendBuf.Length)
            {
                Debug.LogWarning($"[TcpAudioTransport] Packet too large ({data.Length} B) — dropped");
                return;
            }
            try
            {
                // Write header + payload in one call so the OS sees a single TCP segment.
                // Two separate Write() calls with TCP_NODELAY would send two segments,
                // which adds a measurable delay on some network stacks.
                int len = data.Length;
                _sendBuf[0] = (byte)(len);
                _sendBuf[1] = (byte)(len >> 8);
                _sendBuf[2] = (byte)(len >> 16);
                _sendBuf[3] = (byte)(len >> 24);
                Buffer.BlockCopy(data, 0, _sendBuf, 4, len);
                senderStream.Write(_sendBuf, 0, 4 + len);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TcpAudioTransport] Send error: {ex.Message}");
                senderStream = null;
                senderClient = null;
            }
        }
    }

    // ── Receiver (server) ─────────────────────────────────────────────────

    private TcpListener   listener;
    private Thread        receiverThread;
    private volatile bool receiverRunning;

    public void StartReceiver(int port)
    {
        StopReceiver();
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        receiverRunning = true;
        receiverThread  = new Thread(ReceiveLoop)
            { IsBackground = true, Name = "TcpAudioReceiver" };
        receiverThread.Start();
        Debug.Log($"[TcpAudioTransport] Receiver on port {port}");
    }

    public void StopReceiver()
    {
        receiverRunning = false;
        listener?.Stop();
        listener       = null;
        receiverThread?.Join(500);
        receiverThread = null;
    }

    private void ReceiveLoop()
    {
        while (receiverRunning)
        {
            TcpClient client = null;
            try
            {
                client = listener.AcceptTcpClient();
                client.NoDelay = true;
                SetQuickAck(client);

                var    stream = client.GetStream();
                byte[] lenBuf = new byte[4];

                while (receiverRunning)
                {
                    if (!ReadExact(stream, lenBuf, 4)) break;

                    int msgLen = lenBuf[0] | (lenBuf[1] << 8) | (lenBuf[2] << 16) | (lenBuf[3] << 24);
                    if (msgLen <= 0 || msgLen > 65536)
                    {
                        Debug.LogWarning($"[TcpAudioTransport] Bad message length {msgLen} — dropping connection");
                        break;
                    }

                    byte[] data = new byte[msgLen];
                    if (!ReadExact(stream, data, msgLen)) break;

                    OnAudioReceived?.Invoke(data);
                }
            }
            catch (SocketException)         { if (!receiverRunning) break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)            { Debug.LogWarning($"[TcpAudioTransport] Recv error: {ex.Message}"); }
            finally                         { client?.Close(); }
        }
    }

    // Reads exactly `count` bytes from stream into buf starting at offset 0.
    // Returns false if the stream closes before count bytes arrive.
    private static bool ReadExact(NetworkStream stream, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buf, read, count - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    // TCP_QUICKACK (value 12 on Linux/Android) disables the 200 ms delayed-ACK timer.
    // No-op on Windows/macOS where the option is unsupported; the catch swallows it.
    private static void SetQuickAck(TcpClient client)
    {
        try { client.Client.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)12, 1); }
        catch { /* not available on all platforms */ }
    }
}
