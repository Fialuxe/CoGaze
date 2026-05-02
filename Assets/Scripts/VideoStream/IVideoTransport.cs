/// <summary>
/// Video transport abstraction layer.
/// Current implementation: UdpVideoTransport (same-LAN, lowest latency).
/// Future implementations: WebRtcVideoTransport, PhotonVideoTransport.
/// </summary>
public interface IVideoTransport
{
    // ── Sender (Worker side) ──────────────────────────────────────────────

    /// <summary>Start sending to the given remote IP address.</summary>
    void StartSender(string remoteIp, int port);

    /// <summary>Stop sending.</summary>
    void StopSender();

    /// <summary>Enqueue a JPEG frame for transmission.</summary>
    void Send(byte[] jpeg);

    // ── Receiver (Expert side) ────────────────────────────────────────────

    /// <summary>Start listening for incoming frames on the given port.</summary>
    void StartReceiver(int port);

    /// <summary>Stop listening.</summary>
    void StopReceiver();

    /// <summary>
    /// Try to dequeue the latest received JPEG frame (main-thread safe).
    /// Returns false if no new frame is available.
    /// </summary>
    bool TryDequeue(out byte[] jpeg);
}
