using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Startup panel for the Expert (PC) role.
/// Lets the experimenter enter participant ID and condition order index
/// before the experiment begins.
///
/// Instantiated at runtime by SceneBootstrapper2 and attached to a child
/// panel GameObject inside a Screen Space Overlay Canvas.
///
/// Usage:
///   var ui = panelGo.AddComponent&lt;ParticipantConfigUI&gt;();
///   ui.Initialize(config);   // populate fields from config
///   // …user edits fields…
///   ui.Apply(config);        // write UI values back to config
/// </summary>
public class ParticipantConfigUI : MonoBehaviour
{
    private InputField _idField;
    private Dropdown   _orderDropdown;

    // ── Public interface ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the UI elements and populates them with values from <paramref name="config"/>.
    /// Call once after the parent Canvas has been set up.
    /// </summary>
    public void Initialize(StartupConfig config)
    {
        // Vertical layout so label + control stack cleanly
        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth    = true;
        layout.childControlHeight   = false;
        layout.childForceExpandWidth = true;
        layout.spacing              = 8f;
        layout.padding              = new RectOffset(12, 12, 12, 12);

        // ── Participant ID ────────────────────────────────────────────────

        AddLabel(CoGazeStrings.Config_ParticipantIdLabel);

        var res = DefaultControls.CreateInputField(MakeResources());
        res.name = "ParticipantIdField";
        res.transform.SetParent(transform, false);
        AddLayoutElement(res, preferredHeight: 36f);

        _idField = res.GetComponent<InputField>();
        StyleInputField(_idField);
        _idField.text = config != null ? config.participantId : "";

        // ── Condition order index ─────────────────────────────────────────

        AddLabel(CoGazeStrings.Config_ConditionOrderLabel);

        var dres = DefaultControls.CreateDropdown(MakeResources());
        dres.name = "OrderDropdown";
        dres.transform.SetParent(transform, false);
        AddLayoutElement(dres, preferredHeight: 36f);

        _orderDropdown = dres.GetComponent<Dropdown>();
        StyleDropdown(_orderDropdown);

        // Options must be added before setting value
        _orderDropdown.ClearOptions();
        var options = new System.Collections.Generic.List<Dropdown.OptionData>();
        for (int i = 0; i <= 9; i++)
            options.Add(new Dropdown.OptionData(i.ToString()));
        _orderDropdown.AddOptions(options);

        int orderIndex = config != null ? Mathf.Clamp(config.participantOrderIndex, 0, 9) : 0;
        _orderDropdown.value = orderIndex;
        _orderDropdown.RefreshShownValue();
    }

    /// <summary>
    /// Reads the current UI values and writes them back to <paramref name="target"/>.
    /// </summary>
    public void Apply(StartupConfig target)
    {
        if (target == null)
        {
            Debug.LogError("[ParticipantConfigUI] Apply called with null StartupConfig.");
            return;
        }

        target.participantId         = _idField   != null ? _idField.text        : target.participantId;
        target.participantOrderIndex = _orderDropdown != null ? _orderDropdown.value : target.participantOrderIndex;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a Resources struct used by DefaultControls.
    /// We rely on DefaultControls to wire the correct builtin font.
    /// </summary>
    private static DefaultControls.Resources MakeResources()
    {
        // Provide a minimal sprite for backgrounds; DefaultControls fills in
        // the rest. Passing an empty struct also works — controls are functional
        // but use Unity's defaults.
        return new DefaultControls.Resources();
    }

    /// <summary>Creates a simple Text label and parents it to this transform.</summary>
    private void AddLabel(string labelText)
    {
        var go  = new GameObject(labelText);
        go.transform.SetParent(transform, false);
        var text = go.AddComponent<Text>(); // RequireComponent(RectTransform) — auto-adds RT
        AddLayoutElement(go, preferredHeight: 24f);
        text.text      = labelText;
        text.fontSize  = 18;
        text.color     = Color.white;
        text.alignment = TextAnchor.MiddleLeft;

        // Use the best available builtin font; DefaultControls would handle this
        // automatically for InputField/Dropdown, but Text components need it manually.
        var builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtinFont == null)
            builtinFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (builtinFont != null)
            text.font = builtinFont;
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

        // White background on the image component
        var img = field.GetComponent<Image>();
        if (img != null) img.color = Color.white;

        // Dark text
        if (field.textComponent != null)
        {
            field.textComponent.color    = new Color(0.1f, 0.1f, 0.1f, 1f);
            field.textComponent.fontSize = 18;
        }

        if (field.placeholder is Text placeholder)
        {
            placeholder.color    = new Color(0.5f, 0.5f, 0.5f, 1f);
            placeholder.fontSize = 18;
        }
    }

    private static void StyleDropdown(Dropdown dropdown)
    {
        if (dropdown == null) return;

        var img = dropdown.GetComponent<Image>();
        if (img != null) img.color = Color.white;

        var label = dropdown.captionText;
        if (label != null)
        {
            label.color    = new Color(0.1f, 0.1f, 0.1f, 1f);
            label.fontSize = 18;
        }
    }
}
