using UnityEngine;

/// <summary>
/// Industrial factory window that blocks player movement at all times while allowing
/// sight and light through. Shatters on the first gunshot so bullets pass through freely.
/// </summary>
[RequireComponent(typeof(BoxCollider2D))]
public class BreakableWindow : MonoBehaviour
{
    // ────────────────────────────── Runtime State ──────────────────────────

    private int _windowIndex;
    private FactoryMapGenerator _mapGenerator;
    private BoxCollider2D _movementBlockerCollider;
    private SpriteRenderer _glassRenderer;
    private SpriteRenderer _sillRenderer;
    private Color _wallColor = new Color(0.36f, 0.37f, 0.38f, 1f);
    private bool _isBroken;

    private static Sprite _cachedWhiteSprite;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Unique index of this window in <see cref="FactoryMapGenerator"/> for network sync.</summary>
    public int WindowIndex => _windowIndex;

    /// <summary>True once the glass pane has been shattered by gunfire.</summary>
    public bool IsBroken => _isBroken;

    /// <summary>Gets the sprite renderer for the window sill / wall piece that remains after shattering.</summary>
    public SpriteRenderer SillRenderer => _sillRenderer;

    /// <summary>Gets the sprite renderer for the glass pane.</summary>
    public SpriteRenderer GlassRenderer => _glassRenderer;

    /// <summary>Gets the wall color assigned to the leftover window sill piece.</summary>
    public Color WallColor => _wallColor;

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Initializes the window frame, glass pane visual, and movement-blocking collider.
    /// </summary>
    public void Initialize(
        int windowIndex,
        FactoryMapGenerator mapGenerator,
        Vector2 worldCenter,
        Vector2 size,
        bool horizontal,
        Color? wallColor = null)
    {
        _windowIndex = windowIndex;
        _mapGenerator = mapGenerator;
        _isBroken = false;

        _wallColor = wallColor ?? (mapGenerator != null && mapGenerator.Config != null
            ? mapGenerator.Config.WallColor
            : new Color(0.36f, 0.37f, 0.38f, 1f));

        EnsureSprite();

        transform.position = new Vector3(worldCenter.x, worldCenter.y, 0f);
        transform.localScale = Vector3.one;

        // Solid collider blocks player/dummy movement even after glass shatters
        _movementBlockerCollider = GetComponent<BoxCollider2D>();
        _movementBlockerCollider.isTrigger = false;
        _movementBlockerCollider.size = size;

        // 1. Window sill / remaining wall piece matching wall color
        var sillGo = new GameObject("WindowSill");
        sillGo.transform.SetParent(transform, false);
        sillGo.transform.localPosition = Vector3.zero;
        sillGo.transform.localScale = horizontal
            ? new Vector3(size.x, 0.42f, 1f)
            : new Vector3(0.42f, size.y, 1f);

        _sillRenderer = sillGo.AddComponent<SpriteRenderer>();
        _sillRenderer.sprite = _cachedWhiteSprite;
        _sillRenderer.color = _wallColor;
        _sillRenderer.sortingOrder = 7;

        // 2. Translucent pale-cyan industrial glass pane
        var glassGo = new GameObject("GlassPane");
        glassGo.transform.SetParent(transform, false);
        glassGo.transform.localPosition = Vector3.zero;
        glassGo.transform.localScale = horizontal
            ? new Vector3(size.x - 0.12f, 0.22f, 1f)
            : new Vector3(0.22f, size.y - 0.12f, 1f);

        _glassRenderer = glassGo.AddComponent<SpriteRenderer>();
        _glassRenderer.sprite = _cachedWhiteSprite;
        _glassRenderer.color = new Color(0.58f, 0.85f, 0.95f, 0.55f);
        _glassRenderer.sortingOrder = 8;
    }

    /// <summary>
    /// Shatters the window glass from a gunshot and synchronizes across the network.
    /// </summary>
    public void ShatterFromShot(Vector2 hitPoint)
    {
        if (_isBroken) return;

        ApplyShatteredState(hitPoint);

        if (_mapGenerator != null)
        {
            _mapGenerator.NotifyWindowShattered(_windowIndex, hitPoint);
        }
    }

    /// <summary>
    /// Applies the shattered visual state locally (called on gunshot or via network RPC).
    /// </summary>
    public void ApplyShatteredState(Vector2 hitPoint)
    {
        if (_isBroken) return;
        _isBroken = true;

        if (_glassRenderer != null)
        {
            _glassRenderer.enabled = false;
        }

        if (_sillRenderer != null)
        {
            _sillRenderer.color = _wallColor;
        }

        SpawnGlassShardBurst(hitPoint);
    }

    private void SpawnGlassShardBurst(Vector2 hitPoint)
    {
        EnsureSprite();
        int shardCount = 8;
        for (int i = 0; i < shardCount; i++)
        {
            var shardGo = new GameObject("GlassShard");
            Vector2 offset = Random.insideUnitCircle * 0.45f;
            shardGo.transform.position = new Vector3(hitPoint.x + offset.x, hitPoint.y + offset.y, 0f);
            shardGo.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            shardGo.transform.localScale = new Vector3(Random.Range(0.08f, 0.18f), Random.Range(0.05f, 0.12f), 1f);

            var sr = shardGo.AddComponent<SpriteRenderer>();
            sr.sprite = _cachedWhiteSprite;
            sr.color = new Color(0.72f, 0.92f, 1.0f, 0.65f);
            sr.sortingOrder = 3;

            Destroy(shardGo, 8f);
        }
    }

    private static void EnsureSprite()
    {
        if (_cachedWhiteSprite != null) return;

        const int size = 8;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();

        _cachedWhiteSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
