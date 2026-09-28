using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Operating state of a hallway emergency light fixture.
/// </summary>
public enum HallwayLightMode
{
    /// <summary>Light stays steadily on.</summary>
    Steady = 0,

    /// <summary>Light flickers and buzzes intermittently.</summary>
    Flickering = 1,

    /// <summary>Light is dead/broken (dark corridor stretch).</summary>
    Broken = 2
}

/// <summary>
/// Controls a 2D emergency light fixture in a factory hallway.
/// Supports steady illumination, deterministic fluorescent flickering, or broken/off state.
/// </summary>
public class HallwayLight : MonoBehaviour
{
    // ────────────────────────────── Runtime Config ─────────────────────────

    private HallwayLightMode _mode = HallwayLightMode.Steady;
    private float _baseIntensity = 1.15f;
    private float _outerRadius = 5.5f;
    private Color _lightColor = new Color(0.88f, 0.74f, 0.42f, 1f);
    private float _noiseSeed;
    private float _flickerSpeed = 9f;
    private float _floorVisibilityAlpha = 1f;
    private float _lastFlickerMultiplier = 1f;

    private Light2D _light2D;
    private SpriteRenderer _bulbRenderer;

    private static Sprite _cachedBulbSprite;

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Initializes the hallway light fixture with its deterministic mode, intensity, radius, and color.
    /// </summary>
    public void Initialize(
        HallwayLightMode mode,
        float baseIntensity,
        float outerRadius,
        Color lightColor,
        float noiseSeed)
    {
        _mode = mode;
        _baseIntensity = baseIntensity;
        _outerRadius = outerRadius;
        _lightColor = lightColor;
        _noiseSeed = noiseSeed;
        _flickerSpeed = 7f + (Mathf.Abs(noiseSeed) % 6f);
        _floorVisibilityAlpha = 1f;
        _lastFlickerMultiplier = 1f;

        SetupComponents();
        ApplyState(1f);
    }

    /// <summary>
    /// Sets the floor cross-fade visibility weight (0–1) for this light when transitioning along stairs.
    /// </summary>
    public void SetFloorVisibilityAlpha(float alpha)
    {
        _floorVisibilityAlpha = Mathf.Clamp01(alpha);
        ApplyState(_lastFlickerMultiplier);
    }

    private void SetupComponents()
    {
        if (_cachedBulbSprite == null)
        {
            const int size = 16;
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
            _cachedBulbSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        _bulbRenderer = GetComponent<SpriteRenderer>();
        if (_bulbRenderer == null)
        {
            _bulbRenderer = gameObject.AddComponent<SpriteRenderer>();
        }
        _bulbRenderer.sprite = _cachedBulbSprite;
        _bulbRenderer.sortingOrder = 18;
        transform.localScale = new Vector3(0.22f, 0.22f, 1f);

        _light2D = GetComponent<Light2D>();
        if (_light2D == null)
        {
            _light2D = gameObject.AddComponent<Light2D>();
        }
        _light2D.lightType = Light2D.LightType.Point;
        _light2D.color = _lightColor;
        _light2D.pointLightOuterRadius = _outerRadius;
        _light2D.pointLightInnerRadius = _outerRadius * 0.15f;
        _light2D.falloffIntensity = 0.65f;
    }

    private void Update()
    {
        if (_mode != HallwayLightMode.Flickering) return;

        // Deterministic fluorescent tube stutter using layered Perlin noise
        float n1 = Mathf.PerlinNoise(Time.time * _flickerSpeed, _noiseSeed);
        float n2 = Mathf.PerlinNoise(Time.time * (_flickerSpeed * 2.7f), _noiseSeed + 17.3f);
        float combined = (n1 * 0.65f) + (n2 * 0.35f);

        // Occasional sharp dropout when noise dips below threshold
        float multiplier = combined < 0.36f
            ? Mathf.Lerp(0.04f, 0.25f, combined / 0.36f)
            : Mathf.Lerp(0.65f, 1.1f, (combined - 0.36f) / 0.64f);

        ApplyState(multiplier);
    }

    private void ApplyState(float intensityMultiplier)
    {
        _lastFlickerMultiplier = intensityMultiplier;
        if (_light2D == null || _bulbRenderer == null) return;

        if (_mode == HallwayLightMode.Broken || _floorVisibilityAlpha <= 0.005f)
        {
            _light2D.enabled = false;
            _bulbRenderer.color = new Color(0.18f, 0.18f, 0.18f, 0.6f * _floorVisibilityAlpha);
            return;
        }

        _light2D.enabled = true;
        _light2D.intensity = _baseIntensity * intensityMultiplier * _floorVisibilityAlpha;

        float bulbAlpha = Mathf.Clamp(0.25f + 0.7f * intensityMultiplier, 0.15f, 0.95f) * _floorVisibilityAlpha;
        _bulbRenderer.color = new Color(_lightColor.r, _lightColor.g, _lightColor.b, bulbAlpha);
    }
}
