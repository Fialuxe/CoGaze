using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI panel for configuring the Python/OSC host IP address.
/// Displayed on the Expert (PC) startup screen and instantiated at runtime
/// by SceneBootstrapper2.
/// </summary>
public class ConnectionConfigUI : MonoBehaviour
{
    private InputField _hostField;

    public void Initialize(StartupConfig config)
    {
        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth     = true;
        layout.childControlHeight    = false;
        layout.childForceExpandWidth = true;
        layout.spacing               = 8f;
        layout.padding               = new RectOffset(12, 12, 12, 12);

        AddLabel(CoGazeStrings.Config_PythonHostLabel);

        var fieldGo = DefaultControls.CreateInputField(new DefaultControls.Resources());
        fieldGo.name = "PythonHostField";
        fieldGo.transform.SetParent(transform, false);
        AddLayoutElement(fieldGo, preferredHeight: 36f);

        _hostField = fieldGo.GetComponent<InputField>();
        StyleInputField(_hostField);
        _hostField.text = config != null ? config.pythonHost : "127.0.0.1";

        AddHint(CoGazeStrings.Config_PythonHostHint);
    }

    public void Apply(StartupConfig target)
    {
        target.pythonHost = string.IsNullOrWhiteSpace(_hostField?.text)
            ? "127.0.0.1"
            : _hostField.text.Trim();
    }

    private void AddLabel(string text)
    {
        var go   = new GameObject(text);
        go.transform.SetParent(transform, false);
        var t    = go.AddComponent<Text>();
        t.text      = text;
        t.fontSize  = 18;
        t.color     = Color.white;
        t.alignment = TextAnchor.MiddleLeft;
        t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        AddLayoutElement(go, preferredHeight: 24f);
    }

    private void AddHint(string text)
    {
        var go   = new GameObject("Hint");
        go.transform.SetParent(transform, false);
        var t    = go.AddComponent<Text>();
        t.text       = text;
        t.fontSize   = 13;
        t.color      = new Color(0.7f, 0.7f, 0.7f);
        t.alignment  = TextAnchor.MiddleLeft;
        t.fontStyle  = FontStyle.Italic;
        t.font       = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        AddLayoutElement(go, preferredHeight: 20f);
    }

    private static void AddLayoutElement(GameObject go, float preferredHeight)
    {
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.preferredHeight  = preferredHeight;
        le.flexibleWidth    = 1f;
    }

    private static void StyleInputField(InputField field)
    {
        if (field == null) return;
        var img = field.GetComponent<Image>();
        if (img != null) img.color = Color.white;
        if (field.textComponent != null)
        {
            field.textComponent.color    = new Color(0.1f, 0.1f, 0.1f);
            field.textComponent.fontSize = 18;
        }
        if (field.placeholder is Text placeholder)
        {
            placeholder.color    = new Color(0.5f, 0.5f, 0.5f);
            placeholder.fontSize = 18;
        }
    }
}
