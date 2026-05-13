#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom Inspector for URP_WaterShader
/// Place this file in any Editor folder.
/// </summary>
public class WaterShaderGUI : ShaderGUI
{
    // Fold-out states
    bool _showSurface    = true;
    bool _showWaves      = true;
    bool _showNormal     = false;
    bool _showFoam       = true;
    bool _showSpecular   = false;
    bool _showRefraction = false;
    bool _showCaustics   = false;

    static GUIStyle _headerStyle;
    static GUIStyle HeaderStyle
    {
        get
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.foldoutHeader)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize  = 12
                };
            }
            return _headerStyle;
        }
    }

    // store the editor reference so helpers can reach it
    MaterialEditor _editor;

    public override void OnGUI(MaterialEditor editor, MaterialProperty[] props)
    {
        _editor = editor;
        EditorGUILayout.Space(6);

        var waterLogo = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 16,
            alignment = TextAnchor.MiddleCenter,
        };
        EditorGUILayout.LabelField("🌊  URP Water Shader", waterLogo);
        EditorGUILayout.Space(4);

        DrawDivider();

        // ── Surface Color ─────────────────────────────────────────────
        _showSurface = SectionHeader("Surface Color", _showSurface);
        if (_showSurface)
        {
            DrawProp(props, "_ShallowColor", "Shallow Color");
            DrawProp(props, "_DeepColor",    "Deep Color");
            DrawProp(props, "_DepthMaxDistance", "Depth Fade Distance");
            DrawProp(props, "_Opacity",          "Overall Opacity");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Waves ─────────────────────────────────────────────────────
        _showWaves = SectionHeader("Gerstner Waves", _showWaves);
        if (_showWaves)
        {
            DrawProp(props, "_WaveSpeed",      "Speed");
            DrawProp(props, "_WaveAmplitude",  "Amplitude");
            DrawProp(props, "_WaveFrequency",  "Frequency");
            EditorGUILayout.Space(2);
            DrawProp(props, "_WaveDirection1", "Direction 1 (XY)");
            DrawProp(props, "_WaveDirection2", "Direction 2 (XY)");
            DrawProp(props, "_WaveDirection3", "Direction 3 (XY)");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Normal Map ────────────────────────────────────────────────
        _showNormal = SectionHeader("Normal Map", _showNormal);
        if (_showNormal)
        {
            DrawProp(props, "_NormalMap",         "Normal Map");
            DrawProp(props, "_NormalStrength",     "Strength");
            DrawProp(props, "_NormalTiling",       "Tiling");
            DrawProp(props, "_NormalScrollSpeed",  "Scroll Speed (UV1.xy / UV2.xy)");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Shore Foam ────────────────────────────────────────────────
        _showFoam = SectionHeader("Shore Foam", _showFoam);
        if (_showFoam)
        {
            DrawProp(props, "_FoamColor",      "Foam Color");
            DrawProp(props, "_FoamDistance",   "Intersection Distance");
            DrawProp(props, "_FoamFalloff",    "Edge Falloff");
            DrawProp(props, "_FoamIntensity",  "Intensity");
            EditorGUILayout.Space(2);
            DrawProp(props, "_FoamNoise",      "Foam Noise Texture");
            DrawProp(props, "_FoamNoiseScale", "Noise Scale");
            DrawProp(props, "_FoamNoiseSpeed", "Noise Speed");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Specular & Reflection ─────────────────────────────────────
        _showSpecular = SectionHeader("Specular & Reflection", _showSpecular);
        if (_showSpecular)
        {
            DrawProp(props, "_Smoothness",         "Smoothness");
            DrawProp(props, "_SpecularIntensity",  "Specular Intensity");
            DrawProp(props, "_ReflectionStrength", "Reflection Strength");
            DrawProp(props, "_FresnelPower",       "Fresnel Power");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Refraction ────────────────────────────────────────────────
        _showRefraction = SectionHeader("Refraction", _showRefraction);
        if (_showRefraction)
        {
            DrawProp(props, "_RefractionStrength", "Refraction Strength");
            EditorGUILayout.HelpBox(
                "Requires Opaque Texture (Camera Opaque Texture) enabled in URP Asset.",
                MessageType.Info);
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        // ── Caustics ─────────────────────────────────────────────────
        _showCaustics = SectionHeader("Caustics", _showCaustics);
        if (_showCaustics)
        {
            DrawProp(props, "_CausticsTex",      "Caustics Texture");
            DrawProp(props, "_CausticsStrength", "Strength");
            DrawProp(props, "_CausticsScale",    "Scale");
            DrawProp(props, "_CausticsSpeed",    "Animation Speed");
            EditorGUILayout.Space(4);
        }

        DrawDivider();

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Render Queue", EditorStyles.miniBoldLabel);
        editor.RenderQueueField();
        editor.EnableInstancingField();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────
    void DrawProp(MaterialProperty[] props, string name, string label)
    {
        var p = FindProperty(name, props, false);
        if (p != null)
            _editor.DefaultShaderProperty(p, label);  // 인스턴스 메서드로 호출
    }

    bool SectionHeader(string title, bool state)
    {
        EditorGUILayout.Space(2);
        return EditorGUILayout.BeginFoldoutHeaderGroup(state, "  " + title, HeaderStyle);
        // Note: EndFoldoutHeaderGroup called in parent context isn't needed in newer Unity
    }

    void DrawDivider()
    {
        EditorGUILayout.EndFoldoutHeaderGroup();
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.4f, 0.4f, 0.4f, 0.5f));
    }
}
#endif
