using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Networked top-down 2D player controller handling owner-authoritative movement,
/// aiming, and sprint, while smoothly interpolating remote players across clients.
/// Uses <see cref="Health"/> for hit-points and wounded movement slowdown.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Health))]
[RequireComponent(typeof(TeamMember))]
public class PlayerController : NetworkBehaviour
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

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<Vector2> _netPosition = new NetworkVariable<Vector2>(
        Vector2.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    private readonly NetworkVariable<float> _netAimAngle = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired whenever health changes. Passes current health.</summary>
    public event Action<float> OnHealthChanged;

    /// <summary>Fired once when the player dies (health reaches 0).</summary>
    public event Action OnDeath;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>True if this client has authority to control this player (Owner when networked, or local when offline).</summary>
    public bool HasInputAuthority => !IsSpawned || IsOwner;

    /// <summary>The Health component attached to this player.</summary>
    public Health PlayerHealth => _health;

    /// <summary>Current health of the player.</summary>
    public float CurrentHealth => _health != null ? _health.CurrentHealth : 0f;

    /// <summary>Current effective movement speed multiplier from wounds (0–1).</summary>
    public float WoundedSpeedMultiplier => _health != null ? _health.WoundedSpeedMultiplier : 1f;

    /// <summary>True while the sprint action is held by the owner.</summary>
    public bool IsSprinting => HasInputAuthority && _sprintAction.IsPressed();

    /// <summary>Set by the objective system when the player picks up / drops an objective.</summary>
    public bool IsCarryingObjective { get; set; }

    /// <summary>True while the player is actively typing at a computer terminal (locks WASD movement).</summary>
    public bool IsHacking { get; set; }

    /// <summary>True when health has reached zero.</summary>
    public bool IsDead => _health != null && _health.IsDead;

    /// <summary>Current raw movement input direction (for animation / other systems).</summary>
    public Vector2 MoveDirection { get; private set; }

    // ────────────────────────────── Cached refs ────────────────────────────

    private Rigidbody2D _rb;
    private Health _health;
    private Camera _mainCamera;
    private TopDownCamera _topDownCamera;

    // ────────────────────────────── Runtime state ─────────────────────────

    private Vector2 _currentVelocity;

    // ──────────────────────────── Unity & Netcode Callbacks ───────────────

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _health = GetComponent<Health>();
        _mainCamera = Camera.main;
        if (_mainCamera != null)
        {
            _topDownCamera = _mainCamera.GetComponent<TopDownCamera>();
        }

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
        if (_config == null && _health != null)
        {
            _config = _health.Config;
        }

        if (_config == null)
        {
            Debug.LogError($"[{nameof(PlayerController)}] PlayerConfig is not assigned!", this);
            enabled = false;
            return;
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsOwner)
        {
            EnableInputActions();
            _netPosition.Value = _rb.position;
            _netAimAngle.Value = transform.eulerAngles.z;

            // Bind the local camera to follow our owned player
            if (_topDownCamera == null && Camera.main != null)
            {
                _topDownCamera = Camera.main.GetComponent<TopDownCamera>();
            }
            if (_topDownCamera != null)
            {
                _topDownCamera.SetTarget(transform, snapImmediately: true);
            }
        }
        else
        {
            DisableInputActions();
            _rb.position = _netPosition.Value;
            transform.rotation = Quaternion.Euler(0f, 0f, _netAimAngle.Value);
        }
    }

    private void OnEnable()
    {
        if (HasInputAuthority)
        {
            EnableInputActions();
        }

        if (_health != null)
        {
            _health.OnHealthChanged += HandleHealthChanged;
            _health.OnDamaged += HandleDamaged;
            _health.OnDeath += HandleDeath;
        }
    }

    private void OnDisable()
    {
        if (_health != null)
        {
            _health.OnHealthChanged -= HandleHealthChanged;
            _health.OnDamaged -= HandleDamaged;
            _health.OnDeath -= HandleDeath;
        }

        DisableInputActions();
    }

    private void EnableInputActions()
    {
        _moveAction.Enable();
        _sprintAction.Enable();
        _lookAction.Enable();
    }

    private void DisableInputActions()
    {
        _moveAction.Disable();
        _sprintAction.Disable();
        _lookAction.Disable();
    }

    private void Update()
    {
        if (IsDead) return;

        if (HasInputAuthority)
        {
            HandleAimRotation();
        }
        else
        {
            // Smoothly interpolate remote player aim rotation
            float currentAngle = transform.eulerAngles.z;
            float smoothedAngle = Mathf.LerpAngle(currentAngle, _netAimAngle.Value, 20f * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, 0f, smoothedAngle);
        }
    }

    private void FixedUpdate()
    {
        if (IsDead) return;

        if (HasInputAuthority)
        {
            HandleMovement();
        }
        else
        {
            // Smoothly interpolate remote player position
            Vector2 smoothedPos = Vector2.Lerp(_rb.position, _netPosition.Value, 20f * Time.fixedDeltaTime);
            _rb.MovePosition(smoothedPos);
        }
    }

    // ────────────────────────────── Movement ──────────────────────────────

    private void HandleMovement()
    {
        MoveDirection = IsHacking ? Vector2.zero : _moveAction.ReadValue<Vector2>();

        float sprintMultiplier = IsSprinting ? _config.SprintMultiplier : 1f;
        float woundedMultiplier = _health != null ? _health.WoundedSpeedMultiplier : 1f;
        float objectiveCarryMultiplier = IsCarryingObjective ? _config.ObjectiveCarrySpeedMultiplier : 1f;
        float maxSpeed = _config.MoveSpeed * sprintMultiplier * woundedMultiplier * objectiveCarryMultiplier;

        Vector2 targetVelocity = MoveDirection.normalized * maxSpeed;

        float smoothRate = (targetVelocity.sqrMagnitude > 0.001f)
            ? _config.Acceleration
            : _config.Deceleration;

        _currentVelocity = Vector2.Lerp(
            _currentVelocity,
            targetVelocity,
            smoothRate * Time.fixedDeltaTime);

        Vector2 newPosition = _rb.position + _currentVelocity * Time.fixedDeltaTime;
        _rb.MovePosition(newPosition);

        if (IsSpawned && IsOwner)
        {
            _netPosition.Value = newPosition;
        }
    }

    /// <summary>
    /// Immediately teleports the player to the given world position without physics interpolation snap-back.
    /// </summary>
    public void TeleportTo(Vector2 worldPos)
    {
        _currentVelocity = Vector2.zero;
        transform.position = new Vector3(worldPos.x, worldPos.y, 0f);

        if (_rb != null)
        {
            var prevInterp = _rb.interpolation;
            _rb.interpolation = RigidbodyInterpolation2D.None;
            _rb.position = worldPos;
            _rb.linearVelocity = Vector2.zero;
            Physics2D.SyncTransforms();
            _rb.interpolation = prevInterp;
        }

        if (IsSpawned && IsOwner)
        {
            _netPosition.Value = worldPos;
        }
    }

    // ──────────────────────────── Aim / Rotation ──────────────────────────

    private void HandleAimRotation()
    {
        if (_mainCamera == null) _mainCamera = Camera.main;
        if (_mainCamera == null) return;

        Vector2 screenPos = _lookAction.ReadValue<Vector2>();
        Vector3 worldPos = _mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 0f));

        Vector2 direction = (Vector2)worldPos - (Vector2)transform.position;

        if (direction.sqrMagnitude < 0.001f) return;

        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, angle);

        if (IsSpawned && IsOwner)
        {
            _netAimAngle.Value = angle;
        }
    }

    // ──────────────────────────── Health Forwarding ───────────────────────

    /// <summary>
    /// Applies damage to the player's <see cref="Health"/> component.
    /// </summary>
    /// <param name="amount">Raw damage to apply (positive value).</param>
    /// <param name="attacker">Optional attacker team member for friendly-fire validation.</param>
    public void TakeDamage(float amount, TeamMember attacker = null)
    {
        if (_health != null)
        {
            _health.TryTakeDamage(amount, attacker, transform.position, Vector2.zero);
        }
    }

    private void HandleHealthChanged(float current, float max)
    {
        OnHealthChanged?.Invoke(current);
    }

    private void HandleDamaged(DamageInfo info)
    {
        // Only shake the local camera if this is our owned player taking damage
        if (!HasInputAuthority || _topDownCamera == null) return;

        if (info.HitDirection.sqrMagnitude > 0.001f)
        {
            _topDownCamera.DirectionalShake(info.HitDirection, 0.22f);
        }
        else
        {
            _topDownCamera.Shake(0.18f);
        }
    }

    private void HandleDeath()
    {
        _currentVelocity = Vector2.zero;
        if (HasInputAuthority && _topDownCamera != null)
        {
            _topDownCamera.Shake(0.35f);
        }
        OnDeath?.Invoke();
    }
}
