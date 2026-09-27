using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Controls the top-down orthographic camera, providing smooth frame-rate-independent
/// player tracking, optional cursor look-ahead, screen shake, and zoom punches.
/// </summary>
[RequireComponent(typeof(Camera))]
public class TopDownCamera : MonoBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("Tunable camera settings asset.")]
    [SerializeField] private CameraConfig _config;

    [Header("Tracking Target")]
    [Tooltip("The transform the camera follows (typically the Player).")]
    [SerializeField] private Transform _target;

    [Header("Input Actions")]
    [SerializeField] private InputAction _lookAction = new InputAction(
        "Look", InputActionType.Value, "<Mouse>/position");

    // ────────────────────────────── Cached Refs ────────────────────────────

    private Camera _camera;

    // ────────────────────────────── Runtime State ──────────────────────────

    private Vector3 _smoothedPosition;
    private float _currentShakeMagnitude;
    private Vector2 _directionalShakeOffset;
    private float _currentZoomOffset;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The transform currently being tracked by the camera.</summary>
    public Transform Target => _target;

    /// <summary>
    /// Assigns a new follow target for the camera and optionally snaps to it immediately.
    /// </summary>
    /// <param name="newTarget">Transform to follow.</param>
    /// <param name="snapImmediately">If true, teleports the camera directly to the target.</param>
    public void SetTarget(Transform newTarget, bool snapImmediately = false)
    {
        _target = newTarget;
        if (snapImmediately && _target != null)
        {
            float z = _config != null ? _config.ZOffset : transform.position.z;
            _smoothedPosition = new Vector3(_target.position.x, _target.position.y, z);
            transform.position = _smoothedPosition;
        }
    }

    /// <summary>
    /// Triggers an omnidirectional screen shake (e.g., for explosions or gunfire).
    /// </summary>
    /// <param name="intensity">Peak shake offset in world units.</param>
    public void Shake(float intensity)
    {
        float multiplier = _config != null ? _config.ShakeIntensityMultiplier : 1f;
        _currentShakeMagnitude = Mathf.Max(_currentShakeMagnitude, intensity * multiplier);
    }

    /// <summary>
    /// Triggers a directional camera jolt (e.g., when taking damage from a specific direction).
    /// </summary>
    /// <param name="direction">Normalized direction of the impact.</param>
    /// <param name="intensity">Strength of the directional jolt in world units.</param>
    public void DirectionalShake(Vector2 direction, float intensity)
    {
        float multiplier = _config != null ? _config.ShakeIntensityMultiplier : 1f;
        _directionalShakeOffset += direction.normalized * (intensity * multiplier);
    }

    /// <summary>
    /// Applies a momentary zoom punch to the orthographic size (negative values zoom in).
    /// </summary>
    /// <param name="orthoSizeDelta">Change in orthographic size (e.g., -0.5f for a zoom-in punch).</param>
    public void ZoomPunch(float orthoSizeDelta)
    {
        _currentZoomOffset += orthoSizeDelta;
    }

    // ──────────────────────────── Unity Callbacks ──────────────────────────

    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    private void Start()
    {
        if (_config == null)
        {
            Debug.LogError($"[{nameof(TopDownCamera)}] CameraConfig is not assigned!", this);
            enabled = false;
            return;
        }

        _camera.orthographic = true;
        _camera.orthographicSize = _config.OrthographicSize;

        if (_target != null)
        {
            _smoothedPosition = new Vector3(_target.position.x, _target.position.y, _config.ZOffset);
            transform.position = _smoothedPosition;
        }
        else
        {
            _smoothedPosition = transform.position;
        }
    }

    private void OnEnable()
    {
        _lookAction.Enable();
    }

    private void OnDisable()
    {
        _lookAction.Disable();
    }

    private void LateUpdate()
    {
        if (_config == null || _target == null) return;

        UpdateFollowPosition();
        UpdateShakeAndZoom();
    }

    // ──────────────────────────── Camera Logic ─────────────────────────────

    private void UpdateFollowPosition()
    {
        Vector3 desiredPosition = new Vector3(_target.position.x, _target.position.y, _config.ZOffset);

        // Optional look-ahead toward mouse cursor
        if (_config.LookAheadFactor > 0.001f && _camera != null)
        {
            Vector2 screenPos = _lookAction.ReadValue<Vector2>();
            Vector3 mouseWorld = _camera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 0f));
            Vector2 toMouse = (Vector2)mouseWorld - (Vector2)_target.position;
            Vector2 lookAheadOffset = Vector2.ClampMagnitude(
                toMouse * _config.LookAheadFactor,
                _config.MaxLookAheadDistance);

            desiredPosition.x += lookAheadOffset.x;
            desiredPosition.y += lookAheadOffset.y;
        }

        // Frame-rate-independent exponential decay smoothing
        float t = 1f - Mathf.Exp(-_config.FollowSmoothSpeed * Time.deltaTime);
        _smoothedPosition = Vector3.Lerp(_smoothedPosition, desiredPosition, t);
    }

    private void UpdateShakeAndZoom()
    {
        Vector2 totalShakeOffset = Vector2.zero;

        // Random omnidirectional shake
        if (_currentShakeMagnitude > 0.001f)
        {
            totalShakeOffset += Random.insideUnitCircle * _currentShakeMagnitude;
            float decayT = 1f - Mathf.Exp(-_config.ShakeDecaySpeed * Time.deltaTime);
            _currentShakeMagnitude = Mathf.Lerp(_currentShakeMagnitude, 0f, decayT);
        }
        else
        {
            _currentShakeMagnitude = 0f;
        }

        // Directional shake recovery
        if (_directionalShakeOffset.sqrMagnitude > 0.0001f)
        {
            totalShakeOffset += _directionalShakeOffset;
            float decayT = 1f - Mathf.Exp(-_config.ShakeDecaySpeed * Time.deltaTime);
            _directionalShakeOffset = Vector2.Lerp(_directionalShakeOffset, Vector2.zero, decayT);
        }
        else
        {
            _directionalShakeOffset = Vector2.zero;
        }

        // Apply final position (smoothed base + transient shake offset)
        transform.position = new Vector3(
            _smoothedPosition.x + totalShakeOffset.x,
            _smoothedPosition.y + totalShakeOffset.y,
            _config.ZOffset);

        // Zoom punch recovery
        if (Mathf.Abs(_currentZoomOffset) > 0.001f)
        {
            float zoomT = 1f - Mathf.Exp(-_config.ZoomRecoverySpeed * Time.deltaTime);
            _currentZoomOffset = Mathf.Lerp(_currentZoomOffset, 0f, zoomT);
            _camera.orthographicSize = Mathf.Max(1f, _config.OrthographicSize + _currentZoomOffset);
        }
        else
        {
            _currentZoomOffset = 0f;
            _camera.orthographicSize = _config.OrthographicSize;
        }
    }
}
