using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Top-down 2D player controller handling movement, aiming, health, and sprint.
/// Reads input via the New Input System (InputAction fields) and moves via Rigidbody2D.
/// All tunable values come from a referenced <see cref="PlayerConfig"/> ScriptableObject.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class PlayerController : MonoBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [SerializeField] private PlayerConfig _config;

    [Header("Input Actions")]
    [SerializeField] private InputAction _moveAction = new InputAction(
        "Move", InputActionType.Value, null, null, null, "2DVector");

    [SerializeField] private InputAction _sprintAction = new InputAction(
        "Sprint", InputActionType.Button, "<Keyboard>/leftShift");

    [SerializeField] private InputAction _lookAction = new InputAction(
        "Look", InputActionType.Value, "<Mouse>/position");

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired whenever health changes. Passes current health.</summary>
    public event Action<float> OnHealthChanged;

    /// <summary>Fired once when the player dies (health reaches 0).</summary>
    public event Action OnDeath;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Current health of the player.</summary>
    public float CurrentHealth { get; private set; }

    /// <summary>True while the sprint action is held.</summary>
    public bool IsSprinting => _sprintAction.IsPressed();

    /// <summary>Set by the objective system when the player picks up / drops an objective.</summary>
    public bool IsCarryingObjective { get; set; }

    /// <summary>True when health has reached zero.</summary>
    public bool IsDead => CurrentHealth <= 0f;

    /// <summary>Current raw movement input direction (for animation / other systems).</summary>
    public Vector2 MoveDirection { get; private set; }

    // ────────────────────────────── Cached refs ────────────────────────────

    private Rigidbody2D _rb;
    private Camera _mainCamera;

    // ────────────────────────────── Runtime state ─────────────────────────

    private Vector2 _currentVelocity;
    private float _woundedMultiplier = 1f;

    // ──────────────────────────── Unity Callbacks ─────────────────────────

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _mainCamera = Camera.main;

        // Configure default WASD composite bindings if none are serialized yet.
        if (_moveAction.bindings.Count == 0)
        {
            _moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
        }
    }

    private void Start()
    {
        if (_config == null)
        {
            Debug.LogError($"[{nameof(PlayerController)}] PlayerConfig is not assigned!", this);
            enabled = false;
            return;
        }

        CurrentHealth = _config.MaxHealth;
    }

    private void OnEnable()
    {
        _moveAction.Enable();
        _sprintAction.Enable();
        _lookAction.Enable();
    }

    private void OnDisable()
    {
        _moveAction.Disable();
        _sprintAction.Disable();
        _lookAction.Disable();
    }

    private void Update()
    {
        if (IsDead) return;

        HandleAimRotation();
    }

    private void FixedUpdate()
    {
        if (IsDead) return;

        HandleMovement();
    }

    // ────────────────────────────── Movement ──────────────────────────────

    private void HandleMovement()
    {
        // Read input and store for external systems.
        MoveDirection = _moveAction.ReadValue<Vector2>();

        // Calculate effective speed.
        float sprintMultiplier = IsSprinting ? _config.SprintMultiplier : 1f;
        float objectiveCarryMultiplier = IsCarryingObjective ? _config.ObjectiveCarrySpeedMultiplier : 1f;
        float maxSpeed = _config.MoveSpeed * sprintMultiplier * _woundedMultiplier * objectiveCarryMultiplier;

        // Target velocity based on input.
        Vector2 targetVelocity = MoveDirection.normalized * maxSpeed;

        // Smooth acceleration / deceleration.
        float smoothRate = (targetVelocity.sqrMagnitude > 0.001f)
            ? _config.Acceleration
            : _config.Deceleration;

        _currentVelocity = Vector2.Lerp(
            _currentVelocity,
            targetVelocity,
            smoothRate * Time.fixedDeltaTime);

        // Apply movement through physics.
        Vector2 newPosition = _rb.position + _currentVelocity * Time.fixedDeltaTime;
        _rb.MovePosition(newPosition);
    }

    // ──────────────────────────── Aim / Rotation ──────────────────────────

    private void HandleAimRotation()
    {
        if (_mainCamera == null) return;

        Vector2 screenPos = _lookAction.ReadValue<Vector2>();
        Vector3 worldPos = _mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 0f));

        Vector2 direction = (Vector2)worldPos - (Vector2)transform.position;

        if (direction.sqrMagnitude < 0.001f) return;

        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    // ──────────────────────────── Health System ───────────────────────────

    /// <summary>
    /// Reduces health by <paramref name="amount"/>, clamped to 0.
    /// Updates the wounded speed multiplier via the config curve and fires events.
    /// </summary>
    /// <param name="amount">Raw damage to apply (positive value).</param>
    public void TakeDamage(float amount)
    {
        if (IsDead) return;

        CurrentHealth = Mathf.Max(CurrentHealth - amount, 0f);

        // Evaluate wounded multiplier from config AnimationCurve (normalised health 0\u20131).
        if (_config.WoundedSpeedCurve != null)
        {
            float healthNormalized = CurrentHealth / _config.MaxHealth;
            _woundedMultiplier = _config.WoundedSpeedCurve.Evaluate(healthNormalized);
        }

        OnHealthChanged?.Invoke(CurrentHealth);

        if (IsDead)
        {
            HandleDeath();
        }
    }

    private void HandleDeath()
    {
        OnDeath?.Invoke();

        // Zero out any remaining velocity.
        _currentVelocity = Vector2.zero;
    }
}
