using UnityEngine;

/// <summary>
/// ScriptableObject defining all stats and properties for a weapon type.
/// Create new weapon types via Assets → Create → TopDown → Weapon Data.
/// Swap loadouts by assigning different WeaponData assets — no code changes needed.
/// </summary>
[CreateAssetMenu(fileName = "NewWeapon", menuName = "TopDown/Weapon Data")]
public class WeaponData : ScriptableObject
{
    // ───────────────────────────── Identity ─────────────────────────────

    [Header("Identity")]

    [Tooltip("Display name shown in UI (e.g., 'M4A1 Rifle').")]
    [SerializeField] private string _displayName = "New Weapon";

    [Tooltip("Which equipment slot this weapon occupies.")]
    [SerializeField] private WeaponSlot _slot = WeaponSlot.Primary;

    [Tooltip("How the weapon fires when the trigger is held.")]
    [SerializeField] private FireMode _fireMode = FireMode.Automatic;

    // ───────────────────────────── Firing ───────────────────────────────

    [Header("Firing")]

    [Tooltip("Rounds fired per second.")]
    [Min(0.1f)]
    [SerializeField] private float _fireRate = 8f;

    [Tooltip("Base damage per bullet at point-blank range.")]
    [Min(0f)]
    [SerializeField] private float _damage = 20f;

    [Tooltip("Maximum range of hitscan raycast in world units.")]
    [Min(1f)]
    [SerializeField] private float _range = 50f;

    [Tooltip("Spread angle in degrees (0 = perfectly accurate). " +
             "Each shot deviates randomly within ±spread/2.")]
    [Range(0f, 30f)]
    [SerializeField] private float _spreadAngle = 3f;

    [Tooltip("Damage falloff curve. X axis = normalised distance (0–1), " +
             "Y axis = damage multiplier (0–1). Left = close, Right = max range.")]
    [SerializeField] private AnimationCurve _damageFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0.4f);

    // ───────────────────────────── Ammo ─────────────────────────────────

    [Header("Ammo & Reloading")]

    [Tooltip("Bullets per magazine.")]
    [Min(1)]
    [SerializeField] private int _magazineSize = 30;

    [Tooltip("Total reserve ammo the player starts with.")]
    [Min(0)]
    [SerializeField] private int _maxReserveAmmo = 90;

    [Tooltip("Seconds to complete a full reload.")]
    [Min(0.1f)]
    [SerializeField] private float _reloadTime = 2f;

    // ───────────────────────────── Visuals ──────────────────────────────

    [Header("Weapon Indicator Visual")]

    [Tooltip("Optional custom sprite for the weapon. If left empty, a solid line/rectangle is used.")]
    [SerializeField] private Sprite _visualSprite;

    [Tooltip("Length of the weapon visual along the aim direction (world units).")]
    [Min(0.05f)]
    [SerializeField] private float _visualLength = 0.85f;

    [Tooltip("Thickness/width of the weapon visual (world units).")]
    [Min(0.02f)]
    [SerializeField] private float _visualWidth = 0.11f;

    [Tooltip("Local offset from the player center (X = forward along aim, Y = sideways).")]
    [SerializeField] private Vector2 _visualOffset = new Vector2(0.55f, 0f);

    [Tooltip("Color tint of the equipped weapon indicator.")]
    [SerializeField] private Color _visualColor = Color.black;

    [Header("Muzzle Flash & Visuals")]

    [Tooltip("Intensity of the muzzle flash point light (affects visibility for both shooter and targets — core mechanic).")]
    [Min(0f)]
    [SerializeField] private float _muzzleFlashIntensity = 3f;

    [Tooltip("Radius of the muzzle flash light in world units.")]
    [Min(0.1f)]
    [SerializeField] private float _muzzleFlashRadius = 5f;

    [Tooltip("Duration of the muzzle flash in seconds.")]
    [Min(0.01f)]
    [SerializeField] private float _muzzleFlashDuration = 0.06f;

    [Tooltip("Color of the muzzle flash light.")]
    [SerializeField] private Color _muzzleFlashColor = new Color(1f, 0.96f, 0.8f, 1f);

    // ───────────────────────────── Feel ─────────────────────────────────

    [Header("Game Feel")]

    [Tooltip("Screen shake intensity per shot (passed to TopDownCamera.Shake).")]
    [Min(0f)]
    [SerializeField] private float _recoilShake = 0.08f;

    [Tooltip("Barrel offset from the player's pivot along the aim direction (for muzzle flash spawn).")]
    [SerializeField] private float _barrelOffset = 0.55f;

    // ───────────────────────── Public Accessors ─────────────────────────

    /// <summary>Display name shown in UI.</summary>
    public string DisplayName => _displayName;

    /// <summary>Which equipment slot this weapon occupies.</summary>
    public WeaponSlot Slot => _slot;

    /// <summary>Firing mode (semi-auto or full-auto).</summary>
    public FireMode FireMode => _fireMode;

    /// <summary>Rounds fired per second.</summary>
    public float FireRate => _fireRate;

    /// <summary>Seconds between shots (1 / FireRate).</summary>
    public float FireInterval => 1f / _fireRate;

    /// <summary>Base damage per bullet at point-blank range.</summary>
    public float Damage => _damage;

    /// <summary>Maximum hitscan range in world units.</summary>
    public float Range => _range;

    /// <summary>Spread angle in degrees.</summary>
    public float SpreadAngle => _spreadAngle;

    /// <summary>Bullets per magazine.</summary>
    public int MagazineSize => _magazineSize;

    /// <summary>Total reserve ammo at spawn.</summary>
    public int MaxReserveAmmo => _maxReserveAmmo;

    /// <summary>Seconds to complete a reload.</summary>
    public float ReloadTime => _reloadTime;

    /// <summary>Optional custom sprite for the weapon visual.</summary>
    public Sprite VisualSprite => _visualSprite;

    /// <summary>Length of the weapon visual along the aim direction.</summary>
    public float VisualLength => _visualLength;

    /// <summary>Thickness/width of the weapon visual.</summary>
    public float VisualWidth => _visualWidth;

    /// <summary>Local offset of the weapon visual from the player center.</summary>
    public Vector2 VisualOffset => _visualOffset;

    /// <summary>Color of the weapon visual.</summary>
    public Color VisualColor => _visualColor;

    /// <summary>Muzzle flash point light intensity.</summary>
    public float MuzzleFlashIntensity => _muzzleFlashIntensity;

    /// <summary>Muzzle flash light radius in world units.</summary>
    public float MuzzleFlashRadius => _muzzleFlashRadius;

    /// <summary>Duration of muzzle flash in seconds.</summary>
    public float MuzzleFlashDuration => _muzzleFlashDuration;

    /// <summary>Color of the muzzle flash light.</summary>
    public Color MuzzleFlashColor => _muzzleFlashColor;

    /// <summary>Screen shake intensity per shot.</summary>
    public float RecoilShake => _recoilShake;

    /// <summary>Distance from player pivot to barrel tip along aim direction.</summary>
    public float BarrelOffset => _barrelOffset;

    /// <summary>
    /// Evaluates damage at a given distance, applying the falloff curve.
    /// </summary>
    /// <param name="distance">Distance to the hit point in world units.</param>
    /// <returns>Final damage after falloff.</returns>
    public float GetDamageAtDistance(float distance)
    {
        if (_range <= 0f) return _damage;
        float normalised = Mathf.Clamp01(distance / _range);
        return _damage * _damageFalloff.Evaluate(normalised);
    }
}
