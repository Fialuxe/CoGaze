using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Low-latency UDP audio transport.
///
/// Replaces TcpAudioTransport for voice communication. TCP's retransmission
/// mechanism adds 50-200 ms of head-of-line blocking on WiFi packet loss,
/// which drains the jitter buffer and causes audible dropouts. UDP delivers
/// each datagram immediately; any loss is concealed by the Opus FEC/PLC
/// layer in VoiceCommunicator instead.
///
/// Opus-encoded frames are ~40 bytes at 16 kbps/20 ms — well under the
/// 1500-byte Ethernet MTU, so every audio packet fits in a single UDP
/// datagram with no fragmentation.
///
/// Packet format is opaque to this class; VoiceCommunicator owns framing.
/// </summary>
public class UdpAudioTransport
{
    public Action<byte[]> OnAudioReceived;

    // ── Sender ────────────────────────────────────────────────────────────

    private UdpClient     _senderClient;
    private IPEndPoint    _senderEndpoint;
    private readonly object _sendLock = new object();

    public void StartSender(string remoteIp, int remotePort)
    {
        StopSender();
        lock (_sendLock)
        {
            _senderEndpoint = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
            _senderClient   = new UdpClient();
            _senderClient.Client.SendBufferSize = 64 * 1024;
        }
        Debug.Log($"[UdpAudioTransport] Sender → {remoteIp}:{remotePort}");
    }

    public void StopSender()
    {
        lock (_sendLock)
        {
            _senderClient?.Close();
            _senderClient   = null;
            _senderEndpoint = null;
        }
    }

    public void Send(byte[] data)
    {
        if (data == null || data.Length == 0) return;
        lock (_sendLock)
        {
            if (_senderClient == null || _senderEndpoint == null) return;
            try { _senderClient.Send(data, data.Length, _senderEndpoint); }
            catch (SocketException ex)
            {
                Debug.LogWarning($"[UdpAudioTransport] Send error: {ex.Message}");
            }
        }
    }

    // ── Receiver ─────────────────────────────────────────────────────────

    private UdpClient     _receiverClient;
    private Thread        _receiverThread;
    private volatile bool _receiverRunning;

    public void StartReceiver(int port)
    {
        StopReceiver();
        _receiverClient = new UdpClient(port);
        _receiverClient.Client.ReceiveBufferSize = 256 * 1024;
        _receiverRunning = true;
        _receiverThread  = new Thread(ReceiveLoop)
            { IsBackground = true, Name = "UdpAudioReceiver" };
        _receiverThread.Start();
        Debug.Log($"[UdpAudioTransport] Receiver on port {port}");
    }

    public void StopReceiver()
    {
        _receiverRunning = false;
        _receiverClient?.Close();
        _receiverClient = null;
        _receiverThread?.Join(500);
        _receiverThread = null;
    }

    private void ReceiveLoop()
    {
        var anyEp = new IPEndPoint(IPAddress.Any, 0);
        while (_receiverRunning)
        {
            try
            {
                byte[] data = _receiverClient.Receive(ref anyEp);
                OnAudioReceived?.Invoke(data);
            }
            catch (SocketException)         { if (!_receiverRunning) break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UdpAudioTransport] Recv error: {ex.Message}");
            }
        }
    }
}
