using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Walk-on stairwell zone that transitions players between adjacent factory floors
/// (Basement, 1st Floor, 2nd Floor, 3rd Floor) when stepped on.
/// </summary>
[RequireComponent(typeof(BoxCollider2D))]
public class StairwellZone : MonoBehaviour
{
    // ────────────────────────────── Runtime State ──────────────────────────

    private FactoryMapGenerator _mapGenerator;
    private int _sourceFloorLevel;
    private int _targetFloorLevel;
    private Vector2 _targetArrivalWorldPos;
    private string _labelText;

    private static readonly Dictionary<PlayerController, float> _lastTransitionTimeByPlayer = new Dictionary<PlayerController, float>();
    private static Sprite _cachedStepSprite;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Floor level this stairwell is located on (-1=Basement, 0=1st, 1=2nd, 2=3rd).</summary>
    public int SourceFloorLevel => _sourceFloorLevel;

    /// <summary>Destination floor level this stairwell leads to.</summary>
    public int TargetFloorLevel => _targetFloorLevel;

    /// <summary>World position where a player arrives on the destination floor.</summary>
    public Vector2 TargetArrivalWorldPosition => _targetArrivalWorldPos;

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Configures the stairwell trigger pad, visual steps, and destination floor coordinates.
    /// </summary>
    public void Initialize(
        FactoryMapGenerator mapGenerator,
        int sourceFloorLevel,
        int targetFloorLevel,
        Vector2 worldPos,
        Vector2 targetArrivalWorldPos,
        string labelText)
    {
        _mapGenerator = mapGenerator;
        _sourceFloorLevel = sourceFloorLevel;
        _targetFloorLevel = targetFloorLevel;
        _targetArrivalWorldPos = targetArrivalWorldPos;
        _labelText = labelText;

        EnsureStepSprite();

        transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
        transform.localScale = Vector3.one;

        var trigger = GetComponent<BoxCollider2D>();
        trigger.isTrigger = true;
        trigger.size = new Vector2(2.6f, 2.6f);

        bool goingUp = targetFloorLevel > sourceFloorLevel;
        Color padColor = goingUp
            ? new Color(0.24f, 0.32f, 0.26f, 0.95f)
            : new Color(0.32f, 0.25f, 0.22f, 0.95f);

        // Outer stairwell concrete housing
        var baseGo = new GameObject("StairwellBase");
        baseGo.transform.SetParent(transform, false);
        baseGo.transform.localPosition = Vector3.zero;
        baseGo.transform.localScale = new Vector3(2.8f, 2.8f, 1f);

        var baseSr = baseGo.AddComponent<SpriteRenderer>();
        baseSr.sprite = _cachedStepSprite;
        baseSr.color = padColor;
        baseSr.sortingOrder = -2;

        // Visual step treads
        for (int s = -2; s <= 2; s++)
        {
            var stepGo = new GameObject($"Step_{s + 3}");
            stepGo.transform.SetParent(transform, false);
            stepGo.transform.localPosition = new Vector3(s * 0.48f, 0f, 0f);
            stepGo.transform.localScale = new Vector3(0.32f, 2.2f, 1f);

            var stepSr = stepGo.AddComponent<SpriteRenderer>();
            stepSr.sprite = _cachedStepSprite;
            float shade = goingUp ? (0.35f + (s + 2) * 0.08f) : (0.67f - (s + 2) * 0.08f);
            stepSr.color = new Color(shade, shade, shade + 0.03f, 1f);
            stepSr.sortingOrder = -1;
        }

        // Subtle stairwell beacon light so players can spot staircases in dark corridors
        var lightGo = new GameObject("StairwellLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.zero;
        var light2D = lightGo.AddComponent<Light2D>();
        light2D.lightType = Light2D.LightType.Point;
        light2D.color = goingUp
            ? new Color(0.55f, 0.90f, 0.65f, 1f)
            : new Color(0.95f, 0.65f, 0.40f, 1f);
        light2D.intensity = 0.75f;
        light2D.pointLightOuterRadius = 4.5f;
        light2D.pointLightInnerRadius = 0.5f;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        TryTransitionCharacter(other);
    }

    private void OnTriggerStay2D(Collider2D other)
    {
        TryTransitionCharacter(other);
    }

    private void TryTransitionCharacter(Collider2D other)
    {
        if (other == null) return;

        var player = other.GetComponent<PlayerController>();
        if (player == null) return;

        if (_lastTransitionTimeByPlayer.TryGetValue(player, out float lastTime) &&
            Time.time - lastTime < 0.85f)
        {
            return;
        }

        _lastTransitionTimeByPlayer[player] = Time.time;

        Vector2 delta = _targetArrivalWorldPos - (Vector2)player.transform.position;
        player.TeleportTo(_targetArrivalWorldPos);

        if (player.HasInputAuthority)
        {
            if (Camera.main != null)
            {
                Camera.main.transform.position += new Vector3(delta.x, delta.y, 0f);
            }

            if (_mapGenerator != null)
            {
                _mapGenerator.OnLocalPlayerChangedFloor(_targetFloorLevel);
            }
        }
    }

    private void OnGUI()
    {
        if (Camera.main == null || string.IsNullOrEmpty(_labelText)) return;

        Vector3 vp = Camera.main.WorldToViewportPoint(transform.position);
        if (vp.z <= 0f || vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f) return;

        Vector3 sp = Camera.main.WorldToScreenPoint(transform.position + new Vector3(0f, 1.75f, 0f));
        float guiY = Screen.height - sp.y;
        GUI.Box(new Rect(sp.x - 75f, guiY - 12f, 150f, 22f), _labelText);
    }

    private static void EnsureStepSprite()
    {
        if (_cachedStepSprite != null) return;

        const int size = 8;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();

        _cachedStepSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
