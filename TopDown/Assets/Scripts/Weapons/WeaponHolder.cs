using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Networked component that manages a character's weapon loadout — equipping, swapping,
/// syncing the active weapon indicator across clients, and routing fire/reload requests
/// through server-authoritative hitscan checks.
/// </summary>
public class WeaponHolder : NetworkBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Loadout")]
    [Tooltip("Primary weapon data (rifle, SMG, etc.). Assign a WeaponData asset.")]
    [SerializeField] private WeaponData _primaryWeaponData;

    [Tooltip("Sidearm weapon data (pistol). Assign a WeaponData asset.")]
    [SerializeField] private WeaponData _sidearmWeaponData;

    [Header("Input Settings")]
    [Tooltip("True for player characters; set to false for AI dummies so they don't fire on player mouse clicks.")]
    [SerializeField] private bool _usePlayerInput = true;

    [Header("Input Actions")]
    [SerializeField] private InputAction _fireAction = new InputAction(
        "Fire", InputActionType.Button, "<Mouse>/leftButton");

    [SerializeField] private InputAction _reloadAction = new InputAction(
        "Reload", InputActionType.Button, "<Keyboard>/r");

    [SerializeField] private InputAction _swapAction = new InputAction(
        "SwapWeapon", InputActionType.Button, "<Keyboard>/q");

    [Header("References")]
    [Tooltip("The TopDownCamera for screen shake on fire. Auto-found if left empty.")]
    [SerializeField] private TopDownCamera _topDownCamera;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<WeaponSlot> _netActiveSlot = new NetworkVariable<WeaponSlot>(
        WeaponSlot.Primary,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired when the active weapon changes. Passes the new active Weapon.</summary>
    public event Action<Weapon> OnWeaponSwapped;

    /// <summary>Fired each time any equipped weapon shoots (or when remote shot visuals arrive).</summary>
    public event Action<WeaponFireResult> OnWeaponFired;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Set to true while hacking a terminal so keyboard mashing doesn't trigger reload or weapon swap.</summary>
    public bool IsInputBlocked { get; set; }

    /// <summary>True if this instance should read local player mouse/keyboard input.</summary>
    public bool HasInputAuthority => _usePlayerInput && !IsInputBlocked && (!IsSpawned || IsOwner);

    /// <summary>The currently active weapon instance.</summary>
    public Weapon ActiveWeapon { get; private set; }

    /// <summary>The currently active weapon's slot type.</summary>
    public WeaponSlot ActiveSlot { get; private set; }

    /// <summary>
    /// Forces the player to equip the sidearm (e.g., when carrying the objective).
    /// While locked, weapon swapping is disabled.
    /// </summary>
    public bool ForceSidearm
    {
        get => _forceSidearm;
        set
        {
            _forceSidearm = value;
            if (_forceSidearm && ActiveSlot != WeaponSlot.Sidearm)
            {
                EquipWeapon(WeaponSlot.Sidearm);
            }
        }
    }

    // ────────────────────────────── Runtime State ──────────────────────────

    private Weapon _primaryWeapon;
    private Weapon _sidearmWeapon;
    private bool _forceSidearm;
    private bool _fireHeld;
    private bool _firePressedThisFrame;
    private bool _weaponsInitialized;
    private Collider2D _ownerCollider;
    private TeamMember _teamMember;
    private WeaponFireResult _lastFireResult;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        _ownerCollider = GetComponent<Collider2D>();
        _teamMember = GetComponent<TeamMember>();

        if (_topDownCamera == null)
        {
            _topDownCamera = FindAnyObjectByType<TopDownCamera>();
        }
    }

    private void Start()
    {
        EnsureWeaponsInitialized();

        if (!IsSpawned)
        {
            EquipWeapon(WeaponSlot.Primary);
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        EnsureWeaponsInitialized();
        _netActiveSlot.OnValueChanged += OnNetActiveSlotChanged;

        if (IsOwner)
        {
            EquipWeapon(WeaponSlot.Primary);
            if (_usePlayerInput)
            {
                EnableInputActions();
            }
        }
        else
        {
            DisableInputActions();
            ApplyEquippedWeaponLocal(_netActiveSlot.Value);
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netActiveSlot.OnValueChanged -= OnNetActiveSlotChanged;
        base.OnNetworkDespawn();
    }

    private void OnEnable()
    {
        if (HasInputAuthority)
        {
            EnableInputActions();
        }
    }

    private void OnDisable()
    {
        DisableInputActions();
    }

    private void EnableInputActions()
    {
        _fireAction.Enable();
        _reloadAction.Enable();
        _swapAction.Enable();

        _fireAction.started -= OnFireStarted;
        _fireAction.canceled -= OnFireCanceled;
        _reloadAction.performed -= OnReloadPerformed;
        _swapAction.performed -= OnSwapPerformed;

        _fireAction.started += OnFireStarted;
        _fireAction.canceled += OnFireCanceled;
        _reloadAction.performed += OnReloadPerformed;
        _swapAction.performed += OnSwapPerformed;
    }

    private void DisableInputActions()
    {
        _fireAction.started -= OnFireStarted;
        _fireAction.canceled -= OnFireCanceled;
        _reloadAction.performed -= OnReloadPerformed;
        _swapAction.performed -= OnSwapPerformed;

        _fireAction.Disable();
        _reloadAction.Disable();
        _swapAction.Disable();
    }

    private void Update()
    {
        if (!HasInputAuthority || ActiveWeapon == null) return;

        bool shouldFire = false;

        if (ActiveWeapon.Data.FireMode == FireMode.Automatic)
        {
            shouldFire = _fireHeld;
        }
        else // SemiAutomatic
        {
            shouldFire = _firePressedThisFrame;
            _firePressedThisFrame = false;
        }

        if (shouldFire)
        {
            TryFireActiveWeapon();
        }
    }

    // ──────────────────────────── Input Handlers ───────────────────────────

    private void OnFireStarted(InputAction.CallbackContext ctx)
    {
        _fireHeld = true;
        _firePressedThisFrame = true;
    }

    private void OnFireCanceled(InputAction.CallbackContext ctx)
    {
        _fireHeld = false;
    }

    private void OnReloadPerformed(InputAction.CallbackContext ctx)
    {
        if (!HasInputAuthority) return;
        ActiveWeapon?.StartReload();
    }

    private void OnSwapPerformed(InputAction.CallbackContext ctx)
    {
        if (!HasInputAuthority || _forceSidearm) return;

        if (ActiveSlot == WeaponSlot.Primary)
            EquipWeapon(WeaponSlot.Sidearm);
        else
            EquipWeapon(WeaponSlot.Primary);
    }

    // ──────────────────────────── Weapon Management ───────────────────────

    private void EnsureWeaponsInitialized()
    {
        if (_weaponsInitialized) return;
        _weaponsInitialized = true;

        if (_primaryWeaponData != null)
        {
            _primaryWeapon = CreateWeaponInstance(_primaryWeaponData, "PrimaryWeapon");
        }

        if (_sidearmWeaponData != null)
        {
            _sidearmWeapon = CreateWeaponInstance(_sidearmWeaponData, "SidearmWeapon");
        }
    }

    private void OnNetActiveSlotChanged(WeaponSlot previous, WeaponSlot current)
    {
        if (!IsOwner)
        {
            ApplyEquippedWeaponLocal(current);
        }
    }

    /// <summary>
    /// Equips the weapon in the specified slot, deactivating the other, and syncs across the network.
    /// </summary>
    public void EquipWeapon(WeaponSlot slot)
    {
        EnsureWeaponsInitialized();
        ApplyEquippedWeaponLocal(slot);

        if (IsSpawned && IsOwner)
        {
            _netActiveSlot.Value = ActiveSlot;
        }
    }

    private void ApplyEquippedWeaponLocal(WeaponSlot slot)
    {
        Weapon target = (slot == WeaponSlot.Primary) ? _primaryWeapon : _sidearmWeapon;

        if (target == null)
        {
            target = (slot == WeaponSlot.Primary) ? _sidearmWeapon : _primaryWeapon;
            if (target == null) return;
            slot = target.Data.Slot;
        }

        if (_primaryWeapon != null) _primaryWeapon.gameObject.SetActive(false);
        if (_sidearmWeapon != null) _sidearmWeapon.gameObject.SetActive(false);

        target.gameObject.SetActive(true);
        ActiveWeapon = target;
        ActiveSlot = slot;

        OnWeaponSwapped?.Invoke(ActiveWeapon);
    }

    /// <summary>
    /// Replaces the weapon in the given slot with a new WeaponData asset at runtime.
    /// Useful for loadout menus or pickup systems.
    /// </summary>
    /// <param name="slot">Slot to replace.</param>
    /// <param name="newData">New weapon data asset.</param>
    public void SetWeaponData(WeaponSlot slot, WeaponData newData)
    {
        if (newData == null) return;

        if (slot == WeaponSlot.Primary && _primaryWeapon != null)
        {
            Destroy(_primaryWeapon.gameObject);
        }
        else if (slot == WeaponSlot.Sidearm && _sidearmWeapon != null)
        {
            Destroy(_sidearmWeapon.gameObject);
        }

        string childName = slot == WeaponSlot.Primary ? "PrimaryWeapon" : "SidearmWeapon";
        var newWeapon = CreateWeaponInstance(newData, childName);

        if (slot == WeaponSlot.Primary)
            _primaryWeapon = newWeapon;
        else
            _sidearmWeapon = newWeapon;

        EquipWeapon(ActiveSlot);
    }

    // ──────────────────────────── Firing ───────────────────────────────────

    /// <summary>
    /// Fires the currently equipped weapon along the character's facing direction if ready.
    /// Handles both local responsiveness and server-authoritative hit validation.
    /// </summary>
    /// <returns>True if a shot was fired.</returns>
    public bool TryFireActiveWeapon()
    {
        if (ActiveWeapon == null || !ActiveWeapon.CanFire) return false;

        Vector2 aimDirection = transform.right;
        Vector2 origin = (Vector2)transform.position;

        if (!ActiveWeapon.TryFire(origin, aimDirection, _ownerCollider, _teamMember))
        {
            return false;
        }

        // Screen shake for the local player
        if (HasInputAuthority && _topDownCamera != null)
        {
            _topDownCamera.Shake(ActiveWeapon.Data.RecoilShake);
        }

        if (IsSpawned)
        {
            if (IsServer)
            {
                // Host/Server fired: broadcast tracer/muzzle visuals to non-server clients
                BroadcastShotVisualsRpc(
                    _lastFireResult.Origin,
                    _lastFireResult.HitPoint,
                    _lastFireResult.DidHit,
                    aimDirection,
                    OwnerClientId);
            }
            else if (IsOwner)
            {
                // Non-host Client fired: request server to perform authoritative damage & relay visuals
                RequestFireServerRpc(origin, aimDirection);
            }
        }

        return true;
    }

    [Rpc(SendTo.Server)]
    private void RequestFireServerRpc(Vector2 origin, Vector2 aimDirection)
    {
        if (ActiveWeapon == null) return;

        // Server executes authoritative shot
        if (ActiveWeapon.TryFire(origin, aimDirection, _ownerCollider, _teamMember))
        {
            BroadcastShotVisualsRpc(
                _lastFireResult.Origin,
                _lastFireResult.HitPoint,
                _lastFireResult.DidHit,
                aimDirection,
                OwnerClientId);
        }
    }

    [Rpc(SendTo.NotServer)]
    private void BroadcastShotVisualsRpc(
        Vector2 barrelOrigin,
        Vector2 hitPoint,
        bool didHit,
        Vector2 aimDirection,
        ulong shooterClientId)
    {
        // Skip if this client is the shooter who already predicted their own tracer/flash locally
        if (IsOwner && NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClientId == shooterClientId)
        {
            return;
        }

        if (ActiveWeapon != null)
        {
            ActiveWeapon.ShowMuzzleFlash(barrelOrigin, aimDirection);
        }

        var remoteResult = new WeaponFireResult
        {
            Origin = barrelOrigin,
            Direction = aimDirection,
            DidHit = didHit,
            HitPoint = hitPoint,
            HitDistance = Vector2.Distance(barrelOrigin, hitPoint),
            HitCollider = null,
            Damage = 0f
        };

        OnWeaponFired?.Invoke(remoteResult);
    }

    // ──────────────────────────── Helpers ──────────────────────────────────

    private Weapon CreateWeaponInstance(WeaponData data, string childName)
    {
        var existing = transform.Find(childName);
        if (existing != null)
        {
            Destroy(existing.gameObject);
        }

        var go = new GameObject(childName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        var weapon = go.AddComponent<Weapon>();
        weapon.Initialize(data);
        weapon.OnFire += HandleWeaponFired;

        go.SetActive(false);
        return weapon;
    }

    private void HandleWeaponFired(WeaponFireResult result)
    {
        _lastFireResult = result;

        // Only apply damage when offline or on the authoritative Server/Host
        bool canApplyDamage = !IsSpawned || IsServer;
        if (canApplyDamage && result.DidHit && result.HitCollider != null)
        {
            var targetHealth = result.HitCollider.GetComponentInParent<Health>();
            if (targetHealth != null)
            {
                targetHealth.TryTakeDamage(
                    result.Damage,
                    _teamMember,
                    result.HitPoint,
                    result.Direction);
            }
        }

        OnWeaponFired?.Invoke(result);
    }
}
