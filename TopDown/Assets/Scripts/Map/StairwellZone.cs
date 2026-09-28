using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Physical 2D walkable staircase flight connecting a <see cref="LowerFloorLevel"/> (at its bottom landing)
/// and an <see cref="UpperFloorLevel"/> (at its top landing).
/// <para>
/// - Entering from the <b>Bottom Landing</b> (on the lower floor) or <b>Top Landing</b> (on the upper floor)
///   puts the player <b>on top of the stairs</b> (<see cref="IsLocalPlayerClimbing"/> = true), smoothly
///   cross-fading floors from 0% to 100% as the player walks along the steps.
/// - Walking across the <b>further/elevated half</b> of the staircase while on the lower floor without
///   entering from the bottom landing lets the player walk <b>underneath</b> the elevated upper flight of
///   stairs (rendered above the player at sortingOrder 25) without switching or teleporting floors.
/// </para>
/// </summary>
[RequireComponent(typeof(BoxCollider2D))]
public class StairwellZone : MonoBehaviour
{
    // ────────────────────────────── Runtime State ──────────────────────────

    private FactoryMapGenerator _mapGenerator;
    private int _lowerFloorLevel;
    private int _upperFloorLevel;
    private float _bottomLandingY;
    private float _topLandingY;
    private Light2D _stairLight;

    private bool _isLocalPlayerClimbing;

    private readonly List<SpriteRenderer> _elevatedUpperRenderers = new List<SpriteRenderer>();
    private readonly List<Collider2D> _upperHandrailColliders = new List<Collider2D>();
    private BoxCollider2D _underStairLowBulkheadCollider;
    private SpriteRenderer _underStairShadowRenderer;

    private static Sprite _cachedWhiteSprite;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Lower floor connected to the south (bottom) landing of this staircase.</summary>
    public int LowerFloorLevel => _lowerFloorLevel;

    /// <summary>Upper floor connected to the north (top) landing of this staircase.</summary>
    public int UpperFloorLevel => _upperFloorLevel;

    /// <summary>True when the local player entered via a landing and is actively walking on top of the stairs.</summary>
    public bool IsLocalPlayerClimbing => _isLocalPlayerClimbing;

    /// <summary>World position of the south (bottom) landing on <see cref="LowerFloorLevel"/>.</summary>
    public Vector2 BottomLandingWorldPos => new Vector2(transform.position.x, _bottomLandingY);

    /// <summary>World position of the north (top) landing on <see cref="UpperFloorLevel"/>.</summary>
    public Vector2 TopLandingWorldPos => new Vector2(transform.position.x, _topLandingY);

    /// <summary>
    /// Returns true if this staircase connects <paramref name="floorA"/> and <paramref name="floorB"/>.
    /// </summary>
    public bool ConnectsFloors(int floorA, int floorB)
    {
        return (_lowerFloorLevel == floorA && _upperFloorLevel == floorB) ||
               (_lowerFloorLevel == floorB && _upperFloorLevel == floorA);
    }

    /// <summary>
    /// Returns the entry landing world position for a player currently on <paramref name="currentFloor"/>.
    /// </summary>
    public Vector2 GetEntryLandingForFloor(int currentFloor)
    {
        return currentFloor == _lowerFloorLevel ? BottomLandingWorldPos : TopLandingWorldPos;
    }

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Builds the physical 4x10 staircase flight: bottom landing, 12 steps (lower ground-supported half +
    /// elevated upper half you can walk underneath on the lower floor), handrails, and trigger volume.
    /// </summary>
    public void Initialize(
        FactoryMapGenerator mapGenerator,
        int lowerFloorLevel,
        int upperFloorLevel,
        Vector2 worldCenter,
        bool opensToEastRing)
    {
        _mapGenerator = mapGenerator;
        _lowerFloorLevel = lowerFloorLevel;
        _upperFloorLevel = upperFloorLevel;
        _isLocalPlayerClimbing = false;
        _elevatedUpperRenderers.Clear();
        _upperHandrailColliders.Clear();

        EnsureSprite();

        transform.position = new Vector3(worldCenter.x, worldCenter.y, 0f);
        transform.localScale = Vector3.one;

        // Bottom landing is at y - 3.0, Top landing is at y + 3.0 (6.0 units of physical step climb)
        _bottomLandingY = worldCenter.y - 3.0f;
        _topLandingY = worldCenter.y + 3.0f;

        // Trigger volume spanning the bottom landing, 6-unit step flight, and top landing
        var trigger = GetComponent<BoxCollider2D>();
        trigger.isTrigger = true;
        trigger.size = new Vector2(3.8f, 8.8f);

        // 1. Lower half base housing (ground-supported portion on lower floor, y = -4.8 .. 0.0)
        var lowerBaseGo = new GameObject("StairwellBase_Lower");
        lowerBaseGo.transform.SetParent(transform, false);
        lowerBaseGo.transform.localPosition = new Vector3(0f, -2.4f, 0f);
        lowerBaseGo.transform.localScale = new Vector3(3.8f, 4.8f, 1f);
        var lowerBaseSr = lowerBaseGo.AddComponent<SpriteRenderer>();
        lowerBaseSr.sprite = _cachedWhiteSprite;
        lowerBaseSr.color = new Color(0.20f, 0.22f, 0.24f, 1f);
        lowerBaseSr.sortingOrder = -3;

        // 2. Under-stair ground shadow under the elevated upper half (y = 0.0 .. +4.8) so walking underneath feels physical
        var shadowGo = new GameObject("UnderStairShadow");
        shadowGo.transform.SetParent(transform, false);
        shadowGo.transform.localPosition = new Vector3(0f, 2.4f, 0f);
        shadowGo.transform.localScale = new Vector3(3.8f, 4.8f, 1f);
        _underStairShadowRenderer = shadowGo.AddComponent<SpriteRenderer>();
        _underStairShadowRenderer.sprite = _cachedWhiteSprite;
        _underStairShadowRenderer.color = new Color(0.05f, 0.06f, 0.07f, 0.55f);
        _underStairShadowRenderer.sortingOrder = -3;

        // 3. Bottom & Top Landing platforms
        CreateLandingPlatform("BottomLandingPad", new Vector2(0f, -3.8f), new Color(0.24f, 0.28f, 0.25f, 1f), isElevatedUpper: false);
        CreateLandingPlatform("TopLandingPad", new Vector2(0f, 3.8f), new Color(0.28f, 0.26f, 0.23f, 1f), isElevatedUpper: true);

        // 4. 12 physical concrete step treads from y = -2.65 to y = +2.65
        //    Steps 0..5 (lower half) sit on the ground; Steps 6..11 (further/upper half) are elevated overhead!
        const int stepCount = 12;
        for (int s = 0; s < stepCount; s++)
        {
            float t = s / (float)(stepCount - 1);
            float localY = Mathf.Lerp(-2.65f, 2.65f, t);
            bool isElevatedUpperStep = s >= 6;

            var stepGo = new GameObject($"StairStep_{s + 1}");
            stepGo.transform.SetParent(transform, false);
            stepGo.transform.localPosition = new Vector3(0f, localY, 0f);
            stepGo.transform.localScale = new Vector3(3.3f, 0.42f, 1f);

            var stepSr = stepGo.AddComponent<SpriteRenderer>();
            stepSr.sprite = _cachedWhiteSprite;
            float shade = Mathf.Lerp(0.26f, 0.52f, t);
            stepSr.color = new Color(shade, shade + 0.01f, shade + 0.02f, 1f);
            stepSr.sortingOrder = -2;

            if (isElevatedUpperStep)
            {
                _elevatedUpperRenderers.Add(stepSr);
            }
        }

        // 5. Split Handrails into Lower Ground Handrails (always solid at the base of the stairs)
        //    and Upper Elevated Handrails (only solid while the player is climbing ON TOP of the stairs,
        //    so players on the lower floor can walk freely UNDERNEATH the elevated upper half!).
        CreateHandrail("Handrail_LowerLeft", new Vector2(-1.82f, -1.45f), new Vector2(0.26f, 2.5f), isElevatedUpper: false);
        CreateHandrail("Handrail_LowerRight", new Vector2(1.82f, -1.45f), new Vector2(0.26f, 2.5f), isElevatedUpper: false);

        CreateHandrail("Handrail_UpperLeft", new Vector2(-1.82f, 1.45f), new Vector2(0.26f, 2.8f), isElevatedUpper: true);
        CreateHandrail("Handrail_UpperRight", new Vector2(1.82f, 1.45f), new Vector2(0.26f, 2.8f), isElevatedUpper: true);

        // 5b. Low-clearance under-stair bulkhead divider at y = -0.10 (active ONLY when walking underneath on the lower floor,
        //     blocking a player underneath the upper half from walking south through the solid underside of the low steps)
        var bulkheadGo = new GameObject("UnderStairLowBulkhead");
        bulkheadGo.transform.SetParent(transform, false);
        bulkheadGo.transform.localPosition = new Vector3(0f, -0.10f, 0f);
        _underStairLowBulkheadCollider = bulkheadGo.AddComponent<BoxCollider2D>();
        _underStairLowBulkheadCollider.isTrigger = false;
        _underStairLowBulkheadCollider.size = new Vector2(3.4f, 0.24f);

        // 6. Overhead stairwell light beacon
        var lightGo = new GameObject("StairwellBeaconLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.zero;
        _stairLight = lightGo.AddComponent<Light2D>();
        _stairLight.lightType = Light2D.LightType.Point;
        _stairLight.color = new Color(0.75f, 0.88f, 0.78f, 1f);
        _stairLight.intensity = 0.85f;
        _stairLight.pointLightOuterRadius = 7.5f;
        _stairLight.pointLightInnerRadius = 1.0f;

        ApplyElevationVisualAndColliderMode();
    }

    private void CreateLandingPlatform(string name, Vector2 localPos, Color color, bool isElevatedUpper)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        go.transform.localScale = new Vector3(3.6f, 2.1f, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedWhiteSprite;
        sr.color = color;
        sr.sortingOrder = -2;

        if (isElevatedUpper)
        {
            _elevatedUpperRenderers.Add(sr);
        }
    }

    private void CreateHandrail(string name, Vector2 localPos, Vector2 size, bool isElevatedUpper)
    {
        var railGo = new GameObject(name);
        railGo.transform.SetParent(transform, false);
        railGo.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        railGo.transform.localScale = new Vector3(size.x, size.y, 1f);

        var sr = railGo.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedWhiteSprite;
        sr.color = new Color(0.42f, 0.45f, 0.48f, 1f);
        sr.sortingOrder = 9;

        var box = railGo.AddComponent<BoxCollider2D>();
        box.isTrigger = false;
        box.size = Vector2.one;

        if (isElevatedUpper)
        {
            _elevatedUpperRenderers.Add(sr);
            _upperHandrailColliders.Add(box);
        }
    }

    /// <summary>
    /// Updates the sorting order and collider state of the elevated upper half of the staircase:
    /// - When the local player is on <see cref="LowerFloorLevel"/> and NOT climbing on top of the stairs,
    ///   the further/upper half of the staircase is elevated overhead (<c>sortingOrder = 25</c>, above the player)
    ///   and its upper handrail colliders are disabled so the player can walk <b>underneath</b> the stairs!
    /// - When the local player IS climbing on top of the stairs (or is on <see cref="UpperFloorLevel"/>),
    ///   the steps render beneath the player (<c>sortingOrder = -2</c>) and the upper handrails guide the player.
    /// </summary>
    private void ApplyElevationVisualAndColliderMode()
    {
        int curFloor = _mapGenerator != null ? _mapGenerator.CurrentLocalFloorLevel : 0;
        bool walkUnderneathMode = (curFloor == _lowerFloorLevel) && !_isLocalPlayerClimbing;

        if (_underStairLowBulkheadCollider != null)
        {
            _underStairLowBulkheadCollider.enabled = walkUnderneathMode;
        }

        for (int i = 0; i < _upperHandrailColliders.Count; i++)
        {
            if (_upperHandrailColliders[i] != null)
            {
                _upperHandrailColliders[i].enabled = !walkUnderneathMode;
            }
        }

        for (int i = 0; i < _elevatedUpperRenderers.Count; i++)
        {
            SpriteRenderer sr = _elevatedUpperRenderers[i];
            if (sr == null) continue;

            Color c = sr.color;
            if (walkUnderneathMode)
            {
                // Render elevated upper half ABOVE player body (sortingOrder 10) and weapon (12)
                sr.sortingOrder = 25;
                c.a = 0.84f; // Slight translucency so player can see their silhouette passing underneath
            }
            else
            {
                sr.sortingOrder = sr.gameObject.name.StartsWith("Handrail") ? 9 : -2;
                c.a = 1.0f;
            }
            sr.color = c;
        }
    }

    // ──────────────────────────── Continuous Climb Tracking ────────────────

    /// <summary>
    /// Computes normalized climb progress (0 at bottom landing, 1 at top landing) for a world Y position.
    /// </summary>
    public float ComputeClimbProgress(float worldY)
    {
        return Mathf.Clamp01(Mathf.InverseLerp(_bottomLandingY, _topLandingY, worldY));
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        HandlePlayerInStairwellVolume(other);
    }

    private void OnTriggerStay2D(Collider2D other)
    {
        HandlePlayerInStairwellVolume(other);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        if (other == null || _mapGenerator == null) return;

        var player = other.GetComponent<PlayerController>();
        if (player == null || !player.HasInputAuthority) return;

        if (!_isLocalPlayerClimbing)
        {
            ApplyElevationVisualAndColliderMode();
            return;
        }

        _isLocalPlayerClimbing = false;
        float t = ComputeClimbProgress(player.transform.position.y);
        int finalFloor = t >= 0.5f ? _upperFloorLevel : _lowerFloorLevel;
        _mapGenerator.CommitToSingleFloor(finalFloor);
        ApplyElevationVisualAndColliderMode();
    }

    private void HandlePlayerInStairwellVolume(Collider2D other)
    {
        if (other == null || _mapGenerator == null) return;

        var player = other.GetComponent<PlayerController>();
        if (player == null || !player.HasInputAuthority) return;

        int curFloor = _mapGenerator.CurrentLocalFloorLevel;
        if (curFloor != _lowerFloorLevel && curFloor != _upperFloorLevel) return;

        float t = ComputeClimbProgress(player.transform.position.y);
        float localX = Mathf.Abs(player.transform.position.x - transform.position.x);

        if (!_isLocalPlayerClimbing)
        {
            // Player can ONLY begin climbing ON TOP of the stairs by entering through:
            // - the Bottom Landing (t <= 0.28) when on LowerFloorLevel, OR
            // - the Top Landing (t >= 0.72) when on UpperFloorLevel!
            // Walking across the further/elevated half (t > 0.28) while on LowerFloorLevel walks UNDERNEATH the stairs!
            bool enteredBottomMouthFromLower = (curFloor == _lowerFloorLevel && t <= 0.28f && localX <= 1.65f);
            bool enteredTopMouthFromUpper = (curFloor == _upperFloorLevel && t >= 0.72f && localX <= 1.65f);

            if (enteredBottomMouthFromLower || enteredTopMouthFromUpper)
            {
                _isLocalPlayerClimbing = true;
                ApplyElevationVisualAndColliderMode();
            }
            else
            {
                // Walking underneath the elevated upper half on LowerFloorLevel — do NOT switch floors!
                ApplyElevationVisualAndColliderMode();
                return;
            }
        }

        // Player is actively climbing/descending on top of the stairs: smoothly cross-fade 0% to 100%
        _mapGenerator.SetStaircaseCrossFade(_lowerFloorLevel, _upperFloorLevel, t);
    }

    // ──────────────────────────── Visibility & HUD ─────────────────────────

    /// <summary>
    /// Updates whether this staircase is visible based on the currently visible floor(s).
    /// </summary>
    public void UpdateVisibilityForFloors(int activeFloor, int secondaryFloor, float secondaryAlpha)
    {
        bool relevantToActive = (activeFloor == _lowerFloorLevel || activeFloor == _upperFloorLevel);
        bool relevantToSecondary = secondaryAlpha > 0.01f && (secondaryFloor == _lowerFloorLevel || secondaryFloor == _upperFloorLevel);
        bool visible = relevantToActive || relevantToSecondary;

        if (gameObject.activeSelf != visible)
        {
            gameObject.SetActive(visible);
        }

        if (visible)
        {
            ApplyElevationVisualAndColliderMode();
        }
    }

    private void OnGUI()
    {
        if (Camera.main == null || _mapGenerator == null) return;

        int curFloor = _mapGenerator.CurrentLocalFloorLevel;
        if (curFloor != _lowerFloorLevel && curFloor != _upperFloorLevel) return;

        bool onLower = curFloor == _lowerFloorLevel;
        Vector2 landingPos = onLower ? BottomLandingWorldPos : TopLandingWorldPos;
        string label = onLower
            ? $"STAIRS UP -> {FactoryMapGenerator.GetFloorDisplayName(_upperFloorLevel)}"
            : $"STAIRS DOWN -> {FactoryMapGenerator.GetFloorDisplayName(_lowerFloorLevel)}";

        Vector3 vp = Camera.main.WorldToViewportPoint(landingPos);
        if (vp.z <= 0f || vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f) return;

        Vector3 sp = Camera.main.WorldToScreenPoint(landingPos);
        float guiY = Screen.height - sp.y;
        GUI.Box(new Rect(sp.x - 85f, guiY - 12f, 170f, 22f), label);
    }

    private static void EnsureSprite()
    {
        if (_cachedWhiteSprite != null) return;

        const int size = 8;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();

        _cachedWhiteSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
