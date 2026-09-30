using UnityEngine;

/// <summary>
/// Interactive procedural doorway door that swings open around its hinge when physically
/// pushed by a player's body collider or their equipped weapon barrel (<c>WeaponVisual</c>).
/// Synchronizes its swing angle across multiplayer via <see cref="FactoryMapGenerator"/>.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
public class SwingDoor : MonoBehaviour
{
    // ────────────────────────────── Configuration ──────────────────────────

    private int _doorIndex;
    private FactoryMapGenerator _mapGenerator;
    private Vector2 _hingeWorldPos;
    private float _closedAngleDeg;
    private float _doorLength = 1.75f;
    private float _doorThickness = 0.18f;
    private float _maxOpenAngle = 98f;

    // ────────────────────────────── Runtime State ──────────────────────────

    private float _currentOffsetAngle;
    private float _angularVelocity;
    private float _lastSyncedAngle;
    private float _nextNetSyncTime;

    private Rigidbody2D _rb;
    private BoxCollider2D _boxCollider;
    private SpriteRenderer _leafRenderer;

    private static Sprite _cachedDoorSprite;
    private static readonly Collider2D[] OverlapBuffer = new Collider2D[16];

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Deterministic index of this door in the generated factory.</summary>
    public int DoorIndex => _doorIndex;

    /// <summary>Current swing offset angle in degrees relative to the closed angle (-98 to +98).</summary>
    public float CurrentOffsetAngle => _currentOffsetAngle;

    /// <summary>World length of this door leaf from hinge to tip.</summary>
    public float DoorLength => _doorLength;

    /// <summary>World position of this door's hinge pivot.</summary>
    public Vector2 HingeWorldPos => _hingeWorldPos;

    /// <summary>Closed resting angle in degrees.</summary>
    public float ClosedAngleDeg => _closedAngleDeg;

    // ──────────────────────────── Initialization ───────────────────────────

    /// <summary>
    /// Initializes the swinging door at the given hinge position and closed orientation.
    /// </summary>
    public void Initialize(
        int doorIndex,
        FactoryMapGenerator generator,
        Vector2 hingeWorldPos,
        float closedAngleDeg,
        float doorLength,
        Color doorColor)
    {
        _doorIndex = doorIndex;
        _mapGenerator = generator;
        _hingeWorldPos = hingeWorldPos;
        _closedAngleDeg = closedAngleDeg;
        _doorLength = doorLength;
        _currentOffsetAngle = 0f;
        _angularVelocity = 0f;

        SetupComponents(doorColor);
        ApplyTransformImmediate();
    }

    private void SetupComponents(Color doorColor)
    {
        if (_cachedDoorSprite == null)
        {
            const int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();

            // Pivot at (0, 0.5) so the door rotates cleanly around its hinge edge!
            _cachedDoorSprite = Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0f, 0.5f),
                size);
        }

        _rb = GetComponent<Rigidbody2D>();
        _rb.bodyType = RigidbodyType2D.Kinematic;
        _rb.useFullKinematicContacts = true;
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        _boxCollider = GetComponent<BoxCollider2D>();
        _boxCollider.isTrigger = false;
        _boxCollider.size = new Vector2(_doorLength, _doorThickness);
        _boxCollider.offset = new Vector2(_doorLength * 0.5f, 0f);

        // Visual child scaled to match the door panel from the hinge pivot
        var leafTrans = transform.Find("DoorLeaf");
        if (leafTrans == null)
        {
            var leafGo = new GameObject("DoorLeaf");
            leafGo.transform.SetParent(transform, false);
            leafTrans = leafGo.transform;
        }

        _leafRenderer = leafTrans.GetComponent<SpriteRenderer>();
        if (_leafRenderer == null)
        {
            _leafRenderer = leafTrans.gameObject.AddComponent<SpriteRenderer>();
        }
        _leafRenderer.sprite = _cachedDoorSprite;
        _leafRenderer.color = doorColor;
        _leafRenderer.sortingOrder = 9;
        leafTrans.localPosition = Vector3.zero;
        leafTrans.localScale = new Vector3(_doorLength, _doorThickness, 1f);

        // Small hinge pin visual
        var pinTrans = transform.Find("HingePin");
        if (pinTrans == null)
        {
            var pinGo = new GameObject("HingePin");
            pinGo.transform.SetParent(transform, false);
            pinTrans = pinGo.transform;
            var pinSr = pinGo.AddComponent<SpriteRenderer>();
            pinSr.sprite = _cachedDoorSprite;
            pinSr.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            pinSr.sortingOrder = 10;
            pinTrans.localPosition = new Vector3(-0.06f, 0f, 0f);
            pinTrans.localScale = new Vector3(0.14f, 0.24f, 1f);
        }
    }

    // ──────────────────────────── Physics & Push Logic ─────────────────────

    private void FixedUpdate()
    {
        DetectPlayerAndWeaponPush();

        if (Mathf.Abs(_angularVelocity) > 0.01f)
        {
            _currentOffsetAngle += _angularVelocity * Time.fixedDeltaTime;

            // Clamp at physical door frame stops
            if (_currentOffsetAngle > _maxOpenAngle)
            {
                _currentOffsetAngle = _maxOpenAngle;
                _angularVelocity = 0f;
            }
            else if (_currentOffsetAngle < -_maxOpenAngle)
            {
                _currentOffsetAngle = -_maxOpenAngle;
                _angularVelocity = 0f;
            }

            // Smooth hinge friction / damping
            _angularVelocity = Mathf.MoveTowards(_angularVelocity, 0f, 420f * Time.fixedDeltaTime);

            float worldAngle = _closedAngleDeg + _currentOffsetAngle;
            _rb.MoveRotation(worldAngle);
        }

        // Broadcast angle change across network if significant
        if (_mapGenerator != null &&
            Time.time >= _nextNetSyncTime &&
            Mathf.Abs(_currentOffsetAngle - _lastSyncedAngle) > 1.5f)
        {
            _lastSyncedAngle = _currentOffsetAngle;
            _nextNetSyncTime = Time.time + 0.05f;
            _mapGenerator.NotifyDoorAngleChanged(_doorIndex, _currentOffsetAngle);
        }
    }

    private void DetectPlayerAndWeaponPush()
    {
        float worldAngleRad = (_closedAngleDeg + _currentOffsetAngle) * Mathf.Deg2Rad;
        Vector2 doorDir = new Vector2(Mathf.Cos(worldAngleRad), Mathf.Sin(worldAngleRad));
        Vector2 doorNormal = new Vector2(-doorDir.y, doorDir.x); // +90 deg normal
        Vector2 boxCenter = _hingeWorldPos + doorDir * (_doorLength * 0.5f);

        // Slightly expanded detection box around the door panel so both player bodies and weapon barrels push smoothly
        Vector2 checkSize = new Vector2(_doorLength + 0.12f, _doorThickness + 0.28f);
        var filter = new ContactFilter2D
        {
            useTriggers = true,
            useLayerMask = false
        };
        int hitCount = Physics2D.OverlapBox(
            boxCenter,
            checkSize,
            _closedAngleDeg + _currentOffsetAngle,
            filter,
            OverlapBuffer);

        for (int i = 0; i < hitCount; i++)
        {
            Collider2D col = OverlapBuffer[i];
            if (col == null || col == _boxCollider) continue;

            // Check if this collider is a character body OR an equipped WeaponVisual barrel
            bool isWeaponBarrel = col.name == "WeaponVisual";
            bool isCharacterBody = !col.isTrigger && col.attachedRigidbody != null &&
                                   (col.GetComponent<PlayerController>() != null || col.GetComponent<DummyController>() != null);

            if (!isWeaponBarrel && !isCharacterBody) continue;

            // Ignore inactive weapons
            if (!col.gameObject.activeInHierarchy) continue;

            // Determine pusher center point
            Vector2 pusherPos = col.bounds.center;
            Vector2 fromHinge = pusherPos - _hingeWorldPos;

            // Project onto door normal to see which side of the door the player/weapon is pressing from
            float sideDist = Vector2.Dot(fromHinge, doorNormal);
            float alongDoor = Mathf.Clamp(Vector2.Dot(fromHinge, doorDir), 0.25f, _doorLength);

            // Push the door away from the side the player or weapon barrel is on
            float pushDirection = sideDist >= 0f ? -1f : 1f;
            float leverage = alongDoor / _doorLength;
            float basePushSpeed = isWeaponBarrel ? 140f : 180f;

            _angularVelocity = pushDirection * basePushSpeed * Mathf.Max(0.45f, leverage);
        }
    }

    /// <summary>
    /// Applies a network-synced offset angle received from another client or the server.
    /// </summary>
    public void ApplyNetworkedAngle(float offsetAngle)
    {
        _currentOffsetAngle = Mathf.Clamp(offsetAngle, -_maxOpenAngle, _maxOpenAngle);
        _lastSyncedAngle = _currentOffsetAngle;
        ApplyTransformImmediate();
    }

    private void ApplyTransformImmediate()
    {
        transform.position = new Vector3(_hingeWorldPos.x, _hingeWorldPos.y, 0f);
        transform.rotation = Quaternion.Euler(0f, 0f, _closedAngleDeg + _currentOffsetAngle);
    }
}
