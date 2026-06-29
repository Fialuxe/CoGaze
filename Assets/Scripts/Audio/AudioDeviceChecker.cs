using System;
using UnityEngine;

// Pre-experiment mic check: VU meter, device picker; auto-confirms on Quest after k_autoSeconds; destroys itself on confirm.
public class AudioDeviceChecker : MonoBehaviour
{
    public event Action<string> OnDeviceConfirmed;

    private string[] _devices;
    private int      _selectedIndex;
    private bool     _isExpert;
    private bool     _confirmed;

    private AudioClip _testClip;
    private int       _lastSample;
    private float     _smoothedLevel;
    private bool      _isTesting;

    private float       _countdown;
    private const float k_autoSeconds = 10f;

    // Cached GUI styles — built once on the first OnGUI call.
    private GUIStyle _titleStyle;
    private GUIStyle _bodyStyle;
    private GUIStyle _nameStyle;
    private GUIStyle _hintStyle;

    public void Initialize(bool isExpert)
    {
        _isExpert  = isExpert;
        _devices   = Microphone.devices;
        _countdown = k_autoSeconds;
        if (_devices.Length > 0) StartTest();
    }

    private void BuildStyles()
    {
        if (_titleStyle != null) return;

        _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _titleStyle.normal.textColor = Color.white;

        _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        _bodyStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

        _nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        _nameStyle.normal.textColor = Color.yellow;

        _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        _hintStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
    }

    private void Update()
    {
        UpdateVU();
        if (_isExpert || _confirmed) return;

        _countdown -= Time.deltaTime;

        bool ovrBtn = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        // OVRInput is only available on actual Android/Quest builds.
        // Meta Link / Simulator runs in the Editor and does not provide OVRInput,
        // so the block is excluded there — the auto-countdown will still fire.
        try { ovrBtn = OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three); }
        catch { }
#endif
        if (_countdown <= 0f || ovrBtn) Confirm();
    }

    private void OnGUI()
    {
        if (_confirmed) return;
        BuildStyles();

        const float PW = 580f;
        const float PH = 310f;
        float px = (Screen.width  - PW) * 0.5f;
        float py = (Screen.height - PH) * 0.5f;

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Box(new Rect(px, py, PW, PH), "");

        float y = py + 18f;

        GUI.Label(new Rect(px + 10, y, PW - 20, 30), "Microphone Check", _titleStyle);
        y += 36f;

        string countLine = _devices.Length == 0 ? "No microphone detected!" : $"{_devices.Length} device(s) found";
        GUI.Label(new Rect(px + 10, y, PW - 20, 24), countLine, _bodyStyle);
        y += 30f;

        const float NAV = 50f;
        if (_devices.Length > 1 && GUI.Button(new Rect(px + 10, y, NAV, 32), "◀"))
            SelectOffset(-1);

        string devLabel = _devices.Length > 0 ? _devices[_selectedIndex] : "(none)";
        if (devLabel.Length > 50) devLabel = "…" + devLabel.Substring(devLabel.Length - 47);
        GUI.Label(new Rect(px + 65, y, PW - 130, 32), devLabel, _nameStyle);

        if (_devices.Length > 1 && GUI.Button(new Rect(px + PW - 60, y, NAV, 32), "▶"))
            SelectOffset(+1);
        y += 40f;

        const float VU_H = 20f;
        float vuX = px + 30f, vuW = PW - 60f;
        GUI.color = new Color(0.15f, 0.15f, 0.15f);
        GUI.DrawTexture(new Rect(vuX, y, vuW, VU_H), Texture2D.whiteTexture);
        GUI.color = new Color(0.2f, 0.85f, 0.3f);
        GUI.DrawTexture(new Rect(vuX, y, vuW * Mathf.Clamp01(_smoothedLevel * 5f), VU_H), Texture2D.whiteTexture);
        GUI.color = Color.white;
        y += VU_H + 12f;

        string hintMsg = _isExpert
            ? "Select your microphone, then press Proceed."
            : $"Auto-proceed in {Mathf.CeilToInt(Mathf.Max(0f, _countdown))}s  ·  Press A/X on controller to confirm now";
        GUI.Label(new Rect(px + 10, y, PW - 20, 26), hintMsg, _hintStyle);
        y += 34f;

        const float BW = 160f, BH = 40f;
        if (GUI.Button(new Rect(px + (PW - BW) * 0.5f, y, BW, BH), "Proceed"))
            Confirm();
    }

    private void SelectOffset(int delta)
    {
        if (_devices.Length == 0) return;
        StopTest();
        _selectedIndex = (_selectedIndex + delta + _devices.Length) % _devices.Length;
        StartTest();
    }

    private void StartTest()
    {
        if (_devices.Length == 0) return;
        _testClip      = Microphone.Start(_devices[_selectedIndex], true, 2, 16000);
        _lastSample    = 0;
        _smoothedLevel = 0f;
        _isTesting     = true;
    }

    private void StopTest()
    {
        if (!_isTesting || _selectedIndex >= _devices.Length) return;
        Microphone.End(_devices[_selectedIndex]);
        _testClip  = null;
        _isTesting = false;
    }

    private void UpdateVU()
    {
        if (!_isTesting || _testClip == null) return;
        string dev    = _selectedIndex < _devices.Length ? _devices[_selectedIndex] : "";
        int    pos    = Microphone.GetPosition(dev);
        int available = (pos - _lastSample + _testClip.samples) % _testClip.samples;
        if (available < 64) return;
        float[] buf = new float[available];
        _testClip.GetData(buf, _lastSample);
        _lastSample = pos;
        float peak = 0f;
        foreach (float s in buf) peak = Mathf.Max(peak, Mathf.Abs(s));
        _smoothedLevel = Mathf.Lerp(_smoothedLevel, peak, 0.25f);
    }

    private void Confirm()
    {
        if (_confirmed) return;
        _confirmed = true;
        StopTest();
        string dev = _devices.Length > 0 ? _devices[_selectedIndex] : "";
        FileLogger.Log("AudioDeviceChecker", $"Confirmed device: '{(string.IsNullOrEmpty(dev) ? "(default)" : dev)}'");
        OnDeviceConfirmed?.Invoke(dev);
        Destroy(this);
    }

    private void OnDestroy() => StopTest();
}
