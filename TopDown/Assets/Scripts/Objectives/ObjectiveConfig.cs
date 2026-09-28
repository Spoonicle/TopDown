using UnityEngine;

/// <summary>
/// ScriptableObject holding tunable parameters for the computer terminal hacking
/// and extraction objective system.
/// Create via Assets → Create → TopDown → Objective Config.
/// </summary>
[CreateAssetMenu(fileName = "ObjectiveConfig", menuName = "TopDown/Objective Config")]
public class ObjectiveConfig : ScriptableObject
{
    // ───────────────────────────── Terminal Hacking ─────────────────────────

    [Header("Terminal Interaction")]
    [Tooltip("Maximum distance in world units from the terminal to start and maintain hacking.")]
    [Min(0.5f)]
    [SerializeField] private float _interactionRadius = 2.2f;

    [Header("Speed-Typing Hack Mechanic")]
    [Tooltip("Fraction of the progress bar (0–1) added per random keystroke typed (0.016 = ~62 keystrokes for 100%).")]
    [Range(0.002f, 0.1f)]
    [SerializeField] private float _progressPerKeystroke = 0.016f;

    [Tooltip("Maximum number of distinct key presses counted in a single frame.")]
    [Min(1)]
    [SerializeField] private int _maxKeystrokesPerFrame = 4;

    [Tooltip("How much progress (0–1) decays per second when no one is actively hacking (0 = progress never decays).")]
    [Range(0f, 0.25f)]
    [SerializeField] private float _progressDecayPerSecond = 0.0f;

    // ───────────────────────────── Extraction ───────────────────────────────

    [Header("Extraction Zone")]
    [Tooltip("Radius in world units of each team's spawn/extraction zone.")]
    [Min(1f)]
    [SerializeField] private float _extractionRadius = 2.5f;

    // ───────────────────────────── Visuals ──────────────────────────────────

    [Header("Visual Colors")]
    [Tooltip("Screen glow color when the terminal has data ready to be hacked.")]
    [SerializeField] private Color _terminalIdleColor = new Color(0.95f, 0.65f, 0.15f, 1f);

    [Tooltip("Screen glow color while a player is actively speed-typing on the terminal.")]
    [SerializeField] private Color _terminalHackingColor = new Color(0.20f, 0.95f, 0.55f, 1f);

    [Tooltip("Screen color once the data has been downloaded from the terminal.")]
    [SerializeField] private Color _terminalCompletedColor = new Color(0.25f, 0.30f, 0.35f, 1f);

    // ───────────────────────────── Public Accessors ─────────────────────────

    /// <summary>Maximum distance from the terminal to start and maintain hacking.</summary>
    public float InteractionRadius => _interactionRadius;

    /// <summary>Progress (0–1) added per keystroke typed during hacking.</summary>
    public float ProgressPerKeystroke => _progressPerKeystroke;

    /// <summary>Maximum distinct keystrokes counted in a single frame.</summary>
    public int MaxKeystrokesPerFrame => _maxKeystrokesPerFrame;

    /// <summary>Progress decay per second when no one is hacking.</summary>
    public float ProgressDecayPerSecond => _progressDecayPerSecond;

    /// <summary>Radius of the team extraction zone.</summary>
    public float ExtractionRadius => _extractionRadius;

    /// <summary>Screen color when idle and ready to hack.</summary>
    public Color TerminalIdleColor => _terminalIdleColor;

    /// <summary>Screen color while actively being hacked.</summary>
    public Color TerminalHackingColor => _terminalHackingColor;

    /// <summary>Screen color after data download is complete.</summary>
    public Color TerminalCompletedColor => _terminalCompletedColor;
}
