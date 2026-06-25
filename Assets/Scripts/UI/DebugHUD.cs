using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Voice.Unity;
using Photon.Voice.PUN;

#if UNITY_ANDROID && !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// In-headset diagnostic overlay for Meta Quest.
/// Toggle on/off with Y button (left controller).
/// Shows Photon state, room info, player roles, MRUK status, and recent warnings/errors.
/// Works from app launch — independent of the experiment flow.
/// </summary>
public class DebugHUD : MonoBehaviour
{
    private Canvas _canvas;
    private Text   _text;
    private bool   _visible = false;

    private readonly List<string> _recentLogs = new List<string>();
    private const int MaxLogs = 6;

    private float _lastRefresh;
    private const float RefreshInterval = 0.5f;

    // Voice (HMD→PC outgoing mic level via PV2 Recorder — no extra Microphone.Start, so no
    // Android mic contention) + QR detection feedback so testers can see both work in-headset.
    private Recorder _recorder;
    private Speaker  _remoteSpeaker;   // remote peer's voice playback (RX indicator)
    private QRSpatialManager _qr;
    private int    _qrCount;
    private string _qrLast = "";
    private string _qrLastTime = "";
    private System.Action<string, Vector3, Quaternion> _qrCb;

    private void Start()
    {
        Application.logMessageReceived += OnLogMessage;
        _qrCb = (id, pos, rot) =>
        {
            _qrCount++;
            _qrLast = id;
            _qrLastTime = System.DateTime.Now.ToString("HH:mm:ss");
        };
        BuildCanvas();
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLogMessage;
        if (_qr != null && _qrCb != null) _qr.OnMarkerDetected -= _qrCb;
    }

    private void OnLogMessage(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Warning) return;
        string prefix = (type == LogType.Warning) ? "W" : "E";
        string ts  = System.DateTime.Now.ToString("HH:mm:ss");
        string msg = condition.Length > 90 ? condition.Substring(0, 90) + "…" : condition;
        _recentLogs.Add($"[{ts}][{prefix}] {msg}");
        if (_recentLogs.Count > MaxLogs) _recentLogs.RemoveAt(0);
    }

    private void Update()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            SetVisible(!_visible);
#else
        if (Input.GetKeyDown(KeyCode.Tab))
            SetVisible(!_visible);
#endif
        if (!_visible) return;
        if (Time.time - _lastRefresh < RefreshInterval) return;
        _lastRefresh = Time.time;
        RefreshText();
    }

    private void SetVisible(bool v)
    {
        _visible = v;
        if (_canvas != null) _canvas.gameObject.SetActive(v);
        if (v) RefreshText();
    }

    private void RefreshText()
    {
        if (_text == null) return;
        var sb = new StringBuilder();

        sb.AppendLine(CoGazeStrings.Debug_Title);

        sb.Append("[Photon] ");
        sb.AppendLine(PhotonNetwork.NetworkClientState.ToString());
        if (PhotonNetwork.InRoom)
        {
            sb.AppendLine($"  Room : {PhotonNetwork.CurrentRoom?.Name}");
            sb.AppendLine($"  Players ({PhotonNetwork.CurrentRoom.PlayerCount}):");
            foreach (var p in PhotonNetwork.PlayerList)
            {
                string role = RoleManager.GetPlayerRole(p);
                string mark = p.IsLocal ? "▶" : "  ";
                sb.AppendLine($"  {mark}{p.NickName} [{role}]");
            }
        }
        else
        {
            sb.AppendLine("  (not in room)");
        }

        sb.AppendLine($"[LocalRole] {RoleManager.LocalRole}");

#if UNITY_ANDROID && !UNITY_EDITOR
        sb.AppendLine($"[MRUK] {(MRUK.Instance != null ? "OK" : "null — QR unavailable")}");
#endif

        // [Voice] — outgoing mic level from PV2 Recorder. Bar moves when you speak ⇒ your
        // voice is being captured & transmitted (HMD→PC). TX● = PV2 is actively sending.
        if (_recorder == null) _recorder = FindAnyObjectByType<Recorder>();
        if (_remoteSpeaker == null)
        {
            foreach (var pvv in FindObjectsByType<PhotonVoiceView>(FindObjectsSortMode.None))
                if (pvv.GetComponent<PhotonView>()?.IsMine == false && pvv.SpeakerInUse != null)
                { _remoteSpeaker = pvv.SpeakerInUse; break; }
        }
        sb.Append("[Voice] ");
        if (_recorder != null)
        {
            var  lm  = _recorder.LevelMeter;
            float lv = lm != null ? Mathf.Clamp01(lm.CurrentPeakAmp * 8f) : 0f;
            int  n   = Mathf.RoundToInt(lv * 12f);
            string tx = _recorder.IsCurrentlyTransmitting ? "TX●" : "tx-";       // your voice out (HMD→PC)
            string rx = (_remoteSpeaker != null && _remoteSpeaker.IsPlaying) ? "RX●" : "rx-"; // remote voice in
            sb.AppendLine($"mic [{new string('#', n)}{new string('.', 12 - n)}] {tx} {rx}");
        }
        else sb.AppendLine("(recorder not found)");

        // [QR] — increments on every detection (OnMarkerDetected). Lets a tester confirm in the
        // identification task whether the QR is actually being recognised.
        if (_qr == null)
        {
            _qr = FindAnyObjectByType<QRSpatialManager>();
            if (_qr != null && _qrCb != null) _qr.OnMarkerDetected += _qrCb;
        }
        sb.Append("[QR] ");
        if (_qr != null)
            sb.AppendLine(_qrCount > 0
                ? $"detected x{_qrCount}  last '{_qrLast}' @{_qrLastTime}"
                : "none yet (look at QR; tracking on)");
        else
            sb.AppendLine("(manager not found)");

        // [Poke] — QuestionnairePokeInput status so testers can confirm touch input works
        var qpGo = GameObject.Find("QuestionnairePoke");
        sb.Append("[Poke] QS:");
        if (qpGo == null)
            sb.AppendLine("not created");
        else if (!qpGo.activeInHierarchy)
            sb.AppendLine("inactive (between rounds)");
        else
        {
            var qp = qpGo.GetComponent<QuestionnairePokeInput>();
            sb.AppendLine(qp != null && qp.IsEngaged ? "ENGAGED" : "active");
        }

        sb.AppendLine("[Logs]");
        if (_recentLogs.Count == 0)
            sb.AppendLine("  (no warnings/errors)");
        else
            foreach (var l in _recentLogs)
                sb.AppendLine("  " + l);

        _text.text = sb.ToString();
    }

    private void BuildCanvas()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        Transform anchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;

        var go = new GameObject("DebugHUD_Canvas");
        if (anchor != null)
        {
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = new Vector3(0.32f, 0.08f, 0.80f);
            go.transform.localRotation = Quaternion.identity;
        }
        go.transform.localScale = Vector3.one * 0.001f;

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(420f, 320f);

        var bgGo  = new GameObject("BG");
        bgGo.transform.SetParent(go.transform, false);
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.88f);
        var bgRt  = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        _text = textGo.AddComponent<Text>();
        _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                  ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        _text.fontSize           = 17;
        _text.color              = Color.white;
        _text.horizontalOverflow = HorizontalWrapMode.Wrap;
        _text.verticalOverflow   = VerticalWrapMode.Overflow;
        var textRt = textGo.GetComponent<RectTransform>();
        textRt.anchorMin = new Vector2(0f, 0f);
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(8f,  4f);
        textRt.offsetMax = new Vector2(-8f, -8f);

        go.SetActive(false);
        Debug.Log("[DebugHUD] Ready — press Y (left controller) to toggle.");
    }
}
