using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked component attached to player characters that tracks whether they are carrying
/// downloaded objective data, applies carrier movement/weapon restrictions, and renders
/// a visual indicator and directional arrow toward their team's <see cref="ExtractionZone"/>.
/// </summary>
[RequireComponent(typeof(PlayerController))]
[RequireComponent(typeof(Health))]
[RequireComponent(typeof(TeamMember))]
public class ObjectiveCarrier : NetworkBehaviour
{
    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<bool> _netIsCarryingData = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private bool _localFallbackCarrying;

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired when this player picks up or loses the objective data.</summary>
    public event Action<bool> OnCarryingStateChanged;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>True if this player currently holds the downloaded objective data.</summary>
    public bool IsCarryingData => IsSpawned ? _netIsCarryingData.Value : _localFallbackCarrying;

    /// <summary>The TeamMember component of this carrier.</summary>
    public TeamMember CarrierTeam => _teamMember;

    // ────────────────────────────── Cached Refs ────────────────────────────

    private PlayerController _playerController;
    private WeaponHolder _weaponHolder;
    private Health _health;
    private TeamMember _teamMember;

    // Visual badge & extraction pointer
    private Transform _badgeTransform;
    private SpriteRenderer _badgeRenderer;
    private Transform _arrowPivot;
    private SpriteRenderer _arrowRenderer;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        _playerController = GetComponent<PlayerController>();
        _weaponHolder = GetComponent<WeaponHolder>();
        _health = GetComponent<Health>();
        _teamMember = GetComponent<TeamMember>();

        SetupCarrierVisuals();
    }

    private void OnEnable()
    {
        if (_health != null)
        {
            _health.OnDeath += HandleCarrierDeath;
        }
    }

    private void OnDisable()
    {
        if (_health != null)
        {
            _health.OnDeath -= HandleCarrierDeath;
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _netIsCarryingData.OnValueChanged += OnNetCarryingChanged;
        ApplyCarryingState(_netIsCarryingData.Value);
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netIsCarryingData.OnValueChanged -= OnNetCarryingChanged;
        base.OnNetworkDespawn();
    }

    private void LateUpdate()
    {
        if (!IsCarryingData) return;

        // Keep data badge centered above/on the player
        if (_badgeTransform != null)
        {
            _badgeTransform.position = transform.position + new Vector3(0f, 1.05f, 0f);
            _badgeTransform.rotation = Quaternion.Euler(0f, 0f, 45f);
        }

        // Rotate extraction guide arrow toward the carrier's team extraction zone (or nearest stairwell toward 1st floor)
        if (_arrowPivot != null)
        {
            bool showArrow = _playerController != null && _playerController.HasInputAuthority;
            _arrowPivot.gameObject.SetActive(showArrow);

            if (showArrow)
            {
                ExtractionZone zone = ExtractionZone.FindZoneForTeam(_teamMember);
                if (zone != null)
                {
                    Vector2 targetWorld = zone.transform.position;
                    if (FactoryMapGenerator.Instance != null)
                    {
                        targetWorld = FactoryMapGenerator.Instance.ResolveNavigationTarget(transform.position, targetWorld);
                    }

                    Vector2 toZone = targetWorld - (Vector2)transform.position;
                    if (toZone.sqrMagnitude > 0.01f)
                    {
                        float angle = Mathf.Atan2(toZone.y, toZone.x) * Mathf.Rad2Deg;
                        _arrowPivot.position = transform.position;
                        _arrowPivot.rotation = Quaternion.Euler(0f, 0f, angle);
                    }
                }
                else
                {
                    _arrowPivot.gameObject.SetActive(false);
                }
            }
        }
    }

    // ──────────────────────────── Authority Methods ────────────────────────

    /// <summary>
    /// Server-authoritative method to grant or remove the downloaded objective data on this player.
    /// </summary>
    public void SetCarryingDataServer(bool carrying)
    {
        if (IsSpawned)
        {
            if (!IsServer) return;
            _netIsCarryingData.Value = carrying;
        }
        else
        {
            _localFallbackCarrying = carrying;
            ApplyCarryingState(carrying);
        }
    }

    private void OnNetCarryingChanged(bool previousValue, bool newValue)
    {
        ApplyCarryingState(newValue);
    }

    private void ApplyCarryingState(bool carrying)
    {
        if (_playerController != null)
        {
            _playerController.IsCarryingObjective = carrying;
        }

        if (_weaponHolder != null)
        {
            _weaponHolder.ForceSidearm = carrying;
        }

        if (_badgeTransform != null)
        {
            _badgeTransform.gameObject.SetActive(carrying);
        }

        if (_arrowPivot != null)
        {
            bool showArrow = carrying && _playerController != null && _playerController.HasInputAuthority;
            _arrowPivot.gameObject.SetActive(showArrow);
        }

        OnCarryingStateChanged?.Invoke(carrying);
    }

    private void HandleCarrierDeath()
    {
        if (!IsCarryingData) return;

        bool hasServerAuth = !IsSpawned || IsServer;
        if (hasServerAuth)
        {
            SetCarryingDataServer(false);

            // Notify any terminal that the carrier died so the terminal can be re-hacked
            var terminal = FindAnyObjectByType<ComputerTerminalObjective>();
            if (terminal != null)
            {
                terminal.OnCarrierDiedServer();
            }
        }
    }

    // ──────────────────────────── Visuals ──────────────────────────────────

    private static Sprite _cachedSquareSprite;

    private void SetupCarrierVisuals()
    {
        if (_cachedSquareSprite == null)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            _cachedSquareSprite = Sprite.Create(tex, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 8f);
        }

        // 1. Floating cyan diamond badge above the carrier
        var existingBadge = transform.Find("DataCarrierBadge");
        if (existingBadge != null) Destroy(existingBadge.gameObject);

        var badgeGo = new GameObject("DataCarrierBadge");
        badgeGo.transform.SetParent(transform, false);
        badgeGo.transform.localScale = new Vector3(0.22f, 0.22f, 1f);
        _badgeTransform = badgeGo.transform;

        _badgeRenderer = badgeGo.AddComponent<SpriteRenderer>();
        _badgeRenderer.sprite = _cachedSquareSprite;
        _badgeRenderer.color = new Color(0.2f, 0.95f, 0.75f, 0.95f);
        _badgeRenderer.sortingOrder = 28;
        badgeGo.SetActive(false);

        // 2. Orbiting directional pointer arrow pointing toward the team's ExtractionZone
        var existingArrow = transform.Find("ExtractionArrowPivot");
        if (existingArrow != null) Destroy(existingArrow.gameObject);

        var pivotGo = new GameObject("ExtractionArrowPivot");
        pivotGo.transform.SetParent(transform, false);
        _arrowPivot = pivotGo.transform;

        var tipGo = new GameObject("ArrowTip");
        tipGo.transform.SetParent(_arrowPivot, false);
        tipGo.transform.localPosition = new Vector3(1.15f, 0f, 0f);
        tipGo.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
        tipGo.transform.localScale = new Vector3(0.18f, 0.18f, 1f);

        _arrowRenderer = tipGo.AddComponent<SpriteRenderer>();
        _arrowRenderer.sprite = _cachedSquareSprite;
        _arrowRenderer.color = new Color(0.2f, 0.95f, 0.55f, 0.85f);
        _arrowRenderer.sortingOrder = 27;

        pivotGo.SetActive(false);
    }
}
