using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Information about a single instance of damage dealt to a <see cref="Health"/> component.
/// </summary>
public struct DamageInfo
{
    /// <summary>Amount of health lost.</summary>
    public float Amount;

    /// <summary>The team member that dealt the damage (may be null for environmental damage).</summary>
    public TeamMember Attacker;

    /// <summary>World-space point where the hit occurred.</summary>
    public Vector2 HitPoint;

    /// <summary>Normalized direction the attack traveled.</summary>
    public Vector2 HitDirection;
}

/// <summary>
/// Server-authoritative networked health component managing hit-points, friendly-fire validation,
/// wounded movement slowdown, damage flash feedback, blood trails, and a world-space health bar.
/// </summary>
public class Health : NetworkBehaviour
{
    // ────────────────────────────── Constants ──────────────────────────────

    private static readonly Color BloodCrimsonColor = new Color(0.545f, 0f, 0f, 1f); // #8b0000 from GDD
    private static readonly Color DamageFlashColor = new Color(0.95f, 0.2f, 0.2f, 1f);
    private static readonly Color DeadTintMultiplier = new Color(0.35f, 0.35f, 0.35f, 0.7f);

    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("PlayerConfig asset defining MaxHealth, WoundedSpeedCurve, and blood trail settings.")]
    [SerializeField] private PlayerConfig _config;

    [Header("Visual Feedback")]
    [Tooltip("Duration in seconds of the red sprite flash when taking damage.")]
    [SerializeField] private float _damageFlashDuration = 0.1f;

    [Tooltip("Whether to display a small world-space health bar above this character.")]
    [SerializeField] private bool _showWorldHealthBar = true;

    [Tooltip("World-space vertical offset of the health bar above the character.")]
    [SerializeField] private Vector2 _healthBarOffset = new Vector2(0f, 0.8f);

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<float> _netCurrentHealth = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private float _localFallbackHealth = 100f;

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired whenever health changes on server or client. Passes (currentHealth, maxHealth).</summary>
    public event Action<float, float> OnHealthChanged;

    /// <summary>Fired when this character takes damage.</summary>
    public event Action<DamageInfo> OnDamaged;

    /// <summary>Fired once when health reaches zero.</summary>
    public event Action OnDeath;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Maximum health from config (defaults to 100 if no config assigned).</summary>
    public float MaxHealth => _config != null ? _config.MaxHealth : 100f;

    /// <summary>Current hit-points remaining (synced across the network when spawned).</summary>
    public float CurrentHealth => IsSpawned ? _netCurrentHealth.Value : _localFallbackHealth;

    /// <summary>Current health normalised to the 0–1 range.</summary>
    public float HealthNormalized => MaxHealth > 0f ? Mathf.Clamp01(CurrentHealth / MaxHealth) : 0f;

    /// <summary>
    /// Movement speed multiplier (0–1) evaluated from the wounded speed curve.
    /// Automatically updated on all clients as health changes.
    /// </summary>
    public float WoundedSpeedMultiplier { get; private set; } = 1f;

    /// <summary>True when health has reached zero.</summary>
    public bool IsDead => CurrentHealth <= 0f;

    /// <summary>The PlayerConfig asset driving this health component.</summary>
    public PlayerConfig Config => _config;

    // ────────────────────────────── Cached Refs ────────────────────────────

    private TeamMember _teamMember;
    private SpriteRenderer _spriteRenderer;
    private Collider2D _collider;
    private Rigidbody2D _rb;

    // ────────────────────────────── Runtime State ──────────────────────────

    private Color _baseColor = Color.white;
    private bool _isFlashing;
    private float _flashEndTime;
    private Vector2 _lastBloodSpawnPos;
    private float _nextBloodTrailTime;

    // World-space health bar transforms
    private Transform _healthBarRoot;
    private Transform _healthBarFill;
    private SpriteRenderer _healthBarFillRenderer;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        _teamMember = GetComponent<TeamMember>();
        _spriteRenderer = GetComponent<SpriteRenderer>();
        _collider = GetComponent<Collider2D>();
        _rb = GetComponent<Rigidbody2D>();

        _localFallbackHealth = MaxHealth;
        WoundedSpeedMultiplier = 1f;
    }

    private void Start()
    {
        if (!IsSpawned)
        {
            _localFallbackHealth = MaxHealth;
            RecalculateWoundedMultiplier();
        }

        if (_spriteRenderer != null)
        {
            _baseColor = _teamMember != null ? _teamMember.TeamColor : _spriteRenderer.color;
            _spriteRenderer.color = _baseColor;
        }

        _lastBloodSpawnPos = transform.position;

        if (_showWorldHealthBar)
        {
            SetupWorldHealthBar();
            UpdateWorldHealthBar();
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _netCurrentHealth.OnValueChanged += OnNetHealthChanged;

        if (IsServer)
        {
            _netCurrentHealth.Value = MaxHealth;
        }

        RecalculateWoundedMultiplier();
        UpdateWorldHealthBar();
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netCurrentHealth.OnValueChanged -= OnNetHealthChanged;
        base.OnNetworkDespawn();
    }

    private void Update()
    {
        // Recover from damage flash
        if (_isFlashing && Time.time >= _flashEndTime)
        {
            _isFlashing = false;
            if (_spriteRenderer != null && !IsDead)
            {
                _spriteRenderer.color = _teamMember != null ? _teamMember.TeamColor : _baseColor;
            }
        }

        // Spawn blood trail decals when moving while wounded
        if (!IsDead)
        {
            UpdateBloodTrail();
        }
    }

    private void LateUpdate()
    {
        if (_healthBarRoot != null && _healthBarRoot.gameObject.activeSelf)
        {
            _healthBarRoot.position = transform.position + new Vector3(_healthBarOffset.x, _healthBarOffset.y, 0f);
            _healthBarRoot.rotation = Quaternion.identity;
        }
    }

    // ──────────────────────────── Damage Logic ─────────────────────────────

    /// <summary>
    /// Attempts to apply damage to this character (server-authoritative when networked),
    /// respecting team friendly-fire rules.
    /// </summary>
    /// <param name="amount">Raw damage amount (must be positive).</param>
    /// <param name="attacker">The team member dealing the damage (null for environment).</param>
    /// <param name="hitPoint">World position of the impact.</param>
    /// <param name="hitDirection">Direction of the incoming hit.</param>
    /// <returns>True if damage was applied; false if blocked (not server, dead, or friendly fire).</returns>
    public bool TryTakeDamage(
        float amount,
        TeamMember attacker = null,
        Vector2 hitPoint = default,
        Vector2 hitDirection = default)
    {
        // Only the Server/Host may authoritatively apply damage when networked
        if (IsSpawned && !IsServer) return false;
        if (IsDead || amount <= 0f) return false;

        // Enforce no friendly fire between teammates
        if (!TeamMember.CanDamage(attacker, _teamMember))
        {
            return false;
        }

        Vector2 effectiveHitPoint = hitPoint != default ? hitPoint : (Vector2)transform.position;

        if (IsSpawned)
        {
            _netCurrentHealth.Value = Mathf.Max(_netCurrentHealth.Value - amount, 0f);
            BroadcastHitFeedbackRpc(amount, effectiveHitPoint, hitDirection);
        }
        else
        {
            float prev = _localFallbackHealth;
            _localFallbackHealth = Mathf.Max(_localFallbackHealth - amount, 0f);
            ApplyLocalHitFeedback(amount, effectiveHitPoint, hitDirection, attacker);
            OnNetHealthChanged(prev, _localFallbackHealth);
        }

        return true;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastHitFeedbackRpc(float amount, Vector2 hitPoint, Vector2 hitDirection)
    {
        ApplyLocalHitFeedback(amount, hitPoint, hitDirection, null);
    }

    private void ApplyLocalHitFeedback(float amount, Vector2 hitPoint, Vector2 hitDirection, TeamMember attacker)
    {
        TriggerDamageFlash();
        SpawnBloodSplatter(hitPoint);

        var info = new DamageInfo
        {
            Amount = amount,
            Attacker = attacker,
            HitPoint = hitPoint,
            HitDirection = hitDirection
        };
        OnDamaged?.Invoke(info);
    }

    private void OnNetHealthChanged(float previousValue, float newValue)
    {
        RecalculateWoundedMultiplier();
        UpdateWorldHealthBar();

        OnHealthChanged?.Invoke(newValue, MaxHealth);

        if (newValue <= 0f && previousValue > 0f)
        {
            HandleDeath();
        }
        else if (newValue > 0f && previousValue <= 0f)
        {
            ApplyReviveVisuals();
        }
    }

    /// <summary>
    /// Restores the character to full health and resets wounded penalties (server-authoritative).
    /// </summary>
    public void ResetHealth()
    {
        if (IsSpawned)
        {
            if (!IsServer) return;
            _netCurrentHealth.Value = MaxHealth;
            ResetVisualsRpc();
        }
        else
        {
            float prev = _localFallbackHealth;
            _localFallbackHealth = MaxHealth;
            ApplyReviveVisuals();
            OnNetHealthChanged(prev, _localFallbackHealth);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ResetVisualsRpc()
    {
        ApplyReviveVisuals();
    }

    private void ApplyReviveVisuals()
    {
        RecalculateWoundedMultiplier();

        if (_collider != null) _collider.enabled = true;

        var weaponHolder = GetComponent<WeaponHolder>();
        if (weaponHolder != null)
        {
            weaponHolder.enabled = true;
            if (weaponHolder.ActiveWeapon != null)
            {
                weaponHolder.ActiveWeapon.gameObject.SetActive(true);
            }
        }

        if (_spriteRenderer != null)
        {
            _baseColor = _teamMember != null ? _teamMember.TeamColor : _baseColor;
            _spriteRenderer.color = _baseColor;
            _spriteRenderer.sortingOrder = 10;
        }

        if (_healthBarRoot != null)
        {
            _healthBarRoot.gameObject.SetActive(_showWorldHealthBar);
            UpdateWorldHealthBar();
        }
    }

    private void RecalculateWoundedMultiplier()
    {
        if (_config != null)
        {
            WoundedSpeedMultiplier = _config.GetWoundedSpeedMultiplier(HealthNormalized);
        }
        else
        {
            WoundedSpeedMultiplier = Mathf.Lerp(0.3f, 1f, HealthNormalized);
        }
    }

    private void HandleDeath()
    {
        _isFlashing = false;

        if (_collider != null) _collider.enabled = false;
        if (_rb != null) _rb.linearVelocity = Vector2.zero;

        var weaponHolder = GetComponent<WeaponHolder>();
        if (weaponHolder != null)
        {
            weaponHolder.enabled = false;
            if (weaponHolder.ActiveWeapon != null)
            {
                weaponHolder.ActiveWeapon.gameObject.SetActive(false);
            }
        }

        if (_spriteRenderer != null)
        {
            Color teamCol = _teamMember != null ? _teamMember.TeamColor : _baseColor;
            _spriteRenderer.color = new Color(
                teamCol.r * DeadTintMultiplier.r,
                teamCol.g * DeadTintMultiplier.g,
                teamCol.b * DeadTintMultiplier.b,
                DeadTintMultiplier.a);
            _spriteRenderer.sortingOrder = 2;
        }

        if (_healthBarRoot != null)
        {
            _healthBarRoot.gameObject.SetActive(false);
        }

        SpawnBloodDecal(transform.position, 0.75f, 0.85f);

        OnDeath?.Invoke();
    }

    // ──────────────────────────── Visual Feedback ──────────────────────────

    private void TriggerDamageFlash()
    {
        if (_spriteRenderer == null || IsDead) return;

        _isFlashing = true;
        _flashEndTime = Time.time + _damageFlashDuration;
        _spriteRenderer.color = DamageFlashColor;
    }

    private void UpdateBloodTrail()
    {
        float threshold = _config != null ? _config.BloodTrailHealthThreshold : 0.75f;
        if (HealthNormalized >= threshold) return;

        float interval = _config != null ? _config.BloodTrailInterval : 0.3f;
        if (Time.time < _nextBloodTrailTime) return;

        Vector2 currentPos = transform.position;
        if (Vector2.Distance(currentPos, _lastBloodSpawnPos) < 0.25f) return;

        float alpha = _config != null
            ? _config.GetBloodTrailIntensity(HealthNormalized)
            : Mathf.Lerp(1f, 0.3f, HealthNormalized / threshold);

        float scale = Mathf.Lerp(0.32f, 0.16f, HealthNormalized / threshold);
        SpawnBloodDecal(currentPos, scale, alpha);

        _lastBloodSpawnPos = currentPos;
        _nextBloodTrailTime = Time.time + interval;
    }

    private void SpawnBloodSplatter(Vector2 hitPoint)
    {
        Vector2 jitter = UnityEngine.Random.insideUnitCircle * 0.18f;
        SpawnBloodDecal(hitPoint + jitter, UnityEngine.Random.Range(0.2f, 0.35f), 0.75f);
    }

    private static Sprite _cachedCircleSprite;

    private static void SpawnBloodDecal(Vector2 position, float scale, float alpha)
    {
        if (_cachedCircleSprite == null)
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(r, r));
                    tex.SetPixel(x, y, d <= r ? Color.white : Color.clear);
                }
            }
            tex.Apply();
            _cachedCircleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        var go = new GameObject("BloodDecal");
        go.transform.position = new Vector3(position.x, position.y, 0f);
        go.transform.localScale = new Vector3(scale, scale, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedCircleSprite;
        sr.color = new Color(BloodCrimsonColor.r, BloodCrimsonColor.g, BloodCrimsonColor.b, alpha);
        sr.sortingOrder = -5;
    }

    // ──────────────────────────── World Health Bar ─────────────────────────

    private static Sprite _cachedBarSprite;

    private void SetupWorldHealthBar()
    {
        if (_cachedBarSprite == null)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            _cachedBarSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
        }

        var existing = transform.Find("HealthBar");
        if (existing != null)
        {
            Destroy(existing.gameObject);
        }

        var rootGo = new GameObject("HealthBar");
        rootGo.transform.SetParent(transform, false);
        _healthBarRoot = rootGo.transform;

        var bgGo = new GameObject("Background");
        bgGo.transform.SetParent(_healthBarRoot, false);
        bgGo.transform.localScale = new Vector3(0.9f, 0.11f, 1f);
        var bgSr = bgGo.AddComponent<SpriteRenderer>();
        bgSr.sprite = _cachedBarSprite;
        bgSr.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        bgSr.sortingOrder = 25;

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(_healthBarRoot, false);
        _healthBarFill = fillGo.transform;
        _healthBarFillRenderer = fillGo.AddComponent<SpriteRenderer>();
        _healthBarFillRenderer.sprite = _cachedBarSprite;
        _healthBarFillRenderer.sortingOrder = 26;
    }

    private void UpdateWorldHealthBar()
    {
        if (_healthBarFill == null || _healthBarFillRenderer == null) return;

        float pct = HealthNormalized;
        const float fullWidth = 0.86f;
        const float height = 0.07f;

        float currentWidth = fullWidth * pct;
        _healthBarFill.localScale = new Vector3(currentWidth, height, 1f);
        _healthBarFill.localPosition = new Vector3(-(fullWidth - currentWidth) * 0.5f, 0f, 0f);

        _healthBarFillRenderer.color = Color.Lerp(
            new Color(0.85f, 0.18f, 0.18f, 0.95f),
            new Color(0.35f, 0.85f, 0.45f, 0.95f),
            pct);
    }
}
