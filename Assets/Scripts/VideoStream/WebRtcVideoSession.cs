using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.WebRTC;

/// <summary>
/// Pure WebRTC peer connection — no Photon dependency.
///
/// Worker (offerer)  : StartAsOfferer(captureRT) → fires OnSendOffer, OnSendIce
/// Expert (answerer) : StartAsAnswerer(onFrame)  → waits for ApplyRemoteOffer(),
///                     then fires OnSendAnswer, OnSendIce
///
/// Caller is responsible for transporting signaling messages in both directions:
///   outbound: subscribe to OnSendOffer / OnSendAnswer / OnSendIce
///   inbound:  call ApplyRemoteOffer / ApplyRemoteAnswer / AddRemoteIce
/// </summary>
public class WebRtcVideoSession : MonoBehaviour
{
    // Signaling event codes — defined here so setup files can reference them without a separate file.
    public const byte EVT_OFFER  = 60;
    public const byte EVT_ANSWER = 61;
    public const byte EVT_ICE    = 62;
    public const byte EVT_HANGUP = 63;

    private static bool _loopStarted;

    // ── Outbound signaling events (caller routes these to the remote peer) ─

    public event Action<string>              OnSendOffer;
    public event Action<string>              OnSendAnswer;
    public event Action<string, string, int> OnSendIce;    // candidate, sdpMid, sdpMLineIndex

    // ── Internal state ─────────────────────────────────────────────────────

    private RTCPeerConnection _pc;
    private VideoStreamTrack  _sendTrack;
    private RenderTexture     _sourceRT;
    private Action<Texture>   _onFrame;
    private bool              _isOfferer;
    private bool              _remoteDescSet;
    private bool              _stopped;
    private readonly List<RTCIceCandidateInit> _pending = new();

    // ── Lifecycle ───────────────────────────────────────────────────────────

    private void Awake()
    {
        if (!_loopStarted)
        {
            StartCoroutine(WebRTC.Update());
            _loopStarted = true;
            Debug.Log("[WebRTC] WebRTC.Update() loop started.");
        }
    }

    private void OnDestroy()
    {
        CleanUp();
        _loopStarted = false; // allow next instance (reconnect) to restart the loop
    }

    // ── Worker side ─────────────────────────────────────────────────────────

    /// <summary>
    /// Call once when the Expert is confirmed in the room.
    /// Fires OnSendOffer when the SDP is ready.
    /// </summary>
    public void StartAsOfferer(RenderTexture captureRT)
    {
        if (_stopped) return;
        _isOfferer = true;
        _sourceRT  = captureRT;
        StartCoroutine(DoOffer());
    }

    private static RTCConfiguration BuildIceConfig()
    {
        return new RTCConfiguration
        {
            iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } }
        };
    }

    private IEnumerator DoOffer()
    {
        Debug.Log("[WebRTC] DoOffer: start.");
        ClosePeerConnection();
        var cfg = BuildIceConfig();
        _pc = new RTCPeerConnection(ref cfg);
        Debug.Log("[WebRTC] DoOffer: RTCPeerConnection created.");
        BindCommonCallbacks();

        var stream = new MediaStream();
        Debug.Log($"[WebRTC] DoOffer: creating VideoStreamTrack  RT={_sourceRT?.width}x{_sourceRT?.height}  fmt={_sourceRT?.graphicsFormat}  created={_sourceRT?.IsCreated()}");
        _sendTrack = new VideoStreamTrack(_sourceRT);
        Debug.Log("[WebRTC] DoOffer: VideoStreamTrack created.");
        _pc.AddTrack(_sendTrack, stream);

        var offerOp = _pc.CreateOffer();
        Debug.Log("[WebRTC] DoOffer: waiting for CreateOffer…");
        yield return StartCoroutine(WaitWithTimeout(offerOp, 10f, "CreateOffer"));
        if (!offerOp.IsDone) yield break;
        if (offerOp.IsError) { Debug.LogError($"[WebRTC] CreateOffer error: {offerOp.Error.message}"); yield break; }
        Debug.Log("[WebRTC] DoOffer: CreateOffer done.");

        var desc = offerOp.Desc;
        var setLocalOp = _pc.SetLocalDescription(ref desc);
        Debug.Log("[WebRTC] DoOffer: waiting for SetLocalDescription…");
        yield return StartCoroutine(WaitWithTimeout(setLocalOp, 10f, "SetLocalDescription"));
        if (!setLocalOp.IsDone) yield break;
        if (setLocalOp.IsError) { Debug.LogError($"[WebRTC] SetLocalDesc error: {setLocalOp.Error.message}"); yield break; }
        Debug.Log("[WebRTC] DoOffer: SetLocalDescription done.");

        OnSendOffer?.Invoke(desc.sdp);
        Debug.Log("[WebRTC] Offer created and dispatched.");
    }

    private IEnumerator WaitWithTimeout(AsyncOperationBase op, float seconds, string label)
    {
        float elapsed = 0f;
        while (!op.IsDone && elapsed < seconds)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
        if (!op.IsDone)
            Debug.LogError($"[WebRTC] {label} timed out after {seconds}s — WebRTC callback pump may be broken.");
    }

    // ── Expert side ─────────────────────────────────────────────────────────

    /// <summary>
    /// Call once during initialization.
    /// onFrame is invoked on the main thread each time a decoded video frame arrives.
    /// </summary>
    public void StartAsAnswerer(Action<Texture> onFrame)
    {
        _isOfferer = false;
        _onFrame   = onFrame;
        Debug.Log("[WebRTC] Answerer ready — waiting for offer.");
    }

    // ── Inbound signaling (called by the owner with data from the remote peer) ─

    public void ApplyRemoteOffer(string sdp)
    {
        if (_stopped) return;
        StartCoroutine(DoAnswer(sdp));
    }

    public void ApplyRemoteAnswer(string sdp)
    {
        if (_stopped) return;
        StartCoroutine(DoApplyAnswer(sdp));
    }

    public void AddRemoteIce(string candidate, string sdpMid, int sdpMLineIndex)
    {
        var init = new RTCIceCandidateInit
        {
            candidate     = candidate,
            sdpMid        = sdpMid,
            sdpMLineIndex = (ushort)sdpMLineIndex
        };
        if (_remoteDescSet) _pc?.AddIceCandidate(new RTCIceCandidate(init));
        else                _pending.Add(init);
    }

    // ── Internal coroutines ─────────────────────────────────────────────────

    private IEnumerator DoAnswer(string offerSdp)
    {
        ClosePeerConnection();
        var cfg = BuildIceConfig();
        _pc = new RTCPeerConnection(ref cfg);
        BindCommonCallbacks();

        _pc.OnTrack = e =>
        {
            if (e.Track is VideoStreamTrack vt)
                vt.OnVideoReceived += tex => _onFrame?.Invoke(tex);
        };

        var remoteDesc = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = offerSdp };
        var setRemoteOp = _pc.SetRemoteDescription(ref remoteDesc);
        yield return setRemoteOp;
        if (setRemoteOp.IsError) { Debug.LogError($"[WebRTC] SetRemoteDesc(offer): {setRemoteOp.Error.message}"); yield break; }

        _remoteDescSet = true;
        FlushPending();

        var answerOp = _pc.CreateAnswer();
        yield return answerOp;
        if (answerOp.IsError) { Debug.LogError($"[WebRTC] CreateAnswer: {answerOp.Error.message}"); yield break; }

        var desc = answerOp.Desc;
        var setLocalOp = _pc.SetLocalDescription(ref desc);
        yield return setLocalOp;
        if (setLocalOp.IsError) { Debug.LogError($"[WebRTC] SetLocalDesc(answer): {setLocalOp.Error.message}"); yield break; }

        OnSendAnswer?.Invoke(desc.sdp);
        Debug.Log("[WebRTC] Answer created and dispatched.");
    }

    private IEnumerator DoApplyAnswer(string answerSdp)
    {
        if (_pc == null) yield break;
        var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answerSdp };
        var op = _pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError) { Debug.LogError($"[WebRTC] SetRemoteDesc(answer): {op.Error.message}"); yield break; }
        _remoteDescSet = true;
        FlushPending();
        Debug.Log("[WebRTC] Answer applied — ICE negotiating.");
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private void BindCommonCallbacks()
    {
        _remoteDescSet = false;
        _pending.Clear();

        _pc.OnIceCandidate = c =>
        {
            if (string.IsNullOrEmpty(c.Candidate)) return;
            OnSendIce?.Invoke(c.Candidate, c.SdpMid ?? "", c.SdpMLineIndex ?? 0);
        };

        _pc.OnIceConnectionChange = s => Debug.Log($"[WebRTC] ICE: {s}");

        _pc.OnConnectionStateChange = s =>
        {
            Debug.Log($"[WebRTC] Connection: {s}");
            if (!_stopped && s == RTCPeerConnectionState.Failed)
            {
                Debug.LogWarning("[WebRTC] Failed — restarting in 3 s.");
                StartCoroutine(RestartAfterDelay());
            }
        };
    }

    private IEnumerator RestartAfterDelay()
    {
        yield return new WaitForSeconds(3f);
        if (_stopped || !_isOfferer) yield break;
        Debug.Log("[WebRTC] Restarting offer.");
        yield return StartCoroutine(DoOffer());
    }

    private void FlushPending()
    {
        foreach (var init in _pending)
            _pc?.AddIceCandidate(new RTCIceCandidate(init));
        _pending.Clear();
    }

    private void ClosePeerConnection()
    {
        _sendTrack?.Dispose(); _sendTrack = null;
        _pc?.Close();
        _pc?.Dispose();
        _pc = null;
        _remoteDescSet = false;
    }

    public void Stop()
    {
        _stopped = true;
        CleanUp();
    }

    private void CleanUp()
    {
        ClosePeerConnection();
        _pending.Clear();
    }
}
