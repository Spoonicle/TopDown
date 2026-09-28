using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative dummy controller (stationary or patrolling).
/// Movement speed is directly scaled by <see cref="Health.WoundedSpeedMultiplier"/>
/// and synced to all connected clients.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Health))]
[RequireComponent(typeof(TeamMember))]
public class DummyController : NetworkBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("PlayerConfig asset defining base move speed and acceleration.")]
    [SerializeField] private PlayerConfig _config;

    [Header("Patrol Settings")]
    [Tooltip("If true, the dummy walks back and forth between Patrol Offset A and B.")]
    [SerializeField] private bool _enablePatrol = true;

    [Tooltip("First patrol waypoint offset relative to spawn position.")]
    [SerializeField] private Vector2 _patrolOffsetA = new Vector2(0f, -3.5f);

    [Tooltip("Second patrol waypoint offset relative to spawn position.")]
    [SerializeField] private Vector2 _patrolOffsetB = new Vector2(0f, 3.5f);

    [Tooltip("Pause duration in seconds when reaching a patrol endpoint.")]
    [Min(0f)]
    [SerializeField] private float _waypointPauseDuration = 0.25f;

    [Header("Combat Test Settings (Optional)")]
    [Tooltip("If true, the dummy aims at the target transform instead of its walk direction.")]
    [SerializeField] private bool _aimAtTarget = false;

    [Tooltip("Optional target transform (e.g., the Player) for aiming/firing tests.")]
    [SerializeField] private Transform _combatTarget;

    [Tooltip("If true and a WeaponHolder is attached, periodically fires at Combat Target.")]
    [SerializeField] private bool _fireAtTarget = false;

    [Tooltip("Seconds between test shots when Fire At Target is enabled.")]
    [Min(0.2f)]
    [SerializeField] private float _fireInterval = 1.5f;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<Vector2> _netPosition = new NetworkVariable<Vector2>(
        Vector2.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> _netAimAngle = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ────────────────────────────── Cached Refs ────────────────────────────

    private Rigidbody2D _rb;
    private Health _health;
    private WeaponHolder _weaponHolder;

    // ────────────────────────────── Runtime State ──────────────────────────

    private Vector2 _spawnPosition;
    private bool _movingToB = true;
    private float _pauseEndTime;
    private Vector2 _currentVelocity;
    private float _nextFireTime;

    private bool HasServerAuthority => !IsSpawned || IsServer;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _health = GetComponent<Health>();
        _weaponHolder = GetComponent<WeaponHolder>();
    }

    private void Start()
    {
        if (_config == null && _health != null)
        {
            _config = _health.Config;
        }

        if (_spawnPosition == Vector2.zero)
        {
            _spawnPosition = transform.position;
        }
        _nextFireTime = Time.time + _fireInterval;
    }

    /// <summary>
    /// Updates the dummy's spawn/patrol origin and patrol offsets when repositioned by map generation.
    /// </summary>
    public void SetSpawnOrigin(Vector2 worldPos, Vector2 offsetA, Vector2 offsetB)
    {
        _spawnPosition = worldPos;
        _patrolOffsetA = offsetA;
        _patrolOffsetB = offsetB;
        transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
        if (_rb != null)
        {
            _rb.position = worldPos;
            _rb.linearVelocity = Vector2.zero;
        }
        if (IsSpawned && IsServer)
        {
            _netPosition.Value = worldPos;
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            _netPosition.Value = _rb.position;
            _netAimAngle.Value = transform.eulerAngles.z;
        }
        else
        {
            _rb.position = _netPosition.Value;
            transform.rotation = Quaternion.Euler(0f, 0f, _netAimAngle.Value);
        }
    }

    private void Update()
    {
        if (_health != null && _health.IsDead) return;

        if (HasServerAuthority)
        {
            HandleRotation();
            HandleTestFiring();
        }
        else
        {
            float smoothedAngle = Mathf.LerpAngle(transform.eulerAngles.z, _netAimAngle.Value, 20f * Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, 0f, smoothedAngle);
        }
    }

    private void FixedUpdate()
    {
        if (_health != null && _health.IsDead)
        {
            _currentVelocity = Vector2.zero;
            return;
        }

        if (HasServerAuthority)
        {
            HandlePatrolMovement();
        }
        else
        {
            Vector2 smoothedPos = Vector2.Lerp(_rb.position, _netPosition.Value, 20f * Time.fixedDeltaTime);
            _rb.MovePosition(smoothedPos);
        }
    }

    // ──────────────────────────── Movement ─────────────────────────────────

    private void HandlePatrolMovement()
    {
        if (!_enablePatrol)
        {
            _currentVelocity = Vector2.zero;
            return;
        }

        if (Time.time < _pauseEndTime)
        {
            _currentVelocity = Vector2.zero;
            return;
        }

        Vector2 targetPoint = _spawnPosition + (_movingToB ? _patrolOffsetB : _patrolOffsetA);
        Vector2 toTarget = targetPoint - _rb.position;
        float distance = toTarget.magnitude;

        if (distance < 0.1f)
        {
            _movingToB = !_movingToB;
            _pauseEndTime = Time.time + _waypointPauseDuration;
            _currentVelocity = Vector2.zero;
            return;
        }

        float baseSpeed = _config != null ? _config.MoveSpeed : 5f;
        float woundedMultiplier = _health != null ? _health.WoundedSpeedMultiplier : 1f;
        float effectiveSpeed = baseSpeed * woundedMultiplier;

        Vector2 desiredVelocity = (toTarget / distance) * effectiveSpeed;
        float accel = _config != null ? _config.Acceleration : 50f;

        _currentVelocity = Vector2.Lerp(_currentVelocity, desiredVelocity, accel * Time.fixedDeltaTime);
        Vector2 newPos = _rb.position + _currentVelocity * Time.fixedDeltaTime;
        _rb.MovePosition(newPos);

        if (IsSpawned && IsServer)
        {
            _netPosition.Value = newPos;
        }
    }

    // ──────────────────────────── Rotation & Firing ────────────────────────

    private void HandleRotation()
    {
        Vector2 lookDir = Vector2.zero;

        if (_aimAtTarget && _combatTarget != null)
        {
            lookDir = (Vector2)_combatTarget.position - (Vector2)transform.position;
        }
        else if (_currentVelocity.sqrMagnitude > 0.01f)
        {
            lookDir = _currentVelocity.normalized;
        }

        if (lookDir.sqrMagnitude > 0.001f)
        {
            float angle = Mathf.Atan2(lookDir.y, lookDir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);

            if (IsSpawned && IsServer)
            {
                _netAimAngle.Value = angle;
            }
        }
    }

    private void HandleTestFiring()
    {
        if (!_fireAtTarget || _weaponHolder == null || _combatTarget == null) return;

        var targetHealth = _combatTarget.GetComponent<Health>();
        if (targetHealth != null && targetHealth.IsDead) return;

        if (Time.time >= _nextFireTime)
        {
            _nextFireTime = Time.time + _fireInterval;
            _weaponHolder.TryFireActiveWeapon();
        }
    }
}
