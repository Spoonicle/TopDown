using UnityEngine;

/// <summary>
/// Centralised tunable values for the player character.
/// Create an instance via Assets → Create → TopDown → Player Config.
/// </summary>
[CreateAssetMenu(fileName = "PlayerConfig", menuName = "TopDown/Player Config")]
public class PlayerConfig : ScriptableObject
{
    // ───────────────────────────── Movement ─────────────────────────────

    [Header("Movement")]

    [Tooltip("Base movement speed in units per second.")]
    [SerializeField] private float _moveSpeed = 5f;

    [Tooltip("Multiplier applied to move speed while sprinting.")]
    [SerializeField] private float _sprintMultiplier = 1.6f;

    [Tooltip("How quickly the player reaches the target speed (units/s²).")]
    [SerializeField] private float _acceleration = 50f;

    [Tooltip("How quickly the player comes to a stop (units/s²).")]
    [SerializeField] private float _deceleration = 50f;

    // ───────────────────────── Health & Wounded ─────────────────────────

    [Header("Health & Wounded")]

    [Tooltip("Maximum hit-points the player can have.")]
    [SerializeField] private float _maxHealth = 100f;

    [Tooltip("Maps health % (X: 0–1) to speed multiplier (Y: 0–1). Default: linear 1.0 → 0.3.")]
    [SerializeField] private AnimationCurve _woundedSpeedCurve = new AnimationCurve(
        new Keyframe(0f, 0.3f),
        new Keyframe(1f, 1.0f)
    );

    [Tooltip("Health % below which movement is significantly reduced.")]
    [Range(0f, 1f)]
    [SerializeField] private float _criticalHealthThreshold = 0.25f;

    // ──────────────────────── Blood Trails ──────────────────────────────

    [Header("Blood Trails")]

    [Tooltip("Health % below which blood trails begin spawning.")]
    [Range(0f, 1f)]
    [SerializeField] private float _bloodTrailHealthThreshold = 0.75f;

    [Tooltip("Seconds between blood decal spawns.")]
    [Min(0.01f)]
    [SerializeField] private float _bloodTrailInterval = 0.3f;

    [Tooltip("Blood decal opacity at high health (least intense).")]
    [Range(0f, 1f)]
    [SerializeField] private float _bloodTrailIntensityMin = 0.3f;

    [Tooltip("Blood decal opacity at critical health (most intense).")]
    [Range(0f, 1f)]
    [SerializeField] private float _bloodTrailIntensityMax = 1.0f;

    // ───────────────────── Objective Carrying ───────────────────────────

    [Header("Objective Carrying")]

    [Tooltip("Speed multiplier while carrying an objective.")]
    [Range(0f, 1f)]
    [SerializeField] private float _objectiveCarrySpeedMultiplier = 0.7f;

    // ───────────────────── Public Accessors ─────────────────────────────

    /// <summary>Base movement speed in units per second.</summary>
    public float MoveSpeed => _moveSpeed;

    /// <summary>Multiplier applied to move speed while sprinting.</summary>
    public float SprintMultiplier => _sprintMultiplier;

    /// <summary>How quickly the player reaches the target speed.</summary>
    public float Acceleration => _acceleration;

    /// <summary>How quickly the player comes to a stop.</summary>
    public float Deceleration => _deceleration;

    /// <summary>Maximum hit-points the player can have.</summary>
    public float MaxHealth => _maxHealth;

    /// <summary>The raw wounded speed AnimationCurve.</summary>
    public AnimationCurve WoundedSpeedCurve => _woundedSpeedCurve;

    /// <summary>Health percentage below which the player is critically wounded.</summary>
    public float CriticalHealthThreshold => _criticalHealthThreshold;

    /// <summary>Health percentage below which blood trails begin.</summary>
    public float BloodTrailHealthThreshold => _bloodTrailHealthThreshold;

    /// <summary>Seconds between blood decal spawns.</summary>
    public float BloodTrailInterval => _bloodTrailInterval;

    /// <summary>Blood trail opacity at high health.</summary>
    public float BloodTrailIntensityMin => _bloodTrailIntensityMin;

    /// <summary>Blood trail opacity at critical health.</summary>
    public float BloodTrailIntensityMax => _bloodTrailIntensityMax;

    /// <summary>Speed multiplier while carrying an objective.</summary>
    public float ObjectiveCarrySpeedMultiplier => _objectiveCarrySpeedMultiplier;

    // ───────────────────── Helper Methods ───────────────────────────────

    /// <summary>
    /// Evaluates the wounded speed curve at the given normalised health (0–1).
    /// Returns a multiplier to apply to movement speed.
    /// </summary>
    public float GetWoundedSpeedMultiplier(float healthPercent)
    {
        return _woundedSpeedCurve.Evaluate(Mathf.Clamp01(healthPercent));
    }

    /// <summary>
    /// Calculates blood trail intensity based on current health percentage.
    /// Returns 0 if above the blood trail threshold.
    /// </summary>
    public float GetBloodTrailIntensity(float healthPercent)
    {
        if (healthPercent >= _bloodTrailHealthThreshold)
            return 0f;

        float t = 1f - (healthPercent / _bloodTrailHealthThreshold);
        return Mathf.Lerp(_bloodTrailIntensityMin, _bloodTrailIntensityMax, t);
    }
}
