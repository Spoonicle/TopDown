using UnityEngine;

/// <summary>
/// ScriptableObject holding tunable parameters for the top-down camera.
/// Create an instance via Assets → Create → TopDown → Camera Config.
/// </summary>
[CreateAssetMenu(fileName = "CameraConfig", menuName = "TopDown/Camera Config")]
public class CameraConfig : ScriptableObject
{
    // ───────────────────────────── Follow ─────────────────────────────

    [Header("Follow")]

    [Tooltip("How quickly the camera catches up to the player. Higher values mean tighter follow.")]
    [Min(0.1f)]
    [SerializeField] private float _followSmoothSpeed = 10f;

    [Tooltip("Fixed Z position of the camera in world space.")]
    [SerializeField] private float _zOffset = -10f;

    [Tooltip("Default orthographic size (zoom level) of the camera.")]
    [Min(1f)]
    [SerializeField] private float _orthographicSize = 8f;

    // ────────────────────────── Aim Look-Ahead ────────────────────────

    [Header("Aim Look-Ahead (Optional)")]

    [Tooltip("How much the camera shifts toward the mouse cursor (0 = strictly centered on player).")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _lookAheadFactor = 0f;

    [Tooltip("Maximum distance in world units the camera can shift toward the cursor.")]
    [Min(0f)]
    [SerializeField] private float _maxLookAheadDistance = 2.5f;

    // ─────────────────────── Screen Shake & Zoom ──────────────────────

    [Header("Screen Shake & Effects")]

    [Tooltip("Global multiplier for all screen shake intensities (useful for accessibility slider).")]
    [Range(0f, 2f)]
    [SerializeField] private float _shakeIntensityMultiplier = 1f;

    [Tooltip("How quickly screen shake decays back to zero.")]
    [Min(1f)]
    [SerializeField] private float _shakeDecaySpeed = 12f;

    [Tooltip("How quickly zoom punches recover back to the default orthographic size.")]
    [Min(1f)]
    [SerializeField] private float _zoomRecoverySpeed = 8f;

    // ───────────────────────── Public Accessors ───────────────────────

    /// <summary>How quickly the camera catches up to the player.</summary>
    public float FollowSmoothSpeed => _followSmoothSpeed;

    /// <summary>Fixed Z position of the camera in world space.</summary>
    public float ZOffset => _zOffset;

    /// <summary>Default orthographic size (zoom level) of the camera.</summary>
    public float OrthographicSize => _orthographicSize;

    /// <summary>How much the camera shifts toward the mouse cursor (0 = strictly centered).</summary>
    public float LookAheadFactor => _lookAheadFactor;

    /// <summary>Maximum world-unit offset toward the cursor when look-ahead is active.</summary>
    public float MaxLookAheadDistance => _maxLookAheadDistance;

    /// <summary>Global multiplier for screen shake intensity.</summary>
    public float ShakeIntensityMultiplier => _shakeIntensityMultiplier;

    /// <summary>Rate at which screen shake decays.</summary>
    public float ShakeDecaySpeed => _shakeDecaySpeed;

    /// <summary>Rate at which zoom punches recover to default size.</summary>
    public float ZoomRecoverySpeed => _zoomRecoverySpeed;
}
