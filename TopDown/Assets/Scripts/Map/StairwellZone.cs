using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Physical 2D walkable staircase flight connecting a <see cref="LowerFloorLevel"/> (at its bottom landing)
/// and an <see cref="UpperFloorLevel"/> (at its top landing).
/// <para>
/// - Entering from the <b>Bottom Landing</b> (on the lower floor) or <b>Top Landing</b> (on the upper floor)
///   puts the player on top of the stairs (<see cref="IsLocalPlayerClimbing"/> = true), smoothly
///   cross-fading floors from 0% to 100% as the player walks along the steps.
/// - The sides of the staircase that the player is not allowed to enter are enclosed by solid walls
///   on the respective floors, so only the valid entrance landing is accessible.
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
    /// Builds the physical 4x10 staircase flight: bottom landing, 12 steps, continuous solid handrails,
    /// and continuous climb trigger volume.
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

        // 1. Solid base housing underneath the full stair flight (y = -4.8 .. +4.8)
        var baseGo = new GameObject("StairwellBase");
        baseGo.transform.SetParent(transform, false);
        baseGo.transform.localPosition = Vector3.zero;
        baseGo.transform.localScale = new Vector3(3.8f, 9.6f, 1f);
        var baseSr = baseGo.AddComponent<SpriteRenderer>();
        baseSr.sprite = _cachedWhiteSprite;
        baseSr.color = new Color(0.20f, 0.22f, 0.24f, 1f);
        baseSr.sortingOrder = -3;

        // 2. Bottom & Top Landing platforms
        CreateLandingPlatform("BottomLandingPad", new Vector2(0f, -3.8f), new Color(0.24f, 0.28f, 0.25f, 1f));
        CreateLandingPlatform("TopLandingPad", new Vector2(0f, 3.8f), new Color(0.28f, 0.26f, 0.23f, 1f));

        // 3. 12 physical concrete step treads from y = -2.65 to y = +2.65
        const int stepCount = 12;
        for (int s = 0; s < stepCount; s++)
        {
            float t = s / (float)(stepCount - 1);
            float localY = Mathf.Lerp(-2.65f, 2.65f, t);

            var stepGo = new GameObject($"StairStep_{s + 1}");
            stepGo.transform.SetParent(transform, false);
            stepGo.transform.localPosition = new Vector3(0f, localY, 0f);
            stepGo.transform.localScale = new Vector3(3.3f, 0.42f, 1f);

            var stepSr = stepGo.AddComponent<SpriteRenderer>();
            stepSr.sprite = _cachedWhiteSprite;
            float shade = Mathf.Lerp(0.26f, 0.52f, t);
            stepSr.color = new Color(shade, shade + 0.01f, shade + 0.02f, 1f);
            stepSr.sortingOrder = -2;
        }

        // 4. Continuous solid handrails on both sides of the staircase
        CreateHandrail("Handrail_Left", new Vector2(-1.82f, 0f), new Vector2(0.26f, 5.6f));
        CreateHandrail("Handrail_Right", new Vector2(1.82f, 0f), new Vector2(0.26f, 5.6f));

        // 5. Overhead stairwell light beacon
        var lightGo = new GameObject("StairwellBeaconLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.zero;
        _stairLight = lightGo.AddComponent<Light2D>();
        _stairLight.lightType = Light2D.LightType.Point;
        _stairLight.color = new Color(0.75f, 0.88f, 0.78f, 1f);
        _stairLight.intensity = 0.85f;
        _stairLight.pointLightOuterRadius = 7.5f;
        _stairLight.pointLightInnerRadius = 1.0f;
    }

    private void CreateLandingPlatform(string name, Vector2 localPos, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        go.transform.localScale = new Vector3(3.6f, 2.1f, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedWhiteSprite;
        sr.color = color;
        sr.sortingOrder = -2;
    }

    private void CreateHandrail(string name, Vector2 localPos, Vector2 size)
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

        if (!_isLocalPlayerClimbing) return;

        _isLocalPlayerClimbing = false;
        float t = ComputeClimbProgress(player.transform.position.y);
        int finalFloor = t >= 0.5f ? _upperFloorLevel : _lowerFloorLevel;
        _mapGenerator.CommitToSingleFloor(finalFloor);
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
            // Player can ONLY begin climbing by entering through:
            // - the Bottom Landing (t <= 0.28) when on LowerFloorLevel, OR
            // - the Top Landing (t >= 0.72) when on UpperFloorLevel!
            bool enteredBottomMouthFromLower = (curFloor == _lowerFloorLevel && t <= 0.28f && localX <= 1.65f);
            bool enteredTopMouthFromUpper = (curFloor == _upperFloorLevel && t >= 0.72f && localX <= 1.65f);

            if (enteredBottomMouthFromLower || enteredTopMouthFromUpper)
            {
                _isLocalPlayerClimbing = true;
            }
            else
            {
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
