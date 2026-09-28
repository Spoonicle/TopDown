using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Runtime weapon instance that handles firing, reloading, ammo tracking,
/// and muzzle flash effects. All stats come from a <see cref="WeaponData"/> asset.
/// Attach as a child of the player — positioned and rotated by <see cref="WeaponHolder"/>.
/// </summary>
public class Weapon : MonoBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("Weapon stats asset — defines all tunable values for this weapon.")]
    [SerializeField] private WeaponData _data;

    [Header("Layer Mask")]
    [Tooltip("Physics layers the hitscan raycast can hit (walls, enemies, etc.).")]
    [SerializeField] private LayerMask _hitMask = ~0;

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired each time the weapon shoots. Passes the hit info (null if missed).</summary>
    public event Action<WeaponFireResult> OnFire;

    /// <summary>Fired when a reload starts.</summary>
    public event Action OnReloadStarted;

    /// <summary>Fired when a reload completes.</summary>
    public event Action OnReloadCompleted;

    /// <summary>Fired when current magazine or reserve ammo changes.</summary>
    public event Action<int, int> OnAmmoChanged;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The weapon stats asset driving this instance.</summary>
    public WeaponData Data => _data;

    /// <summary>Bullets remaining in the current magazine.</summary>
    public int CurrentAmmo { get; private set; }

    /// <summary>Bullets remaining in reserve (not in magazine).</summary>
    public int ReserveAmmo { get; private set; }

    /// <summary>True while a reload is in progress.</summary>
    public bool IsReloading { get; private set; }

    /// <summary>True if the weapon can fire right now.</summary>
    public bool CanFire => _data != null
                           && !IsReloading
                           && CurrentAmmo > 0
                           && Time.time >= _nextFireTime;

    // ────────────────────────────── Runtime State ──────────────────────────

    private float _nextFireTime;
    private float _reloadEndTime;

    // Weapon visual & muzzle flash
    private SpriteRenderer _weaponVisualSprite;
    private SpriteRenderer _muzzleFlashSprite;
    private Light2D _muzzleFlashLight;
    private float _muzzleFlashHideTime;

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Initializes the weapon with a specific WeaponData asset.
    /// Called by <see cref="WeaponHolder"/> when equipping.
    /// </summary>
    /// <param name="data">The weapon stats to use.</param>
    public void Initialize(WeaponData data)
    {
        _data = data;
        CurrentAmmo = _data.MagazineSize;
        ReserveAmmo = _data.MaxReserveAmmo;
        IsReloading = false;
        _nextFireTime = 0f;

        SetupWeaponVisual();
        SetupMuzzleFlash();
    }

    // ──────────────────────────── Unity Callbacks ──────────────────────────

    private void Update()
    {
        // Handle reload timer
        if (IsReloading && Time.time >= _reloadEndTime)
        {
            FinishReload();
        }

        // Handle muzzle flash timeout
        if (_muzzleFlashSprite != null && _muzzleFlashSprite.enabled && Time.time >= _muzzleFlashHideTime)
        {
            _muzzleFlashSprite.enabled = false;
            if (_muzzleFlashLight != null) _muzzleFlashLight.enabled = false;
        }
    }

    // ──────────────────────────── Firing ───────────────────────────────────

    /// <summary>
    /// Attempts to fire the weapon. Returns true if a shot was fired.
    /// </summary>
    /// <param name="origin">World position to fire from (player position).</param>
    /// <param name="aimDirection">Normalized direction the player is aiming.</param>
    /// <param name="ignoredCollider">Collider to skip (typically the player's own collider).</param>
    /// <param name="shooterTeam">The shooter's TeamMember component; teammates on the same team are skipped.</param>
    /// <returns>True if a shot was successfully fired.</returns>
    public bool TryFire(
        Vector2 origin,
        Vector2 aimDirection,
        Collider2D ignoredCollider = null,
        TeamMember shooterTeam = null)
    {
        if (!CanFire) return false;

        // Consume ammo
        CurrentAmmo--;
        _nextFireTime = Time.time + _data.FireInterval;

        // Apply spread
        float spreadRad = _data.SpreadAngle * 0.5f * Mathf.Deg2Rad;
        float randomSpread = UnityEngine.Random.Range(-spreadRad, spreadRad);
        float aimAngle = Mathf.Atan2(aimDirection.y, aimDirection.x) + randomSpread;
        Vector2 fireDirection = new Vector2(Mathf.Cos(aimAngle), Mathf.Sin(aimAngle));

        // Hitscan raycast
        Vector2 barrelPosition = origin + aimDirection.normalized * _data.BarrelOffset;
        RaycastHit2D hit = default;
        RaycastHit2D[] hits = Physics2D.RaycastAll(barrelPosition, fireDirection, _data.Range, _hitMask);

        // Find the closest valid hit (skipping self and own team, hitting all other teams and walls)
        float closestDist = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.collider == null || h.collider.isTrigger) continue;
            if (h.collider == ignoredCollider) continue;

            if (shooterTeam != null)
            {
                var hitTeam = h.collider.GetComponentInParent<TeamMember>();
                if (hitTeam != null && !TeamMember.CanDamage(shooterTeam, hitTeam))
                {
                    continue;
                }
            }

            if (h.distance < closestDist)
            {
                closestDist = h.distance;
                hit = h;
            }
        }

        // Build result
        bool didHit = hit.collider != null;
        var result = new WeaponFireResult
        {
            Origin = barrelPosition,
            Direction = fireDirection,
            DidHit = didHit,
            HitPoint = didHit ? hit.point : barrelPosition + fireDirection * _data.Range,
            HitDistance = didHit ? hit.distance : _data.Range,
            HitCollider = hit.collider,
            Damage = didHit ? _data.GetDamageAtDistance(hit.distance) : 0f
        };

        // Show muzzle flash
        ShowMuzzleFlash(barrelPosition, aimDirection);

        // Fire events
        OnFire?.Invoke(result);
        OnAmmoChanged?.Invoke(CurrentAmmo, ReserveAmmo);

        // Auto-reload when empty
        if (CurrentAmmo <= 0 && ReserveAmmo > 0)
        {
            StartReload();
        }

        return true;
    }

    // ──────────────────────────── Reloading ────────────────────────────────

    /// <summary>
    /// Starts a reload if possible (not already reloading, magazine not full, has reserve ammo).
    /// </summary>
    /// <returns>True if reload was started.</returns>
    public bool StartReload()
    {
        if (IsReloading) return false;
        if (_data == null) return false;
        if (CurrentAmmo >= _data.MagazineSize) return false;
        if (ReserveAmmo <= 0) return false;

        IsReloading = true;
        _reloadEndTime = Time.time + _data.ReloadTime;
        OnReloadStarted?.Invoke();
        return true;
    }

    private void FinishReload()
    {
        int bulletsNeeded = _data.MagazineSize - CurrentAmmo;
        int bulletsToLoad = Mathf.Min(bulletsNeeded, ReserveAmmo);

        CurrentAmmo += bulletsToLoad;
        ReserveAmmo -= bulletsToLoad;
        IsReloading = false;

        OnReloadCompleted?.Invoke();
        OnAmmoChanged?.Invoke(CurrentAmmo, ReserveAmmo);
    }

    // ──────────────────────────── Weapon Visual ────────────────────────────

    private void SetupWeaponVisual()
    {
        if (_data == null) return;

        var visualTransform = transform.Find("WeaponVisual");
        if (visualTransform == null)
        {
            var visualGo = new GameObject("WeaponVisual");
            visualGo.transform.SetParent(transform, false);
            visualTransform = visualGo.transform;
        }

        _weaponVisualSprite = visualTransform.GetComponent<SpriteRenderer>();
        if (_weaponVisualSprite == null)
        {
            _weaponVisualSprite = visualTransform.gameObject.AddComponent<SpriteRenderer>();
        }

        _weaponVisualSprite.sprite = _data.VisualSprite != null
            ? _data.VisualSprite
            : CreateSolidSquareSprite();

        _weaponVisualSprite.color = _data.VisualColor;
        _weaponVisualSprite.sortingOrder = 12;

        // Add a trigger collider on the weapon barrel so it can physically push swinging doors open
        var weaponCollider = visualTransform.GetComponent<BoxCollider2D>();
        if (weaponCollider == null)
        {
            weaponCollider = visualTransform.gameObject.AddComponent<BoxCollider2D>();
        }
        weaponCollider.isTrigger = true;
        weaponCollider.size = Vector2.one;

        visualTransform.localPosition = new Vector3(_data.VisualOffset.x, _data.VisualOffset.y, 0f);
        visualTransform.localRotation = Quaternion.identity;
        visualTransform.localScale = new Vector3(_data.VisualLength, _data.VisualWidth, 1f);
    }

    private static Sprite _cachedSquareSprite;

    private static Sprite CreateSolidSquareSprite()
    {
        if (_cachedSquareSprite != null) return _cachedSquareSprite;

        const int size = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;

        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Color.white;
        }
        tex.SetPixels(pixels);
        tex.Apply();

        _cachedSquareSprite = Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            size);
        _cachedSquareSprite.name = "SolidWeaponLineSprite";
        return _cachedSquareSprite;
    }

    // ──────────────────────────── Muzzle Flash ─────────────────────────────

    private void SetupMuzzleFlash()
    {
        // Create or find the muzzle flash child object
        var flashTransform = transform.Find("MuzzleFlash");
        if (flashTransform == null)
        {
            var flashGo = new GameObject("MuzzleFlash");
            flashGo.transform.SetParent(transform, false);
            flashTransform = flashGo.transform;
        }

        _muzzleFlashSprite = flashTransform.GetComponent<SpriteRenderer>();
        if (_muzzleFlashSprite == null)
        {
            _muzzleFlashSprite = flashTransform.gameObject.AddComponent<SpriteRenderer>();
        }

        // Assign a sprite if none is set — generate a soft circle procedurally
        if (_muzzleFlashSprite.sprite == null)
        {
            _muzzleFlashSprite.sprite = CreateFlashSprite();
        }

        _muzzleFlashSprite.sortingOrder = 20;
        _muzzleFlashSprite.enabled = false;

        // Add a 2D Point Light so gunfire illuminates dark factory rooms
        _muzzleFlashLight = flashTransform.GetComponent<Light2D>();
        if (_muzzleFlashLight == null)
        {
            _muzzleFlashLight = flashTransform.gameObject.AddComponent<Light2D>();
        }
        _muzzleFlashLight.lightType = Light2D.LightType.Point;
        _muzzleFlashLight.enabled = false;
    }

    private static Sprite _cachedFlashSprite;

    private static Sprite CreateFlashSprite()
    {
        if (_cachedFlashSprite != null) return _cachedFlashSprite;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        float center = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                float t = Mathf.Clamp01(1f - (dist / center));
                // Soft falloff for a glowy flash
                float alpha = t * t;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        _cachedFlashSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 64f);
        _cachedFlashSprite.name = "MuzzleFlashSprite";
        return _cachedFlashSprite;
    }

    /// <summary>
    /// Displays the muzzle flash visual and dynamic 2D point light at the given barrel position.
    /// </summary>
    /// <param name="barrelPosition">World position of the barrel tip.</param>
    /// <param name="aimDirection">Direction the weapon was aimed.</param>
    public void ShowMuzzleFlash(Vector2 barrelPosition, Vector2 aimDirection)
    {
        if (_muzzleFlashSprite == null || _data == null) return;

        // Position at barrel
        _muzzleFlashSprite.transform.position = (Vector3)barrelPosition;

        // Random rotation for variety
        float randomAngle = UnityEngine.Random.Range(0f, 360f);
        _muzzleFlashSprite.transform.rotation = Quaternion.Euler(0f, 0f, randomAngle);

        // Scale based on intensity
        float scale = _data.MuzzleFlashRadius * 0.2f;
        _muzzleFlashSprite.transform.localScale = new Vector3(scale, scale, 1f);

        // Color
        _muzzleFlashSprite.color = _data.MuzzleFlashColor;
        _muzzleFlashSprite.enabled = true;

        if (_muzzleFlashLight != null)
        {
            _muzzleFlashLight.color = _data.MuzzleFlashColor;
            _muzzleFlashLight.intensity = _data.MuzzleFlashIntensity;
            _muzzleFlashLight.pointLightOuterRadius = _data.MuzzleFlashRadius;
            _muzzleFlashLight.pointLightInnerRadius = _data.MuzzleFlashRadius * 0.15f;
            _muzzleFlashLight.enabled = true;
        }

        _muzzleFlashHideTime = Time.time + _data.MuzzleFlashDuration;
    }
}

/// <summary>
/// Data about a single weapon fire event, passed via <see cref="Weapon.OnFire"/>.
/// </summary>
public struct WeaponFireResult
{
    /// <summary>World position the shot originated from (barrel tip).</summary>
    public Vector2 Origin;

    /// <summary>Direction the bullet traveled (after spread).</summary>
    public Vector2 Direction;

    /// <summary>True if the raycast hit something.</summary>
    public bool DidHit;

    /// <summary>World position of the hit (or max range endpoint if missed).</summary>
    public Vector2 HitPoint;

    /// <summary>Distance from origin to hit point.</summary>
    public float HitDistance;

    /// <summary>The collider that was hit (null if missed).</summary>
    public Collider2D HitCollider;

    /// <summary>Damage to apply (after distance falloff). 0 if missed.</summary>
    public float Damage;
}
