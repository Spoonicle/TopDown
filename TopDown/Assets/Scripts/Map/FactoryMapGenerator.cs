using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Server-authoritative procedural cement factory generator.
/// Synchronizes a single integer seed (<see cref="MapSeed"/>) across all clients via Netcode,
/// then deterministically constructs a 1-to-3 floor factory (<c>Basement</c>, <c>1st Floor</c>,
/// <c>2nd Floor</c>, or <c>3rd Floor</c>, with strict mutual exclusion between Basement and 3rd Floor),
/// stacked at the same world coordinates and connected by 1–3 physical walkable <see cref="StairwellZone"/>
/// flights per floor transition that smoothly cross-fade floor visuals from 0% to 100% as players climb
/// while allowing players on the lower floor to walk underneath the elevated upper half of the stairs.
/// </summary>
public class FactoryMapGenerator : NetworkBehaviour
{
    // ────────────────────────────── Types ──────────────────────────────────

    /// <summary>
    /// Archetype defining the scale, doorway count, window count, and cover density of a generated room.
    /// </summary>
    public enum RoomArchetype : byte
    {
        /// <summary>Small utility/storage closet (~5x5 to 8x8) with 1 doorway and no windows.</summary>
        Closet = 0,

        /// <summary>Standard factory office or control room (~11x11 to 17x17) with 1–3 doorways and 1–2 windows.</summary>
        StandardRoom = 1,

        /// <summary>Large assembly workshop (~18x18 to 24x24) with 2–4 doorways and 2–3 windows.</summary>
        LargeWorkshop = 2,

        /// <summary>Massive industrial hall/auditorium (~26x20 to 32x26) with 3–4 doorways and 2–4 windows.</summary>
        Auditorium = 3,

        /// <summary>Central 24x24 Objective Room or Atrium Hub with 4 cardinal doorways and 2–4 windows.</summary>
        ObjectiveHub = 4
    }

    private enum CellType : byte
    {
        OutsideCourtyard = 0,
        Wall = 1,
        HallwayFloor = 2,
        RoomFloor = 3,
        ObjectiveRoomFloor = 4,
        Doorway = 5,
        CoverPillar = 6,
        Window = 7
    }

    /// <summary>
    /// Metadata for a generated room on a specific floor.
    /// </summary>
    public struct RoomData
    {
        /// <summary>Floor level this room belongs to (-1=Basement, 0=1st, 1=2nd, 2=3rd).</summary>
        public int FloorLevel;

        /// <summary>Interior floor tile bounds in local floor grid coordinates.</summary>
        public RectInt Bounds;

        /// <summary>Size/function archetype of the room.</summary>
        public RoomArchetype Archetype;

        /// <summary>True if this room houses the primary ComputerTerminal objective.</summary>
        public bool IsObjectiveRoom;

        /// <summary>Target number of entrance/exit doorways (1–4) for this room.</summary>
        public int TargetDoorways;

        /// <summary>Actual number of distinct walls with doorways carved for this room.</summary>
        public int ActualDoorways;

        /// <summary>Target number of window spans to place in this room's walls.</summary>
        public int TargetWindows;
    }

    private struct EntranceData
    {
        public int WallSide; // 0=North, 1=East, 2=South, 3=West
        public Vector2Int GridCell;
        public Vector2 WorldPosition;
        public Vector2 CourtyardSpawnPosition;
    }

    private struct StaircaseShaftPlan
    {
        public int LowerFloor;
        public int UpperFloor;
        public int BayX;      // 4-tile wide interior [BayX .. BayX + 3]
        public int BayMinY;   // 10-tile tall interior [BayMinY .. BayMinY + 9]
        public int ThroatX;   // 1-tile wall column connecting landings into the central ring hallway
        public bool OpensToEastRing;
    }

    private struct DoorwaySpanRecord
    {
        public int FloorLevel;
        public bool Horizontal;
        public int StartX;
        public int StartY;
        public int Span;
        public float TotalDoorLeafLength;
    }

    private struct DesignatedDoorway
    {
        public int StartX;
        public int StartY;
        public int Span;
        public bool IsHorizontal;
    }

    private class FloorGenerationState
    {
        public int FloorLevel;
        public GameObject FloorRoot;
        public CellType[,] Grid;
        public bool[,] ProtectedZone;
        public RectInt CentralRoomBounds;
        public RectInt RingOuterBounds;
        public readonly List<RoomData> Rooms = new List<RoomData>();
        public readonly List<Vector2Int> DoorwayCells = new List<Vector2Int>();
        public readonly List<DesignatedDoorway> DesignatedDoorways = new List<DesignatedDoorway>();

        public readonly List<(SpriteRenderer renderer, Color baseColor)> Renderers = new List<(SpriteRenderer, Color)>();
        public readonly List<HallwayLight> Lights = new List<HallwayLight>();
        public readonly List<Collider2D> Colliders = new List<Collider2D>();
    }

    /// <summary>
    /// Binary Space Partition node for deterministic room placement.
    /// The BSP tree recursively subdivides the factory floor into non-overlapping rectangular
    /// zones, guaranteeing that every room archetype (Auditorium, Workshop, Standard, Closet)
    /// receives appropriately sized space without random overlap-retry failures.
    /// </summary>
    private class BspNode
    {
        public RectInt Bounds;
        public BspNode Left;
        public BspNode Right;
        public bool IsLeaf => Left == null && Right == null;
    }

    // ────────────────────────────── Singleton ──────────────────────────────

    /// <summary>Active singleton instance in the scene.</summary>
    public static FactoryMapGenerator Instance { get; private set; }

    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("Factory map generation settings asset.")]
    [SerializeField] private FactoryMapConfig _config;

    [Header("Seed Settings")]
    [Tooltip("If true, the Host generates a new random map seed at the start of each session.")]
    [SerializeField] private bool _useRandomSeedOnStart = true;

    [Tooltip("Fixed seed used when Use Random Seed On Start is false.")]
    [SerializeField] private int _initialSeed = 40921;

    [Tooltip("Show a 'New Map Seed' button and floor status in the top-left HUD for instant playtesting.")]
    [SerializeField] private bool _showMapSeedHud = true;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<int> _netMapSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private int _lastGeneratedSeed;

    // ────────────────────────────── Runtime State ──────────────────────────

    private GameObject _mapRoot;
    private int _width;
    private int _height;

    private readonly List<int> _activeFloors = new List<int>();
    private readonly Dictionary<int, FloorGenerationState> _floorStates = new Dictionary<int, FloorGenerationState>();
    private readonly List<StaircaseShaftPlan> _plannedStaircases = new List<StaircaseShaftPlan>();
    private readonly List<RoomData> _allRooms = new List<RoomData>();
    private readonly List<SwingDoor> _spawnedDoors = new List<SwingDoor>();
    private readonly List<DoorwaySpanRecord> _doorwayRecords = new List<DoorwaySpanRecord>();
    private readonly List<BreakableWindow> _spawnedWindows = new List<BreakableWindow>();
    private readonly List<StairwellZone> _spawnedStairwells = new List<StairwellZone>();

    private EntranceData[] _entrances = new EntranceData[4];
    private int _objectiveFloorLevel;
    private RectInt _objectiveRoomBounds;

    private GameObject _terminalObject;
    private Light2D _terminalLight;
    private float _terminalBaseLightIntensity = 0.85f;
    private readonly List<(SpriteRenderer renderer, Color baseColor)> _objectiveEntityRenderers = new List<(SpriteRenderer, Color)>();
    private readonly List<Collider2D> _objectiveEntityColliders = new List<Collider2D>();
    private Rigidbody2D _patrolDummyBody;

    private FloorGenerationState _currentFloor;
    private CellType[,] _grid;
    private bool[,] _entranceProtectedZone;
    private List<RoomData> _rooms;
    private List<Vector2Int> _doorwayCells;

    private static Sprite _cachedTileSprite;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The active seed driving the current factory layout.</summary>
    public int MapSeed => IsSpawned ? _netMapSeed.Value : _lastGeneratedSeed;

    /// <summary>Ordered list of active floor levels in the building (-1=Basement, 0=1st, 1=2nd, 2=3rd).</summary>
    public IReadOnlyList<int> ActiveFloors => _activeFloors;

    /// <summary>Total number of floors in the current building (1, 2, or 3).</summary>
    public int FloorCount => _activeFloors.Count;

    /// <summary>Floor level where the ComputerTerminal Objective Room is located.</summary>
    public int ObjectiveFloorLevel => _objectiveFloorLevel;

    /// <summary>Floor level the local player is currently on (-1=Basement, 0=1st, 1=2nd, 2=3rd).</summary>
    public int CurrentLocalFloorLevel { get; private set; }

    /// <summary>Normalized progress (0..1) while the local player is actively walking on a staircase, or -1 when not on stairs.</summary>
    public float CurrentStairClimbProgress { get; private set; } = -1f;

    /// <summary>True if the current building includes a Basement (-1).</summary>
    public bool HasBasement => _activeFloors.Contains(-1);

    /// <summary>True if the current building includes a 3rd Floor (2).</summary>
    public bool HasThirdFloor => _activeFloors.Contains(2);

    /// <summary>Number of rooms generated on the 1st Floor (or per floor).</summary>
    public int GeneratedRoomCount => _floorStates.TryGetValue(0, out var f0) ? f0.Rooms.Count : _allRooms.Count;

    /// <summary>Total number of rooms generated across all active floors.</summary>
    public int TotalGeneratedRoomCount => _allRooms.Count;

    /// <summary>Read-only list of all generated rooms across all floors.</summary>
    public IReadOnlyList<RoomData> AllRooms => _allRooms;

    /// <summary>Number of physics-pushed swinging doors spawned in the current layout.</summary>
    public int SpawnedDoorCount => _spawnedDoors.Count;

    /// <summary>Total number of doorway openings across all floors.</summary>
    public int TotalDoorwayCount => _doorwayRecords.Count;

    /// <summary>Number of breakable windows spawned across the factory.</summary>
    public int SpawnedWindowCount => _spawnedWindows.Count;

    /// <summary>Number of physical walkable staircases spawned across the factory.</summary>
    public int SpawnedStairwellCount => _spawnedStairwells.Count;

    /// <summary>True when all 4 perimeter entrances passed both grid BFS reachability and physics corridor clearance checks.</summary>
    public bool AllEntrancesVerifiedClear { get; private set; }

    /// <summary>True when every hallway across all active floors is verified to be at least 1.5x the player's body diameter wide.</summary>
    public bool AllHallwaysMeetMinWidth { get; private set; }

    /// <summary>True when 100% of doorways across all active floors have doors spawned that fill 100% of the doorway length.</summary>
    public bool AllDoorsFillDoorwaysVerified { get; private set; }

    /// <summary>Minimum hallway width in world units observed across the generated map.</summary>
    public float MinObservedHallwayWidthWorld { get; private set; }

    /// <summary>Minimum number of distinct entrance/exit doorways on any room in the building.</summary>
    public int MinDoorwaysPerRoom { get; private set; }

    /// <summary>Maximum number of distinct entrance/exit doorways on any room in the building.</summary>
    public int MaxDoorwaysPerRoom { get; private set; }

    /// <summary>World positions of the 4 shifted perimeter entrances on the 1st Floor (North, East, South, West).</summary>
    public Vector2[] GetEntranceWorldPositions()
    {
        var result = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = _entrances[i].WorldPosition;
        }
        return result;
    }

    /// <summary>Returns a human-readable display label for a floor level.</summary>
    public static string GetFloorDisplayName(int floorLevel)
    {
        return floorLevel switch
        {
            -1 => "BASEMENT",
            0 => "1ST FLOOR",
            1 => "2ND FLOOR",
            2 => "3RD FLOOR",
            _ => $"FLOOR {floorLevel}"
        };
    }

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Start()
    {
        if (!IsSpawned)
        {
            int seed = _useRandomSeedOnStart ? UnityEngine.Random.Range(10000, 999999) : _initialSeed;
            GenerateFactory(seed);
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _netMapSeed.OnValueChanged += OnNetMapSeedChanged;

        if (IsServer)
        {
            int seed = _useRandomSeedOnStart ? UnityEngine.Random.Range(10000, 999999) : _initialSeed;
            _netMapSeed.Value = seed;
            if (_lastGeneratedSeed != seed)
            {
                GenerateFactory(seed);
            }
        }
        else if (_netMapSeed.Value != 0 && _lastGeneratedSeed != _netMapSeed.Value)
        {
            GenerateFactory(_netMapSeed.Value);
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netMapSeed.OnValueChanged -= OnNetMapSeedChanged;
        base.OnNetworkDespawn();
    }

    private void OnNetMapSeedChanged(int previousSeed, int newSeed)
    {
        if (newSeed != 0 && newSeed != _lastGeneratedSeed)
        {
            GenerateFactory(newSeed);
        }
    }

    // ──────────────────────────── Seed Regeneration API ────────────────────

    /// <summary>
    /// Generates a brand new procedural factory layout with a fresh random seed.
    /// Callable from client or server during Play Mode.
    /// </summary>
    [ContextMenu("Generate New Map Seed")]
    public void RequestNewRandomMap()
    {
        int newSeed = UnityEngine.Random.Range(10000, 999999);
        if (IsSpawned)
        {
            RequestNewSeedServerRpc(newSeed);
        }
        else
        {
            GenerateFactory(newSeed);
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestNewSeedServerRpc(int newSeed)
    {
        _netMapSeed.Value = newSeed;
        if (_lastGeneratedSeed != newSeed)
        {
            GenerateFactory(newSeed);
        }
    }

    // ──────────────────────────── Door & Window Network Sync ───────────────

    /// <summary>
    /// Called by <see cref="SwingDoor"/> when a player or weapon pushes a door open.
    /// </summary>
    public void NotifyDoorAngleChanged(int doorIndex, float offsetAngle)
    {
        if (!IsSpawned) return;

        if (IsServer)
        {
            BroadcastDoorAngleRpc(doorIndex, offsetAngle);
        }
        else
        {
            SyncDoorAngleServerRpc(doorIndex, offsetAngle);
        }
    }

    [Rpc(SendTo.Server)]
    private void SyncDoorAngleServerRpc(int doorIndex, float offsetAngle)
    {
        if (doorIndex >= 0 && doorIndex < _spawnedDoors.Count && _spawnedDoors[doorIndex] != null)
        {
            _spawnedDoors[doorIndex].ApplyNetworkedAngle(offsetAngle);
        }
        BroadcastDoorAngleRpc(doorIndex, offsetAngle);
    }

    [Rpc(SendTo.NotServer)]
    private void BroadcastDoorAngleRpc(int doorIndex, float offsetAngle)
    {
        if (doorIndex >= 0 && doorIndex < _spawnedDoors.Count && _spawnedDoors[doorIndex] != null)
        {
            _spawnedDoors[doorIndex].ApplyNetworkedAngle(offsetAngle);
        }
    }

    /// <summary>
    /// Called by <see cref="BreakableWindow"/> when a bullet shatters an intact glass pane.
    /// Synchronizes the shattered state across all clients.
    /// </summary>
    public void NotifyWindowShattered(int windowIndex, Vector2 hitPoint)
    {
        if (!IsSpawned) return;

        if (IsServer)
        {
            BroadcastWindowShatteredRpc(windowIndex, hitPoint);
        }
        else
        {
            SyncWindowShatteredServerRpc(windowIndex, hitPoint);
        }
    }

    [Rpc(SendTo.Server)]
    private void SyncWindowShatteredServerRpc(int windowIndex, Vector2 hitPoint)
    {
        if (windowIndex >= 0 && windowIndex < _spawnedWindows.Count && _spawnedWindows[windowIndex] != null)
        {
            _spawnedWindows[windowIndex].ApplyShatteredState(hitPoint);
        }
        BroadcastWindowShatteredRpc(windowIndex, hitPoint);
    }

    [Rpc(SendTo.NotServer)]
    private void BroadcastWindowShatteredRpc(int windowIndex, Vector2 hitPoint)
    {
        if (windowIndex >= 0 && windowIndex < _spawnedWindows.Count && _spawnedWindows[windowIndex] != null)
        {
            _spawnedWindows[windowIndex].ApplyShatteredState(hitPoint);
        }
    }

    // ──────────────────── Physical Staircase Cross-Fade & Floor Physics ─────

    /// <summary>
    /// Continuously cross-fades <paramref name="lowerFloor"/> (opacity <c>1 - t</c>) and
    /// <paramref name="upperFloor"/> (opacity <c>t</c>) as the local player physically walks
    /// along a <see cref="StairwellZone"/> from the bottom landing (<c>t = 0</c>) to the top landing (<c>t = 1</c>).
    /// Swaps active floor physics at the midpoint (<c>t = 0.5</c>).
    /// </summary>
    public void SetStaircaseCrossFade(int lowerFloor, int upperFloor, float t)
    {
        t = Mathf.Clamp01(t);
        CurrentStairClimbProgress = t;

        int targetPhysicsFloor = t >= 0.5f ? upperFloor : lowerFloor;
        if (CurrentLocalFloorLevel != targetPhysicsFloor)
        {
            SetActivePhysicsFloor(targetPhysicsFloor);
        }

        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int fl = _activeFloors[i];
            if (fl == lowerFloor)
            {
                SetFloorVisualAlpha(fl, 1f - t);
            }
            else if (fl == upperFloor)
            {
                SetFloorVisualAlpha(fl, t);
            }
            else
            {
                SetFloorVisualAlpha(fl, 0f);
            }
        }

        int secondaryFloor = targetPhysicsFloor == lowerFloor ? upperFloor : lowerFloor;
        float secondaryAlpha = targetPhysicsFloor == lowerFloor ? t : (1f - t);
        for (int i = 0; i < _spawnedStairwells.Count; i++)
        {
            if (_spawnedStairwells[i] != null)
            {
                _spawnedStairwells[i].UpdateVisibilityForFloors(targetPhysicsFloor, secondaryFloor, secondaryAlpha);
            }
        }
    }

    /// <summary>
    /// Locks active physics and 100% visual visibility to <paramref name="floorLevel"/> once a player
    /// steps off a staircase landing (or at initial spawn).
    /// </summary>
    public void CommitToSingleFloor(int floorLevel)
    {
        CurrentStairClimbProgress = -1f;
        SetActivePhysicsFloor(floorLevel);

        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int fl = _activeFloors[i];
            SetFloorVisualAlpha(fl, fl == floorLevel ? 1f : 0f);
        }

        for (int i = 0; i < _spawnedStairwells.Count; i++)
        {
            if (_spawnedStairwells[i] != null)
            {
                _spawnedStairwells[i].UpdateVisibilityForFloors(floorLevel, floorLevel, 0f);
            }
        }
    }

    private void SetActivePhysicsFloor(int activeFloorLevel)
    {
        CurrentLocalFloorLevel = activeFloorLevel;

        foreach (var kvp in _floorStates)
        {
            bool active = kvp.Key == activeFloorLevel;
            List<Collider2D> cols = kvp.Value.Colliders;
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i] != null)
                {
                    cols[i].enabled = active;
                }
            }
        }

        bool objActive = activeFloorLevel == _objectiveFloorLevel;
        for (int i = 0; i < _objectiveEntityColliders.Count; i++)
        {
            if (_objectiveEntityColliders[i] != null)
            {
                _objectiveEntityColliders[i].enabled = objActive;
            }
        }
        if (_patrolDummyBody != null)
        {
            _patrolDummyBody.simulated = objActive;
        }

        Physics2D.SyncTransforms();
    }

    private void SetFloorVisualAlpha(int floorLevel, float alpha)
    {
        alpha = Mathf.Clamp01(alpha);
        if (!_floorStates.TryGetValue(floorLevel, out var state) || state.FloorRoot == null) return;

        bool visible = alpha > 0.005f || floorLevel == CurrentLocalFloorLevel;
        if (state.FloorRoot.activeSelf != visible)
        {
            state.FloorRoot.SetActive(visible);
        }

        if (visible)
        {
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                var (sr, baseCol) = state.Renderers[i];
                if (sr != null)
                {
                    sr.color = new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * alpha);
                }
            }

            for (int i = 0; i < state.Lights.Count; i++)
            {
                if (state.Lights[i] != null)
                {
                    state.Lights[i].SetFloorVisibilityAlpha(alpha);
                }
            }
        }

        if (floorLevel == _objectiveFloorLevel)
        {
            for (int i = 0; i < _objectiveEntityRenderers.Count; i++)
            {
                var (sr, baseCol) = _objectiveEntityRenderers[i];
                if (sr != null)
                {
                    sr.color = new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * alpha);
                }
            }

            if (_terminalLight != null)
            {
                _terminalLight.enabled = alpha > 0.005f;
                _terminalLight.intensity = _terminalBaseLightIntensity * alpha;
            }
        }
    }

    /// <summary>
    /// Resolves the immediate navigation target on the local player's current floor.
    /// When carrying objective data on a non-1st floor, points to the entry landing of the nearest
    /// physical staircase leading toward the 1st Floor (0).
    /// </summary>
    public Vector2 ResolveNavigationTarget(Vector2 fromWorldPos, Vector2 toWorldPos)
    {
        if (CurrentLocalFloorLevel == 0)
        {
            return toWorldPos;
        }

        int desiredNextFloor = CurrentLocalFloorLevel > 0
            ? CurrentLocalFloorLevel - 1
            : CurrentLocalFloorLevel + 1;

        StairwellZone bestStair = null;
        float bestDistSq = float.MaxValue;

        for (int i = 0; i < _spawnedStairwells.Count; i++)
        {
            StairwellZone stair = _spawnedStairwells[i];
            if (stair == null) continue;
            if (stair.ConnectsFloors(CurrentLocalFloorLevel, desiredNextFloor))
            {
                Vector2 landing = stair.GetEntryLandingForFloor(CurrentLocalFloorLevel);
                float dSq = (landing - fromWorldPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestStair = stair;
                }
            }
        }

        return bestStair != null
            ? bestStair.GetEntryLandingForFloor(CurrentLocalFloorLevel)
            : toWorldPos;
    }

    // ──────────────────────────── Player Body & Hallway Sizing ─────────────

    /// <summary>
    /// Measures the player character's physical collider diameter in world units (defaults to 1.0u if no Player is active).
    /// </summary>
    public float GetPlayerBodyDiameter()
    {
        var player = GameObject.Find("Player");
        if (player != null)
        {
            var circle = player.GetComponent<CircleCollider2D>();
            if (circle != null)
            {
                float scale = Mathf.Max(Mathf.Abs(player.transform.lossyScale.x), Mathf.Abs(player.transform.lossyScale.y));
                return Mathf.Max(0.8f, circle.radius * 2f * scale);
            }

            var col = player.GetComponent<Collider2D>();
            if (col != null && col.bounds.size.x > 0.1f)
            {
                return Mathf.Max(0.8f, Mathf.Max(col.bounds.size.x, col.bounds.size.y));
            }
        }
        return 1.0f;
    }

    /// <summary>
    /// Returns the minimum allowable hallway width in world units (at least 1.5x the player's body diameter).
    /// </summary>
    public float GetMinHallwayWidthWorld()
    {
        return GetPlayerBodyDiameter() * 1.5f;
    }

    /// <summary>
    /// Returns the minimum allowable hallway width in grid tiles (at least 1.5x the player's body, minimum 3 tiles).
    /// </summary>
    private int GetMinHallwayWidthTiles()
    {
        return Mathf.Max(3, Mathf.CeilToInt(GetMinHallwayWidthWorld()));
    }

    // ──────────────────────────── Procedural Generation ────────────────────

    /// <summary>
    /// Deterministically builds the entire multi-floor factory layout for the given integer seed.
    /// </summary>
    public void GenerateFactory(int seed)
    {
        _lastGeneratedSeed = seed;
        var rng = new System.Random(seed);

        _width = _config != null ? _config.BuildingWidth : 108;
        _height = _config != null ? _config.BuildingHeight : 108;

        _activeFloors.Clear();
        _floorStates.Clear();
        _plannedStaircases.Clear();
        _allRooms.Clear();
        _spawnedDoors.Clear();
        _doorwayRecords.Clear();
        _spawnedWindows.Clear();
        _spawnedStairwells.Clear();

        AllEntrancesVerifiedClear = false;
        AllHallwaysMeetMinWidth = true;
        AllDoorsFillDoorwaysVerified = true;
        MinObservedHallwayWidthWorld = float.MaxValue;
        CurrentLocalFloorLevel = 0;
        CurrentStairClimbProgress = -1f;

        if (_mapRoot != null)
        {
            _mapRoot.name = "DestroyedFactoryMap";
            _mapRoot.SetActive(false);
            Destroy(_mapRoot);
        }
        var existingRoot = GameObject.Find("GeneratedFactoryMap");
        if (existingRoot != null)
        {
            existingRoot.name = "DestroyedFactoryMap";
            existingRoot.SetActive(false);
            Destroy(existingRoot);
        }
        _mapRoot = new GameObject("GeneratedFactoryMap");

        ApplyAmbientLightingAndFloor();

        // Step 1: Roll the 1-to-3 active floors (strictly enforcing: Basement (-1) and 3rd Floor (2) never coexist)
        DetermineActiveFloors(rng);

        // Step 2: Plan 1–3 physical staircases per floor transition so shafts align vertically across stacked floors
        PlanStaircasesBetweenActiveFloors(rng);

        // Step 3: Generate each active floor's grid, rooms (Closets to Auditoriums), doors, windows, cover, and lights
        int minDoorsAcrossMap = int.MaxValue;
        int maxDoorsAcrossMap = 0;

        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int floorLevel = _activeFloors[i];
            GenerateSingleFloor(floorLevel, rng, ref minDoorsAcrossMap, ref maxDoorsAcrossMap);
        }

        MinDoorwaysPerRoom = _allRooms.Count > 0 ? minDoorsAcrossMap : 0;
        MaxDoorwaysPerRoom = maxDoorsAcrossMap;

        // Step 4: Spawn physical walkable StairwellZones for all planned shafts
        SpawnPlannedPhysicalStaircases();

        // Step 5: Position the ComputerTerminal in the Objective Room & assign Teams/Dummies on 1st Floor
        PlaceObjectiveAndTeams(rng);

        // Step 6: Commit initial active floor to 1st Floor (0)
        CommitToSingleFloor(0);

        // Step 7: Final physics & geometry verification for entrances, hallway minimum width (>=1.5x player), and full-length doors
        ValidateEntranceHallwayAndDoorSafety();
    }

    private void DetermineActiveFloors(System.Random rng)
    {
        int roll = rng.Next(100);
        if (roll < 16)
        {
            _activeFloors.Add(0);
        }
        else if (roll < 37)
        {
            _activeFloors.Add(-1);
            _activeFloors.Add(0);
        }
        else if (roll < 58)
        {
            _activeFloors.Add(0);
            _activeFloors.Add(1);
        }
        else if (roll < 79)
        {
            _activeFloors.Add(-1);
            _activeFloors.Add(0);
            _activeFloors.Add(1);
        }
        else
        {
            _activeFloors.Add(0);
            _activeFloors.Add(1);
            _activeFloors.Add(2);
        }

        if (_activeFloors.Contains(-1) && _activeFloors.Contains(2))
        {
            _activeFloors.Remove(2);
        }

        _objectiveFloorLevel = _activeFloors[rng.Next(_activeFloors.Count)];
    }

    private void PlanStaircasesBetweenActiveFloors(System.Random rng)
    {
        if (_activeFloors.Count <= 1) return;

        int objSize = _config != null ? _config.ObjectiveRoomSize : 24;
        int hw = Mathf.Max(GetMinHallwayWidthTiles(), _config != null ? _config.HallwayWidth : 6);
        int centralMinX = (_width - objSize) / 2;
        int centralMinY = (_height - objSize) / 2;
        int ringMinX = centralMinX - 1 - hw;
        int ringMaxX = centralMinX + objSize + 1 + hw;
        int ringMinY = centralMinY - 1 - hw;
        int ringMaxY = centralMinY + objSize + 1 + hw;

        int westBayX = ringMinX - 5;
        int westThroatX = ringMinX - 1;
        int eastBayX = ringMaxX + 1;
        int eastThroatX = ringMaxX;

        int northBayMinY = ringMaxY - 12;
        int centerBayMinY = ringMinY + 14;
        int southBayMinY = ringMinY + 2;

        var pool0 = new (int bayX, int bayMinY, int throatX, bool opensEast)[]
        {
            (westBayX, northBayMinY, westThroatX, true),
            (eastBayX, northBayMinY, eastThroatX, false),
            (westBayX, centerBayMinY, westThroatX, true)
        };

        var pool1 = new (int bayX, int bayMinY, int throatX, bool opensEast)[]
        {
            (eastBayX, southBayMinY, eastThroatX, false),
            (westBayX, southBayMinY, westThroatX, true),
            (eastBayX, centerBayMinY, eastThroatX, false)
        };

        for (int t = 0; t < _activeFloors.Count - 1; t++)
        {
            int lowerFloor = _activeFloors[t];
            int upperFloor = _activeFloors[t + 1];
            int stairCount = rng.Next(1, 4);

            var pool = (t == 0) ? pool0 : pool1;
            int[] order = { 0, 1, 2 };
            for (int k = 2; k > 0; k--)
            {
                int swap = rng.Next(k + 1);
                (order[k], order[swap]) = (order[swap], order[k]);
            }

            for (int s = 0; s < stairCount; s++)
            {
                var slot = pool[order[s]];
                _plannedStaircases.Add(new StaircaseShaftPlan
                {
                    LowerFloor = lowerFloor,
                    UpperFloor = upperFloor,
                    BayX = slot.bayX,
                    BayMinY = slot.bayMinY,
                    ThroatX = slot.throatX,
                    OpensToEastRing = slot.opensEast
                });
            }
        }
    }

    private void GenerateSingleFloor(
        int floorLevel,
        System.Random rng,
        ref int minDoorsAcrossMap,
        ref int maxDoorsAcrossMap)
    {
        var state = new FloorGenerationState
        {
            FloorLevel = floorLevel,
            FloorRoot = new GameObject($"Floor_{GetFloorDisplayName(floorLevel).Replace(' ', '_')}"),
            Grid = new CellType[_width, _height],
            ProtectedZone = new bool[_width, _height]
        };
        state.FloorRoot.transform.SetParent(_mapRoot.transform, false);
        _floorStates[floorLevel] = state;

        _currentFloor = state;
        _grid = state.Grid;
        _entranceProtectedZone = state.ProtectedZone;
        _rooms = state.Rooms;
        _doorwayCells = state.DoorwayCells;

        if (floorLevel != 0)
        {
            CreateNonGroundFloorBackdrop(floorLevel, state.FloorRoot.transform);
        }

        // 1. Initialize entire building footprint as solid concrete Wall
        for (int x = 0; x < _width; x++)
        {
            for (int y = 0; y < _height; y++)
            {
                _grid[x, y] = CellType.Wall;
            }
        }

        // 2. Carve Central Room
        bool isObjectiveFloor = (floorLevel == _objectiveFloorLevel);
        PlaceCentralRoom(rng, isObjectiveFloor);

        // 3. Carve the Inner Ring Hallway around the Central Room & reserve/carve Stairwell Bays
        RectInt ringOuter = CarveInnerRingHallway();
        state.RingOuterBounds = ringOuter;
        CarveAndReserveStairwellBaysForFloor(floorLevel);

        // 4. Carve perimeter entrances (1st Floor) or internal arterial corridors (non-ground floors)
        if (floorLevel == 0)
        {
            PlaceAndConnectFourEntrances(rng, ringOuter);
        }
        else
        {
            CarveNonGroundFloorArterialHallways(rng, ringOuter);
        }

        // 5. Carve rooms via BSP (Binary Space Partitioning) across 4 size archetypes
        CarveInteriorRoomsViaBsp(rng);

        // 6. Connect rooms with secondary hallways and doorways according to each room's TargetDoorways (1–4)
        ConnectRoomsWithSecondaryHallwaysAndDoors(rng, ringOuter);

        // 7. Place breakable glass windows in room walls bordering hallways or adjacent rooms
        PlaceRoomWindows(rng);

        // 8. Place interior concrete cover pillars scaled to each room's archetype
        PlaceInteriorCoverPillars(rng);

        // 9. Safety pass — enforce 100% entrance, stairwell bay, doorway swing arc, BFS path, and >=1.5x player hallway width
        EnforceFloorSafetyClearance(floorLevel, ringOuter);

        // 9.5. Normalize all Doorway runs (lock flanking Wall jambs) & spawn full-span SwingDoors filling 100% of every doorway
        NormalizeAndSpawnAllDoorsForFloor(floorLevel);

        // 10. Instantiate greedy-merged concrete wall colliders and cover pillars for this floor
        BuildWallAndCoverGeometry();

        // 11. Spawn steady & flickering emergency lights along hallways ONLY (rooms stay dark!)
        SpawnHallwayEmergencyLights(floorLevel, rng);

        // Cache all SpriteRenderers, HallwayLights, and Collider2Ds on this floor
        CacheFloorComponents(state);

        // Record room stats
        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData r = _rooms[i];
            r.ActualDoorways = GetRoomDoorwayWallCount(r.Bounds);
            _rooms[i] = r;
            _allRooms.Add(r);

            if (r.ActualDoorways < minDoorsAcrossMap) minDoorsAcrossMap = r.ActualDoorways;
            if (r.ActualDoorways > maxDoorsAcrossMap) maxDoorsAcrossMap = r.ActualDoorways;
        }
    }

    private void CacheFloorComponents(FloorGenerationState state)
    {
        state.Renderers.Clear();
        var srs = state.FloorRoot.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
        {
            if (srs[i].GetComponentInParent<HallwayLight>() != null) continue;
            state.Renderers.Add((srs[i], srs[i].color));
        }

        state.Lights.Clear();
        state.Lights.AddRange(state.FloorRoot.GetComponentsInChildren<HallwayLight>(true));

        state.Colliders.Clear();
        state.Colliders.AddRange(state.FloorRoot.GetComponentsInChildren<Collider2D>(true));
    }

    private void CreateNonGroundFloorBackdrop(int floorLevel, Transform parent)
    {
        EnsureTileSprite();
        Vector2 centerWorld = GridToWorld((_width - 1) * 0.5f, (_height - 1) * 0.5f);

        var shroudGo = new GameObject("NonGroundVoidShroud");
        shroudGo.transform.SetParent(parent, false);
        shroudGo.transform.position = new Vector3(centerWorld.x, centerWorld.y, 0f);
        shroudGo.transform.localScale = new Vector3(_width + 90f, _height + 90f, 1f);
        var shroudSr = shroudGo.AddComponent<SpriteRenderer>();
        shroudSr.sprite = _cachedTileSprite;
        shroudSr.color = new Color(0.05f, 0.055f, 0.06f, 1f);
        shroudSr.sortingOrder = -6;

        var slabGo = new GameObject("FloorConcreteSlab");
        slabGo.transform.SetParent(parent, false);
        slabGo.transform.position = new Vector3(centerWorld.x, centerWorld.y, 0f);
        slabGo.transform.localScale = new Vector3(_width, _height, 1f);
        var slabSr = slabGo.AddComponent<SpriteRenderer>();
        slabSr.sprite = _cachedTileSprite;
        slabSr.color = floorLevel < 0
            ? new Color(0.18f, 0.19f, 0.21f, 1f)
            : new Color(0.22f, 0.23f, 0.24f, 1f);
        slabSr.sortingOrder = -5;
    }

    private void ApplyAmbientLightingAndFloor()
    {
        var globalLightGo = GameObject.Find("Global Light 2D");
        if (globalLightGo != null)
        {
            var globalLight = globalLightGo.GetComponent<Light2D>();
            if (globalLight != null)
            {
                globalLight.intensity = _config != null ? _config.AmbientLightIntensity : 0.08f;
                globalLight.color = new Color(0.65f, 0.72f, 0.82f, 1f);
            }
        }

        var floorGo = GameObject.Find("FloorGrid");
        if (floorGo != null)
        {
            int margin = _config != null ? _config.CourtyardMargin : 14;
            float totalW = _width + margin * 4f;
            float totalH = _height + margin * 4f;
            var sr = floorGo.GetComponent<SpriteRenderer>();
            if (sr != null && sr.drawMode == SpriteDrawMode.Tiled)
            {
                sr.size = new Vector2(totalW, totalH);
            }
            else
            {
                floorGo.transform.localScale = new Vector3(totalW, totalH, 1f);
            }
        }
    }

    private void PlaceCentralRoom(System.Random rng, bool isObjectiveRoom)
    {
        int objSize = _config != null ? _config.ObjectiveRoomSize : 24;
        int startX = (_width - objSize) / 2;
        int startY = (_height - objSize) / 2;

        var bounds = new RectInt(startX, startY, objSize, objSize);
        _currentFloor.CentralRoomBounds = bounds;
        if (isObjectiveRoom)
        {
            _objectiveRoomBounds = bounds;
        }

        CellType floorType = isObjectiveRoom ? CellType.ObjectiveRoomFloor : CellType.RoomFloor;
        for (int x = startX; x < startX + objSize; x++)
        {
            for (int y = startY; y < startY + objSize; y++)
            {
                _grid[x, y] = floorType;
            }
        }

        _rooms.Add(new RoomData
        {
            FloorLevel = _currentFloor.FloorLevel,
            Bounds = bounds,
            Archetype = RoomArchetype.ObjectiveHub,
            IsObjectiveRoom = isObjectiveRoom,
            TargetDoorways = 4,
            ActualDoorways = 4,
            TargetWindows = rng.Next(2, 5)
        });
    }

    private int GetConfiguredHallwayWidth()
    {
        int cfg = _config != null ? _config.HallwayWidth : 6;
        return Mathf.Max(GetMinHallwayWidthTiles(), cfg);
    }

    private int GetDoorwaySpan()
    {
        int hw = GetConfiguredHallwayWidth();
        return hw >= 4 ? 4 : 2;
    }

    private RectInt CarveInnerRingHallway()
    {
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = GetDoorwaySpan();
        RectInt central = _currentFloor.CentralRoomBounds;

        int ringMinX = central.xMin - 1 - hw;
        int ringMaxX = central.xMax + 1 + hw;
        int ringMinY = central.yMin - 1 - hw;
        int ringMaxY = central.yMax + 1 + hw;

        for (int x = ringMinX; x < ringMaxX; x++)
        {
            for (int y = ringMinY; y < ringMaxY; y++)
            {
                bool inTopBand = y >= central.yMax + 1;
                bool inBottomBand = y < central.yMin - 1;
                bool inLeftBand = x < central.xMin - 1;
                bool inRightBand = x >= central.xMax + 1;

                if (inTopBand || inBottomBand || inLeftBand || inRightBand)
                {
                    _grid[x, y] = CellType.HallwayFloor;
                }
            }
        }

        int midX = central.xMin + (central.width - doorSpan) / 2;
        int midY = central.yMin + (central.height - doorSpan) / 2;

        CarveHorizontalDoorway(midX, central.yMax, doorSpan);
        CarveHorizontalDoorway(midX, central.yMin - 1, doorSpan);
        CarveVerticalDoorway(central.xMax, midY, doorSpan);
        CarveVerticalDoorway(central.xMin - 1, midY, doorSpan);

        return new RectInt(ringMinX, ringMinY, ringMaxX - ringMinX, ringMaxY - ringMinY);
    }

    /// <summary>
    /// Reserves all planned stairwell bays in <c>_entranceProtectedZone</c> and carves the 4x10 stairwell shaft
    /// on any floor connected by that staircase:
    /// - On <c>LowerFloor</c>: opens the entire stairwell side into the ring hallway so players can either
    ///   enter at the Bottom Landing to climb ON TOP of the stairs, or walk UNDERNEATH the elevated upper half!
    /// - On <c>UpperFloor</c>: opens a full 4-tile-wide Top Landing passage into the ring hallway while keeping
    ///   the lower drop-off railed off by concrete wall.
    /// </summary>
    private void CarveAndReserveStairwellBaysForFloor(int floorLevel)
    {
        for (int i = 0; i < _plannedStaircases.Count; i++)
        {
            StaircaseShaftPlan plan = _plannedStaircases[i];

            MarkDoorwayClearanceZone(plan.BayX - 2, plan.BayMinY - 2, 8, 14);

            if (floorLevel != plan.LowerFloor && floorLevel != plan.UpperFloor)
            {
                continue;
            }

            // Surround the 4x10 bay with solid 1-tile concrete walls first
            for (int x = plan.BayX - 1; x <= plan.BayX + 4; x++)
            {
                for (int y = plan.BayMinY - 1; y <= plan.BayMinY + 10; y++)
                {
                    if (x > 0 && x < _width - 1 && y > 0 && y < _height - 1)
                    {
                        _grid[x, y] = CellType.Wall;
                    }
                }
            }

            // Carve the 4x10 staircase interior (width = 4 tiles >= 4x player body)
            for (int x = plan.BayX; x < plan.BayX + 4; x++)
            {
                for (int y = plan.BayMinY; y < plan.BayMinY + 10; y++)
                {
                    _grid[x, y] = CellType.HallwayFloor;
                }
            }

            if (floorLevel == plan.LowerFloor)
            {
                // On the lower floor, open the full side facing the ring hallway (and top under-stair exit)
                // so players can walk into the Bottom Landing to climb OR walk underneath the elevated upper half!
                for (int y = plan.BayMinY; y < plan.BayMinY + 10; y++)
                {
                    _grid[plan.ThroatX, y] = CellType.HallwayFloor;
                }
            }
            else if (floorLevel == plan.UpperFloor)
            {
                // On the upper floor, open a 4-tile-wide Top Landing passage (>= 4x player body) into the ring hallway
                for (int y = plan.BayMinY + 6; y < plan.BayMinY + 10; y++)
                {
                    _grid[plan.ThroatX, y] = CellType.HallwayFloor;
                }
            }
        }
    }

    private void SpawnPlannedPhysicalStaircases()
    {
        for (int i = 0; i < _plannedStaircases.Count; i++)
        {
            StaircaseShaftPlan plan = _plannedStaircases[i];
            Vector2 centerWorld = GridToWorld(plan.BayX + 1.5f, plan.BayMinY + 4.5f);

            var stairGo = new GameObject($"PhysicalStaircase_{i}_{plan.LowerFloor}_to_{plan.UpperFloor}");
            stairGo.transform.SetParent(_mapRoot.transform, false);

            var zone = stairGo.AddComponent<StairwellZone>();
            zone.Initialize(this, plan.LowerFloor, plan.UpperFloor, centerWorld, plan.OpensToEastRing);
            _spawnedStairwells.Add(zone);
        }
    }

    private void PlaceAndConnectFourEntrances(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = GetDoorwaySpan();
        int doorOffset = Mathf.Max(0, (hw - doorSpan) / 2);
        float courtyardDist = (_config != null ? _config.CourtyardMargin : 14) * 0.65f;
        int vestibuleDepth = Mathf.Max(4, hw + 2);
        float halfHwOffset = (hw - 1) * 0.5f;

        // 0 = North (y = _height - 1)
        int northX = rng.Next(margin, _width - margin - hw);
        _entrances[0] = new EntranceData
        {
            WallSide = 0,
            GridCell = new Vector2Int(northX, _height - 1),
            WorldPosition = GridToWorld(northX + halfHwOffset, _height - 1),
            CourtyardSpawnPosition = GridToWorld(northX + halfHwOffset, _height - 1 + courtyardDist)
        };
        CarveHorizontalDoorway(northX + doorOffset, _height - 1, doorSpan);
        ReserveEntranceVestibule(northX, _height - 1 - vestibuleDepth, hw, vestibuleDepth, 2);
        CarveLCorridor(new Vector2Int(northX, _height - 1 - vestibuleDepth), new Vector2Int(
            Mathf.Clamp(northX, ringOuter.xMin, ringOuter.xMax - hw),
            ringOuter.yMax - hw), hw, true);

        // 1 = East (x = _width - 1)
        int eastY = rng.Next(margin, _height - margin - hw);
        _entrances[1] = new EntranceData
        {
            WallSide = 1,
            GridCell = new Vector2Int(_width - 1, eastY),
            WorldPosition = GridToWorld(_width - 1, eastY + halfHwOffset),
            CourtyardSpawnPosition = GridToWorld(_width - 1 + courtyardDist, eastY + halfHwOffset)
        };
        CarveVerticalDoorway(_width - 1, eastY + doorOffset, doorSpan);
        ReserveEntranceVestibule(_width - 1 - vestibuleDepth, eastY, vestibuleDepth, hw, 2);
        CarveLCorridor(new Vector2Int(_width - 1 - vestibuleDepth, eastY), new Vector2Int(
            ringOuter.xMax - hw,
            Mathf.Clamp(eastY, ringOuter.yMin, ringOuter.yMax - hw)), hw, false);

        // 2 = South (y = 0)
        int southX = rng.Next(margin, _width - margin - hw);
        _entrances[2] = new EntranceData
        {
            WallSide = 2,
            GridCell = new Vector2Int(southX, 0),
            WorldPosition = GridToWorld(southX + halfHwOffset, 0),
            CourtyardSpawnPosition = GridToWorld(southX + halfHwOffset, -courtyardDist)
        };
        CarveHorizontalDoorway(southX + doorOffset, 0, doorSpan);
        ReserveEntranceVestibule(southX, 1, hw, vestibuleDepth, 2);
        CarveLCorridor(new Vector2Int(southX, vestibuleDepth), new Vector2Int(
            Mathf.Clamp(southX, ringOuter.xMin, ringOuter.xMax - hw),
            ringOuter.yMin), hw, true);

        // 3 = West (x = 0)
        int westY = rng.Next(margin, _height - margin - hw);
        _entrances[3] = new EntranceData
        {
            WallSide = 3,
            GridCell = new Vector2Int(0, westY),
            WorldPosition = GridToWorld(0, westY + halfHwOffset),
            CourtyardSpawnPosition = GridToWorld(-courtyardDist, westY + halfHwOffset)
        };
        CarveVerticalDoorway(0, westY + doorOffset, doorSpan);
        ReserveEntranceVestibule(1, westY, vestibuleDepth, hw, 2);
        CarveLCorridor(new Vector2Int(vestibuleDepth, westY), new Vector2Int(
            ringOuter.xMin,
            Mathf.Clamp(westY, ringOuter.yMin, ringOuter.yMax - hw)), hw, false);
    }

    private void CarveNonGroundFloorArterialHallways(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = GetConfiguredHallwayWidth();

        int northX = rng.Next(margin, _width - margin - hw);
        CarveLCorridor(
            new Vector2Int(northX, _height - 10 - hw),
            new Vector2Int(Mathf.Clamp(northX, ringOuter.xMin, ringOuter.xMax - hw), ringOuter.yMax - hw),
            hw,
            true);

        int eastY = rng.Next(margin, _height - margin - hw);
        CarveLCorridor(
            new Vector2Int(_width - 10 - hw, eastY),
            new Vector2Int(ringOuter.xMax - hw, Mathf.Clamp(eastY, ringOuter.yMin, ringOuter.yMax - hw)),
            hw,
            false);

        int southX = rng.Next(margin, _width - margin - hw);
        CarveLCorridor(
            new Vector2Int(southX, 10),
            new Vector2Int(Mathf.Clamp(southX, ringOuter.xMin, ringOuter.xMax - hw), ringOuter.yMin),
            hw,
            true);

        int westY = rng.Next(margin, _height - margin - hw);
        CarveLCorridor(
            new Vector2Int(10, westY),
            new Vector2Int(ringOuter.xMin, Mathf.Clamp(westY, ringOuter.yMin, ringOuter.yMax - hw)),
            hw,
            false);
    }

    private void ReserveEntranceVestibule(int startX, int startY, int w, int h, int margin)
    {
        for (int x = startX; x < startX + w; x++)
        {
            for (int y = startY; y < startY + h; y++)
            {
                if (x > 0 && x < _width - 1 && y > 0 && y < _height - 1)
                {
                    if (_grid[x, y] == CellType.Wall)
                    {
                        _grid[x, y] = CellType.HallwayFloor;
                    }
                }
            }
        }

        for (int x = startX - margin; x < startX + w + margin; x++)
        {
            for (int y = startY - margin; y < startY + h + margin; y++)
            {
                if (x >= 0 && x < _width && y >= 0 && y < _height)
                {
                    _entranceProtectedZone[x, y] = true;
                }
            }
        }
    }

    private void CarveLCorridor(Vector2Int from, Vector2Int to, int width, bool verticalFirst)
    {
        width = Mathf.Max(width, GetMinHallwayWidthTiles());

        if (verticalFirst)
        {
            int minY = Mathf.Min(from.y, to.y);
            int maxY = Mathf.Max(from.y, to.y);
            for (int y = minY; y <= maxY + width - 1; y++)
            {
                for (int w = 0; w < width; w++)
                {
                    SetHallwayIfWall(from.x + w, y);
                }
            }

            int minX = Mathf.Min(from.x, to.x);
            int maxX = Mathf.Max(from.x, to.x);
            for (int x = minX; x <= maxX + width - 1; x++)
            {
                for (int w = 0; w < width; w++)
                {
                    SetHallwayIfWall(x, to.y + w);
                }
            }
        }
        else
        {
            int minX = Mathf.Min(from.x, to.x);
            int maxX = Mathf.Max(from.x, to.x);
            for (int x = minX; x <= maxX + width - 1; x++)
            {
                for (int w = 0; w < width; w++)
                {
                    SetHallwayIfWall(x, from.y + w);
                }
            }

            int minY = Mathf.Min(from.y, to.y);
            int maxY = Mathf.Max(from.y, to.y);
            for (int y = minY; y <= maxY + width - 1; y++)
            {
                for (int w = 0; w < width; w++)
                {
                    SetHallwayIfWall(to.x + w, y);
                }
            }
        }
    }

    private void SetHallwayIfWall(int x, int y)
    {
        if (x <= 0 || x >= _width - 1 || y <= 0 || y >= _height - 1) return;
        if (_grid[x, y] == CellType.Wall)
        {
            _grid[x, y] = CellType.HallwayFloor;
        }
    }

    // ──────────────── BSP (Binary Space Partitioning) Constants ────────────

    /// <summary>
    /// Minimum dimension (width or height) in tiles for a BSP leaf to be considered
    /// viable for room placement (room interior + 1-tile wall padding each side).
    /// </summary>
    private const int BspMinLeafDim = 9;

    /// <summary>
    /// Maximum BSP tree recursion depth. Controls the granularity of spatial partitioning.
    /// Higher values produce more, smaller rooms; lower values produce fewer, larger rooms.
    /// </summary>
    private const int BspMaxDepth = 5;

    // ──────────────── BSP Room Placement ─────────────────────────────────

    /// <summary>
    /// Replaces random-coordinate room placement with BSP (Binary Space Partitioning).
    /// Subdivides the factory floor into 8 zones around the central ring hallway,
    /// recursively partitions each zone into non-overlapping rectangular leaves, then
    /// places rooms sized to each leaf's dimensions — guaranteeing that Auditoriums and
    /// Workshops always receive appropriately sized space without retry failures.
    /// </summary>
    private void CarveInteriorRoomsViaBsp(System.Random rng)
    {
        int targetRooms = _config != null ? _config.TargetRoomCount : 15;
        RectInt ring = _currentFloor.RingOuterBounds;

        // ── Define 8 non-overlapping zones tiling the building interior around the ring ──
        //
        //   +------+--------+------+
        //   |  NW  |   N    |  NE  |
        //   +------+--------+------+
        //   |  W   | [RING] |  E   |
        //   +------+--------+------+
        //   |  SW  |   S    |  SE  |
        //   +------+--------+------+
        //
        var zones = new List<RectInt>(8);
        int bMinX = 2, bMinY = 2;
        int bMaxX = _width - 2, bMaxY = _height - 2;

        // 4 corner quadrants
        AddBspZoneIfViable(zones, bMinX,     ring.yMax, ring.xMin - bMinX, bMaxY - ring.yMax); // NW
        AddBspZoneIfViable(zones, ring.xMax, ring.yMax, bMaxX - ring.xMax, bMaxY - ring.yMax); // NE
        AddBspZoneIfViable(zones, bMinX,     bMinY,     ring.xMin - bMinX, ring.yMin - bMinY); // SW
        AddBspZoneIfViable(zones, ring.xMax, bMinY,     bMaxX - ring.xMax, ring.yMin - bMinY); // SE

        // 4 strip zones (between ring outer edge and building perimeter)
        AddBspZoneIfViable(zones, ring.xMin, ring.yMax, ring.width,        bMaxY - ring.yMax); // N
        AddBspZoneIfViable(zones, ring.xMin, bMinY,     ring.width,        ring.yMin - bMinY); // S
        AddBspZoneIfViable(zones, ring.xMax, ring.yMin, bMaxX - ring.xMax, ring.height);       // E
        AddBspZoneIfViable(zones, bMinX,     ring.yMin, ring.xMin - bMinX, ring.height);       // W

        // ── Build BSP trees for each zone and collect all leaf partitions ──
        var allLeaves = new List<BspNode>(64);
        for (int i = 0; i < zones.Count; i++)
        {
            BspNode tree = BuildBspTree(zones[i], rng, 0, BspMaxDepth);
            CollectBspLeaves(tree, allLeaves);
        }

        // Sort leaves by area descending so the largest partitions (Auditoriums, Workshops)
        // get first priority for room placement
        allLeaves.Sort((a, b) =>
        {
            int areaA = a.Bounds.width * a.Bounds.height;
            int areaB = b.Bounds.width * b.Bounds.height;
            return areaB.CompareTo(areaA);
        });

        // ── Place rooms in BSP leaves until we reach the target count ──
        int targetFromBsp = Mathf.Max(0, targetRooms - _rooms.Count);
        int placed = 0;
        for (int i = 0; i < allLeaves.Count && placed < targetFromBsp; i++)
        {
            if (TryPlaceRoomInBspLeaf(allLeaves[i], rng))
            {
                placed++;
            }
        }

        // ── Fallback: fill remaining slots with small random rooms if BSP fell short ──
        int fallbackAttempts = 0;
        int maxFallbackAttempts = (targetFromBsp - placed) * 80;
        while (_rooms.Count < targetRooms && fallbackAttempts < maxFallbackAttempts)
        {
            fallbackAttempts++;
            int rw = rng.Next(5, 12);
            int rh = rng.Next(5, 12);
            int rx = rng.Next(2, _width - rw - 2);
            int ry = rng.Next(2, _height - rh - 2);

            if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

            var candidate = new RectInt(rx, ry, rw, rh);
            int doorSpan = 3;
            Vector2Int doorCell;
            bool horizontalDoor;
            bool connected =
                TryFindHallwayDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor) ||
                TryCarveBranchHallwayToRoom(candidate, doorSpan, out doorCell, out horizontalDoor) ||
                TryFindAdjacentRoomDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor);

            if (!connected) continue;

            for (int x = rx; x < rx + rw; x++)
                for (int y = ry; y < ry + rh; y++)
                    _grid[x, y] = CellType.RoomFloor;

            if (horizontalDoor)
                CarveHorizontalDoorway(doorCell.x, doorCell.y, doorSpan);
            else
                CarveVerticalDoorway(doorCell.x, doorCell.y, doorSpan);

            RoomArchetype fallbackArch = (rw >= 11 && rh >= 11)
                ? RoomArchetype.StandardRoom
                : RoomArchetype.Closet;
            GetArchetypeDoorAndWindowTargets(fallbackArch, rng, out int td, out int tw);

            _rooms.Add(new RoomData
            {
                FloorLevel = _currentFloor.FloorLevel,
                Bounds = candidate,
                Archetype = fallbackArch,
                IsObjectiveRoom = false,
                TargetDoorways = td,
                ActualDoorways = 1,
                TargetWindows = tw
            });
        }
    }

    /// <summary>Adds a zone to the list only if both dimensions meet the minimum BSP leaf size.</summary>
    private static void AddBspZoneIfViable(List<RectInt> zones, int x, int y, int w, int h)
    {
        if (w >= BspMinLeafDim && h >= BspMinLeafDim)
        {
            zones.Add(new RectInt(x, y, w, h));
        }
    }

    /// <summary>
    /// Recursively subdivides a rectangular region into a binary tree of sub-partitions.
    /// Split direction favors the longer axis with 80% probability for natural room proportions.
    /// Split position is biased toward the center (±30%) for balanced partition sizes.
    /// </summary>
    private BspNode BuildBspTree(RectInt bounds, System.Random rng, int depth, int maxDepth)
    {
        var node = new BspNode { Bounds = bounds };

        if (depth >= maxDepth) return node;

        // Both children must be at least BspMinLeafDim in the split dimension
        int minSplitDim = BspMinLeafDim * 2 + 1;
        bool canSplitH = bounds.height >= minSplitDim;
        bool canSplitV = bounds.width >= minSplitDim;

        if (!canSplitH && !canSplitV) return node;

        // Choose split axis: strongly prefer splitting the longer dimension
        bool splitHorizontal;
        if (canSplitH && !canSplitV)       splitHorizontal = true;
        else if (!canSplitH && canSplitV)  splitHorizontal = false;
        else splitHorizontal = bounds.height > bounds.width
            ? (rng.Next(100) < 80)
            : (rng.Next(100) < 20);

        if (splitHorizontal)
        {
            int splitMin = bounds.yMin + BspMinLeafDim;
            int splitMax = bounds.yMax - BspMinLeafDim;
            if (splitMin > splitMax) return node;

            int center = (splitMin + splitMax) / 2;
            int range = Mathf.Max(1, (splitMax - splitMin) * 30 / 100);
            int splitY = Mathf.Clamp(center + rng.Next(-range, range + 1), splitMin, splitMax);

            node.Left = BuildBspTree(
                new RectInt(bounds.xMin, bounds.yMin, bounds.width, splitY - bounds.yMin),
                rng, depth + 1, maxDepth);
            node.Right = BuildBspTree(
                new RectInt(bounds.xMin, splitY, bounds.width, bounds.yMax - splitY),
                rng, depth + 1, maxDepth);
        }
        else
        {
            int splitMin = bounds.xMin + BspMinLeafDim;
            int splitMax = bounds.xMax - BspMinLeafDim;
            if (splitMin > splitMax) return node;

            int center = (splitMin + splitMax) / 2;
            int range = Mathf.Max(1, (splitMax - splitMin) * 30 / 100);
            int splitX = Mathf.Clamp(center + rng.Next(-range, range + 1), splitMin, splitMax);

            node.Left = BuildBspTree(
                new RectInt(bounds.xMin, bounds.yMin, splitX - bounds.xMin, bounds.height),
                rng, depth + 1, maxDepth);
            node.Right = BuildBspTree(
                new RectInt(splitX, bounds.yMin, bounds.xMax - splitX, bounds.height),
                rng, depth + 1, maxDepth);
        }

        return node;
    }

    /// <summary>Recursively collects all leaf nodes from a BSP tree.</summary>
    private static void CollectBspLeaves(BspNode node, List<BspNode> leaves)
    {
        if (node == null) return;
        if (node.IsLeaf) { leaves.Add(node); return; }
        CollectBspLeaves(node.Left, leaves);
        CollectBspLeaves(node.Right, leaves);
    }

    /// <summary>
    /// Classifies the best-fitting room archetype for a BSP leaf based on its available
    /// interior dimensions (after subtracting 1-tile wall padding on each side).
    /// </summary>
    private static RoomArchetype ClassifyBspLeafArchetype(int interiorW, int interiorH)
    {
        int minDim = Mathf.Min(interiorW, interiorH);
        int maxDim = Mathf.Max(interiorW, interiorH);

        if (minDim >= 20 && maxDim >= 26) return RoomArchetype.Auditorium;
        if (minDim >= 18)                 return RoomArchetype.LargeWorkshop;
        if (minDim >= 11)                 return RoomArchetype.StandardRoom;
        if (minDim >= 5)                  return RoomArchetype.Closet;

        return RoomArchetype.Closet;
    }

    /// <summary>
    /// Attempts to place a room inside a BSP leaf partition. Determines the room archetype from
    /// the leaf dimensions, validates pure wall space, carves the room floor, and connects it
    /// to the hallway network via doorways or branch corridors.
    /// Falls back through smaller archetypes if the primary archetype cannot fit.
    /// </summary>
    private bool TryPlaceRoomInBspLeaf(BspNode leaf, System.Random rng)
    {
        RectInt lb = leaf.Bounds;

        // Available interior after reserving 1-tile wall padding on each side
        int availW = lb.width - 2;
        int availH = lb.height - 2;
        if (availW < 5 || availH < 5) return false;

        // Determine archetype tiers to try (best fit → fallbacks)
        RoomArchetype primary = ClassifyBspLeafArchetype(availW, availH);
        RoomArchetype[] tiers = primary switch
        {
            RoomArchetype.Auditorium =>
                new[] { RoomArchetype.Auditorium, RoomArchetype.LargeWorkshop, RoomArchetype.StandardRoom },
            RoomArchetype.LargeWorkshop =>
                new[] { RoomArchetype.LargeWorkshop, RoomArchetype.StandardRoom, RoomArchetype.Closet },
            RoomArchetype.StandardRoom =>
                new[] { RoomArchetype.StandardRoom, RoomArchetype.Closet },
            _ =>
                new[] { RoomArchetype.Closet }
        };

        for (int tier = 0; tier < tiers.Length; tier++)
        {
            RoomArchetype tryArch = tiers[tier];
            int attemptsPerTier = tryArch == RoomArchetype.Closet ? 6 : 10;

            for (int attempt = 0; attempt < attemptsPerTier; attempt++)
            {
                // Generate room dimensions within archetype range, clamped to leaf interior
                bool useSmallerFallback = attempt > attemptsPerTier / 2;
                GetDimensionsForArchetype(tryArch, rng, useSmallerFallback, out int rw, out int rh);
                rw = Mathf.Min(rw, availW);
                rh = Mathf.Min(rh, availH);
                if (rw < 5 || rh < 5) continue;

                // Position the room within the leaf with randomized offset from the padding edge
                int maxOffX = Mathf.Max(0, availW - rw);
                int maxOffY = Mathf.Max(0, availH - rh);
                int rx = lb.xMin + 1 + (maxOffX > 0 ? rng.Next(0, maxOffX + 1) : 0);
                int ry = lb.yMin + 1 + (maxOffY > 0 ? rng.Next(0, maxOffY + 1) : 0);

                // Validate that room footprint (plus 1-tile wall border) is pure uncarved wall
                if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

                var candidate = new RectInt(rx, ry, rw, rh);

                // Connect room to hallway network via doorway or branch corridor
                int doorSpan = tryArch == RoomArchetype.Closet ? 3 : GetDoorwaySpan();
                Vector2Int doorCell;
                bool horizontalDoor;
                bool connected =
                    TryFindHallwayDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor) ||
                    TryCarveBranchHallwayToRoom(candidate, doorSpan, out doorCell, out horizontalDoor) ||
                    TryFindAdjacentRoomDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor);

                if (!connected) continue;

                // Carve the room floor tiles
                for (int x = rx; x < rx + rw; x++)
                    for (int y = ry; y < ry + rh; y++)
                        _grid[x, y] = CellType.RoomFloor;

                // Carve the connecting doorway
                if (horizontalDoor)
                    CarveHorizontalDoorway(doorCell.x, doorCell.y, doorSpan);
                else
                    CarveVerticalDoorway(doorCell.x, doorCell.y, doorSpan);

                // Register room with archetype-appropriate door and window targets
                GetArchetypeDoorAndWindowTargets(tryArch, rng, out int targetDoors, out int targetWindows);
                _rooms.Add(new RoomData
                {
                    FloorLevel = _currentFloor.FloorLevel,
                    Bounds = candidate,
                    Archetype = tryArch,
                    IsObjectiveRoom = false,
                    TargetDoorways = targetDoors,
                    ActualDoorways = 1,
                    TargetWindows = targetWindows
                });

                return true;
            }
        }

        return false;
    }

    private static void GetDimensionsForArchetype(
        RoomArchetype archetype,
        System.Random rng,
        bool slightlySmallerFallback,
        out int width,
        out int height)
    {
        switch (archetype)
        {
            case RoomArchetype.Closet:
                width = rng.Next(5, 9);
                height = rng.Next(5, 9);
                break;

            case RoomArchetype.StandardRoom:
                width = slightlySmallerFallback ? rng.Next(10, 15) : rng.Next(11, 18);
                height = slightlySmallerFallback ? rng.Next(10, 15) : rng.Next(11, 18);
                break;

            case RoomArchetype.LargeWorkshop:
                width = slightlySmallerFallback ? rng.Next(16, 21) : rng.Next(18, 25);
                height = slightlySmallerFallback ? rng.Next(16, 21) : rng.Next(18, 25);
                break;

            case RoomArchetype.Auditorium:
                bool wideOrientation = rng.Next(2) == 0;
                int longSide = slightlySmallerFallback ? rng.Next(23, 28) : rng.Next(26, 33);
                int shortSide = slightlySmallerFallback ? rng.Next(18, 23) : rng.Next(20, 27);
                width = wideOrientation ? longSide : shortSide;
                height = wideOrientation ? shortSide : longSide;
                break;

            default:
                width = 24;
                height = 24;
                break;
        }
    }

    private static void GetArchetypeDoorAndWindowTargets(
        RoomArchetype archetype,
        System.Random rng,
        out int targetDoors,
        out int targetWindows)
    {
        switch (archetype)
        {
            case RoomArchetype.Closet:
                targetDoors = 1;
                targetWindows = 0;
                break;

            case RoomArchetype.StandardRoom:
                targetDoors = rng.Next(1, 4);
                targetWindows = rng.Next(1, 3);
                break;

            case RoomArchetype.LargeWorkshop:
                targetDoors = rng.Next(2, 5);
                targetWindows = rng.Next(2, 4);
                break;

            case RoomArchetype.Auditorium:
                targetDoors = rng.Next(3, 5);
                targetWindows = rng.Next(2, 5);
                break;

            default:
                targetDoors = 4;
                targetWindows = 3;
                break;
        }
    }

    private void ConnectRoomsWithSecondaryHallwaysAndDoors(System.Random rng, RectInt ringOuter)
    {
        for (int i = 1; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.TargetDoorways <= 1) continue;

            RectInt bounds = room.Bounds;
            int[] wallOrder = { 0, 1, 2, 3 };
            for (int k = 3; k > 0; k--)
            {
                int swap = rng.Next(k + 1);
                (wallOrder[k], wallOrder[swap]) = (wallOrder[swap], wallOrder[k]);
            }

            for (int w = 0; w < 4 && GetRoomDoorwayWallCount(bounds) < room.TargetDoorways; w++)
            {
                int side = wallOrder[w];
                if (DoesRoomWallHaveDoorway(bounds, side)) continue;

                TryConnectRoomWallOutward(bounds, side);
            }
        }

        for (int i = 1; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            int minRequired = room.Archetype switch
            {
                RoomArchetype.Auditorium => 3,
                RoomArchetype.LargeWorkshop => 2,
                _ => room.TargetDoorways
            };

            int safety = 0;
            while (GetRoomDoorwayWallCount(room.Bounds) < minRequired && safety < 3)
            {
                ForceSecondHallwayEntranceForRoom(room.Bounds, ringOuter);
                safety++;
            }
        }
    }

    // ──────────────────────────── Breakable Windows ────────────────────────

    private void PlaceRoomWindows(System.Random rng)
    {
        const int windowSpan = 3;

        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.TargetWindows <= 0) continue;

            int placed = 0;
            int[] wallOrder = { 0, 1, 2, 3 };
            for (int k = 3; k > 0; k--)
            {
                int swap = rng.Next(k + 1);
                (wallOrder[k], wallOrder[swap]) = (wallOrder[swap], wallOrder[k]);
            }

            for (int w = 0; w < 4 && placed < room.TargetWindows; w++)
            {
                if (TryPlaceWindowOnRoomWall(room.Bounds, wallOrder[w], windowSpan, rng))
                {
                    placed++;
                }
            }
        }
    }

    private bool TryPlaceWindowOnRoomWall(RectInt room, int wallSide, int span, System.Random rng)
    {
        var candidates = new List<(Vector2Int startCell, bool horizontal)>();

        if (wallSide == 0)
        {
            int wy = room.yMax;
            if (wy + 1 < _height - 1)
            {
                for (int x = room.xMin + 2; x <= room.xMax - 2 - span; x++)
                {
                    if (IsValidWindowSpan(x, wy, span, true, 0, 1))
                        candidates.Add((new Vector2Int(x, wy), true));
                }
            }
        }
        else if (wallSide == 2)
        {
            int wy = room.yMin - 1;
            if (wy - 1 >= 1)
            {
                for (int x = room.xMin + 2; x <= room.xMax - 2 - span; x++)
                {
                    if (IsValidWindowSpan(x, wy, span, true, 0, -1))
                        candidates.Add((new Vector2Int(x, wy), true));
                }
            }
        }
        else if (wallSide == 1)
        {
            int wx = room.xMax;
            if (wx + 1 < _width - 1)
            {
                for (int y = room.yMin + 2; y <= room.yMax - 2 - span; y++)
                {
                    if (IsValidWindowSpan(wx, y, span, false, 1, 0))
                        candidates.Add((new Vector2Int(wx, y), false));
                }
            }
        }
        else if (wallSide == 3)
        {
            int wx = room.xMin - 1;
            if (wx - 1 >= 1)
            {
                for (int y = room.yMin + 2; y <= room.yMax - 2 - span; y++)
                {
                    if (IsValidWindowSpan(wx, y, span, false, -1, 0))
                        candidates.Add((new Vector2Int(wx, y), false));
                }
            }
        }

        if (candidates.Count == 0) return false;

        var chosen = candidates[rng.Next(candidates.Count)];
        for (int s = 0; s < span; s++)
        {
            int gx = chosen.horizontal ? chosen.startCell.x + s : chosen.startCell.x;
            int gy = chosen.horizontal ? chosen.startCell.y : chosen.startCell.y + s;
            _grid[gx, gy] = CellType.Window;
        }

        float centerGx = chosen.horizontal ? chosen.startCell.x + (span - 1) * 0.5f : chosen.startCell.x;
        float centerGy = chosen.horizontal ? chosen.startCell.y : chosen.startCell.y + (span - 1) * 0.5f;
        Vector2 worldCenter = GridToWorld(centerGx, centerGy);
        Vector2 windowSize = chosen.horizontal ? new Vector2(span, 0.92f) : new Vector2(0.92f, span);

        int winIdx = _spawnedWindows.Count;
        var winGo = new GameObject($"BreakableWindow_{winIdx}");
        winGo.transform.SetParent(_currentFloor.FloorRoot.transform, false);
        var window = winGo.AddComponent<BreakableWindow>();
        window.Initialize(winIdx, this, worldCenter, windowSize, chosen.horizontal);
        _spawnedWindows.Add(window);

        return true;
    }

    private bool IsValidWindowSpan(int startX, int startY, int span, bool horizontal, int outDx, int outDy)
    {
        for (int s = -2; s < span + 2; s++)
        {
            int gx = horizontal ? startX + s : startX;
            int gy = horizontal ? startY : startY + s;
            if (gx <= 1 || gx >= _width - 2 || gy <= 1 || gy >= _height - 2) return false;
            if (_grid[gx, gy] != CellType.Wall) return false;
        }

        for (int s = 0; s < span; s++)
        {
            int gx = horizontal ? startX + s : startX;
            int gy = horizontal ? startY : startY + s;

            CellType outside = _grid[gx + outDx, gy + outDy];
            CellType inside = _grid[gx - outDx, gy - outDy];

            bool validOutside = outside == CellType.HallwayFloor ||
                                outside == CellType.RoomFloor ||
                                outside == CellType.ObjectiveRoomFloor;
            bool validInside = inside == CellType.RoomFloor ||
                               inside == CellType.ObjectiveRoomFloor;

            if (!validOutside || !validInside) return false;
        }

        return true;
    }

    // ──────────────────────────── Door & Corridor Helpers ──────────────────

    private int GetRoomDoorwayWallCount(RectInt room)
    {
        int count = 0;
        for (int side = 0; side < 4; side++)
        {
            if (DoesRoomWallHaveDoorway(room, side)) count++;
        }
        return count;
    }

    private bool DoesRoomWallHaveDoorway(RectInt room, int wallSide)
    {
        if (wallSide == 0)
        {
            int y = room.yMax;
            if (y < 0 || y >= _height) return false;
            for (int x = room.xMin; x < room.xMax; x++)
                if (_grid[x, y] == CellType.Doorway) return true;
        }
        else if (wallSide == 1)
        {
            int x = room.xMax;
            if (x < 0 || x >= _width) return false;
            for (int y = room.yMin; y < room.yMax; y++)
                if (_grid[x, y] == CellType.Doorway) return true;
        }
        else if (wallSide == 2)
        {
            int y = room.yMin - 1;
            if (y < 0 || y >= _height) return false;
            for (int x = room.xMin; x < room.xMax; x++)
                if (_grid[x, y] == CellType.Doorway) return true;
        }
        else if (wallSide == 3)
        {
            int x = room.xMin - 1;
            if (x < 0 || x >= _width) return false;
            for (int y = room.yMin; y < room.yMax; y++)
                if (_grid[x, y] == CellType.Doorway) return true;
        }
        return false;
    }

    private bool TryConnectRoomWallOutward(RectInt room, int wallSide)
    {
        // Always keep hallways at full configured width (>= 1.5x player body), never narrower!
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 3 : GetDoorwaySpan();
        if (room.width - 2 < doorSpan || room.height - 2 < doorSpan) return false;

        int doorX = Mathf.Clamp(room.xMin + (room.width - doorSpan) / 2, room.xMin + 1, room.xMax - doorSpan - 1);
        int doorY = Mathf.Clamp(room.yMin + (room.height - doorSpan) / 2, room.yMin + 1, room.yMax - doorSpan - 1);
        int hallStartX = Mathf.Clamp(doorX - (hw - doorSpan) / 2, 2, _width - hw - 2);
        int hallStartY = Mathf.Clamp(doorY - (hw - doorSpan) / 2, 2, _height - hw - 2);
        const int maxDist = 36;

        if (wallSide == 0)
        {
            if (doorX + doorSpan >= _width - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cy = room.yMax + dist;
                if (cy >= _height - 2) break;

                if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                {
                    for (int y = room.yMax + 1; y < cy; y++)
                        for (int w = 0; w < hw; w++)
                            SetHallwayIfWall(hallStartX + w, y);

                    CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                    return true;
                }

                if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                {
                    if (dist == 1)
                    {
                        CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                    }
                    else
                    {
                        for (int y = room.yMax + 1; y <= cy - 2; y++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(hallStartX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                        CarveHorizontalDoorway(doorX, cy - 1, doorSpan);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 2)
        {
            if (doorX + doorSpan >= _width - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cy = room.yMin - 1 - dist;
                if (cy <= 1) break;

                if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                {
                    for (int y = cy + 1; y <= room.yMin - 2; y++)
                        for (int w = 0; w < hw; w++)
                            SetHallwayIfWall(hallStartX + w, y);

                    CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                    return true;
                }

                if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                {
                    if (dist == 1)
                    {
                        CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                    }
                    else
                    {
                        for (int y = cy + 2; y <= room.yMin - 2; y++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(hallStartX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                        CarveHorizontalDoorway(doorX, cy + 1, doorSpan);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 1)
        {
            if (doorY + doorSpan >= _height - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cx = room.xMax + dist;
                if (cx >= _width - 2) break;

                if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                {
                    for (int x = room.xMax + 1; x < cx; x++)
                        for (int w = 0; w < hw; w++)
                            SetHallwayIfWall(x, hallStartY + w);

                    CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                    return true;
                }

                if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                {
                    if (dist == 1)
                    {
                        CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                    }
                    else
                    {
                        for (int x = room.xMax + 1; x <= cx - 2; x++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(x, hallStartY + w);

                        CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                        CarveVerticalDoorway(cx - 1, doorY, doorSpan);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 3)
        {
            if (doorY + doorSpan >= _height - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cx = room.xMin - 1 - dist;
                if (cx <= 1) break;

                if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                {
                    for (int x = cx + 1; x <= room.xMin - 2; x++)
                        for (int w = 0; w < hw; w++)
                            SetHallwayIfWall(x, hallStartY + w);

                    CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                    return true;
                }

                if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                {
                    if (dist == 1)
                    {
                        CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                    }
                    else
                    {
                        for (int x = cx + 2; x <= room.xMin - 2; x++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(x, hallStartY + w);

                        CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                        CarveVerticalDoorway(cx + 1, doorY, doorSpan);
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private bool SpanMatchesCellType(int startX, int startY, int span, bool horizontal, CellType expected)
    {
        for (int s = 0; s < span; s++)
        {
            int x = horizontal ? startX + s : startX;
            int y = horizontal ? startY : startY + s;
            if (x <= 0 || x >= _width - 1 || y <= 0 || y >= _height - 1) return false;
            if (_grid[x, y] != expected) return false;
        }
        return true;
    }

    private bool SpanMatchesRoomFloor(int startX, int startY, int span, bool horizontal)
    {
        for (int s = 0; s < span; s++)
        {
            int x = horizontal ? startX + s : startX;
            int y = horizontal ? startY : startY + s;
            if (x <= 0 || x >= _width - 1 || y <= 0 || y >= _height - 1) return false;
            CellType t = _grid[x, y];
            if (t != CellType.RoomFloor && t != CellType.ObjectiveRoomFloor) return false;
        }
        return true;
    }

    private void ForceSecondHallwayEntranceForRoom(RectInt room, RectInt ringOuter)
    {
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 2 : GetDoorwaySpan();

        int doorX = Mathf.Clamp(room.xMin + (room.width - doorSpan) / 2, room.xMin + 1, room.xMax - doorSpan - 1);
        int doorY = Mathf.Clamp(room.yMin + (room.height - doorSpan) / 2, room.yMin + 1, room.yMax - doorSpan - 1);
        int hallStartX = Mathf.Clamp(doorX - (hw - doorSpan) / 2, 2, _width - hw - 2);
        int hallStartY = Mathf.Clamp(doorY - (hw - doorSpan) / 2, 2, _height - hw - 2);

        int centerX = _width / 2;
        int centerY = _height / 2;
        int roomCenterX = room.xMin + room.width / 2;
        int roomCenterY = room.yMin + room.height / 2;

        int[] candidateSides =
        {
            roomCenterY < centerY ? 0 : 2,
            roomCenterX < centerX ? 1 : 3,
            roomCenterY < centerY ? 2 : 0,
            roomCenterX < centerX ? 3 : 1
        };

        for (int i = 0; i < 4; i++)
        {
            int side = candidateSides[i];
            if (DoesRoomWallHaveDoorway(room, side)) continue;

            if (side == 0 && room.yMax + hw + 1 < _height - 1)
            {
                CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                var from = new Vector2Int(hallStartX, room.yMax + 1);
                var to = new Vector2Int(Mathf.Clamp(hallStartX, ringOuter.xMin, ringOuter.xMax - hw), ringOuter.yMax - hw);
                CarveLCorridor(from, to, hw, true);
                return;
            }
            if (side == 2 && room.yMin - 1 - hw > 1)
            {
                CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                var from = new Vector2Int(hallStartX, room.yMin - 1 - hw);
                var to = new Vector2Int(Mathf.Clamp(hallStartX, ringOuter.xMin, ringOuter.xMax - hw), ringOuter.yMin);
                CarveLCorridor(from, to, hw, true);
                return;
            }
            if (side == 1 && room.xMax + hw + 1 < _width - 1)
            {
                CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                var from = new Vector2Int(room.xMax + 1, hallStartY);
                var to = new Vector2Int(ringOuter.xMax - hw, Mathf.Clamp(hallStartY, ringOuter.yMin, ringOuter.yMax - hw));
                CarveLCorridor(from, to, hw, false);
                return;
            }
            if (side == 3 && room.xMin - 1 - hw > 1)
            {
                CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                var from = new Vector2Int(room.xMin - 1 - hw, hallStartY);
                var to = new Vector2Int(ringOuter.xMin, Mathf.Clamp(hallStartY, ringOuter.yMin, ringOuter.yMax - hw));
                CarveLCorridor(from, to, hw, false);
                return;
            }
        }
    }

    private bool TryCarveBranchHallwayToRoom(
        RectInt room,
        int doorSpan,
        out Vector2Int doorCell,
        out bool horizontalDoor)
    {
        // Always carve full-width hallways (>= 1.5x player body), even when connecting to a small Closet!
        int hw = GetConfiguredHallwayWidth();
        const int maxBranchLength = 40;

        int doorX = Mathf.Clamp(room.xMin + (room.width - doorSpan) / 2, room.xMin + 1, room.xMax - doorSpan - 1);
        int doorY = Mathf.Clamp(room.yMin + (room.height - doorSpan) / 2, room.yMin + 1, room.yMax - doorSpan - 1);
        int hallStartX = Mathf.Clamp(doorX - (hw - doorSpan) / 2, 2, _width - hw - 2);
        int hallStartY = Mathf.Clamp(doorY - (hw - doorSpan) / 2, 2, _height - hw - 2);

        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkY = room.yMax + dist;
            if (checkY >= _height - 2) break;
            if (_grid[hallStartX, checkY] == CellType.HallwayFloor && _grid[hallStartX + hw - 1, checkY] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(hallStartX, room.yMax + 1, hw, dist - 1))
                {
                    for (int y = room.yMax + 1; y < checkY; y++)
                        for (int w = 0; w < hw; w++)
                            _grid[hallStartX + w, y] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(doorX, room.yMax);
                    horizontalDoor = true;
                    return true;
                }
                break;
            }
            if (_grid[hallStartX, checkY] != CellType.Wall) break;
        }

        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkY = room.yMin - 1 - dist;
            if (checkY <= 1) break;
            if (_grid[hallStartX, checkY] == CellType.HallwayFloor && _grid[hallStartX + hw - 1, checkY] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(hallStartX, checkY + 1, hw, dist - 1))
                {
                    for (int y = checkY + 1; y <= room.yMin - 2; y++)
                        for (int w = 0; w < hw; w++)
                            _grid[hallStartX + w, y] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(doorX, room.yMin - 1);
                    horizontalDoor = true;
                    return true;
                }
                break;
            }
            if (_grid[hallStartX, checkY] != CellType.Wall) break;
        }

        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkX = room.xMax + dist;
            if (checkX >= _width - 2) break;
            if (_grid[checkX, hallStartY] == CellType.HallwayFloor && _grid[checkX, hallStartY + hw - 1] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(room.xMax + 1, hallStartY, dist - 1, hw))
                {
                    for (int x = room.xMax + 1; x < checkX; x++)
                        for (int w = 0; w < hw; w++)
                            _grid[x, hallStartY + w] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(room.xMax, doorY);
                    horizontalDoor = false;
                    return true;
                }
                break;
            }
            if (_grid[checkX, hallStartY] != CellType.Wall) break;
        }

        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkX = room.xMin - 1 - dist;
            if (checkX <= 1) break;
            if (_grid[checkX, hallStartY] == CellType.HallwayFloor && _grid[checkX, hallStartY + hw - 1] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(checkX + 1, hallStartY, dist - 1, hw))
                {
                    for (int x = checkX + 1; x <= room.xMin - 2; x++)
                        for (int w = 0; w < hw; w++)
                            _grid[x, hallStartY + w] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(room.xMin - 1, doorY);
                    horizontalDoor = false;
                    return true;
                }
                break;
            }
            if (_grid[checkX, hallStartY] != CellType.Wall) break;
        }

        doorCell = default;
        horizontalDoor = false;
        return false;
    }

    private bool TryFindAdjacentRoomDoorwaySpot(
        RectInt room,
        int doorSpan,
        System.Random rng,
        out Vector2Int doorCell,
        out bool horizontalDoor)
    {
        var candidates = new List<(Vector2Int cell, bool horiz)>();

        if (room.yMax + 1 < _height - 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
            {
                bool allFloor = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[x + s, room.yMax + 1] != CellType.RoomFloor) { allFloor = false; break; }
                }
                if (allFloor) candidates.Add((new Vector2Int(x, room.yMax), true));
            }
        }

        if (room.yMin - 2 >= 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
            {
                bool allFloor = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[x + s, room.yMin - 2] != CellType.RoomFloor) { allFloor = false; break; }
                }
                if (allFloor) candidates.Add((new Vector2Int(x, room.yMin - 1), true));
            }
        }

        if (room.xMax + 1 < _width - 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
            {
                bool allFloor = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[room.xMax + 1, y + s] != CellType.RoomFloor) { allFloor = false; break; }
                }
                if (allFloor) candidates.Add((new Vector2Int(room.xMax, y), false));
            }
        }

        if (room.xMin - 2 >= 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
            {
                bool allFloor = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[room.xMin - 2, y + s] != CellType.RoomFloor) { allFloor = false; break; }
                }
                if (allFloor) candidates.Add((new Vector2Int(room.xMin - 1, y), false));
            }
        }

        if (candidates.Count == 0)
        {
            doorCell = default;
            horizontalDoor = false;
            return false;
        }

        var chosen = candidates[rng.Next(candidates.Count)];
        doorCell = chosen.cell;
        horizontalDoor = chosen.horiz;
        return true;
    }

    private bool IsRegionPureWall(int startX, int startY, int w, int h)
    {
        for (int x = startX; x < startX + w; x++)
        {
            for (int y = startY; y < startY + h; y++)
            {
                if (x < 1 || x >= _width - 1 || y < 1 || y >= _height - 1) return false;
                if (_grid[x, y] != CellType.Wall) return false;
                if (_entranceProtectedZone[x, y]) return false;
            }
        }
        return true;
    }

    private bool TryFindHallwayDoorwaySpot(
        RectInt room,
        int doorSpan,
        System.Random rng,
        out Vector2Int doorCell,
        out bool horizontalDoor)
    {
        var candidates = new List<(Vector2Int cell, bool horiz)>();

        if (room.yMax + 1 < _height - 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
            {
                bool allHall = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[x + s, room.yMax + 1] != CellType.HallwayFloor)
                    {
                        allHall = false;
                        break;
                    }
                }
                if (allHall) candidates.Add((new Vector2Int(x, room.yMax), true));
            }
        }

        if (room.yMin - 2 >= 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
            {
                bool allHall = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[x + s, room.yMin - 2] != CellType.HallwayFloor)
                    {
                        allHall = false;
                        break;
                    }
                }
                if (allHall) candidates.Add((new Vector2Int(x, room.yMin - 1), true));
            }
        }

        if (room.xMax + 1 < _width - 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
            {
                bool allHall = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[room.xMax + 1, y + s] != CellType.HallwayFloor)
                    {
                        allHall = false;
                        break;
                    }
                }
                if (allHall) candidates.Add((new Vector2Int(room.xMax, y), false));
            }
        }

        if (room.xMin - 2 >= 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
            {
                bool allHall = true;
                for (int s = 0; s < doorSpan; s++)
                {
                    if (_grid[room.xMin - 2, y + s] != CellType.HallwayFloor)
                    {
                        allHall = false;
                        break;
                    }
                }
                if (allHall) candidates.Add((new Vector2Int(room.xMin - 1, y), false));
            }
        }

        if (candidates.Count == 0)
        {
            doorCell = default;
            horizontalDoor = false;
            return false;
        }

        var chosen = candidates[rng.Next(candidates.Count)];
        doorCell = chosen.cell;
        horizontalDoor = chosen.horiz;
        return true;
    }

    private void CarveHorizontalDoorway(int x, int y, int doorSpan)
    {
        if (x < 1 || x + doorSpan >= _width - 1 || y < 0 || y >= _height) return;

        for (int s = 0; s < doorSpan; s++)
        {
            _grid[x + s, y] = CellType.Doorway;
            _doorwayCells.Add(new Vector2Int(x + s, y));
        }

        if (_currentFloor != null)
        {
            _currentFloor.DesignatedDoorways.Add(new DesignatedDoorway
            {
                StartX = x,
                StartY = y,
                Span = doorSpan,
                IsHorizontal = true
            });
        }

        MarkDoorwayClearanceZone(x, y - 3, doorSpan, 7);
    }

    private void CarveVerticalDoorway(int x, int y, int doorSpan)
    {
        if (x < 0 || x >= _width || y < 1 || y + doorSpan >= _height - 1) return;

        for (int s = 0; s < doorSpan; s++)
        {
            _grid[x, y + s] = CellType.Doorway;
            _doorwayCells.Add(new Vector2Int(x, y + s));
        }

        if (_currentFloor != null)
        {
            _currentFloor.DesignatedDoorways.Add(new DesignatedDoorway
            {
                StartX = x,
                StartY = y,
                Span = doorSpan,
                IsHorizontal = false
            });
        }

        MarkDoorwayClearanceZone(x - 3, y, 7, doorSpan);
    }

    /// <summary>
    /// Post-carve pass that validates every designated doorway, prevents any door from spawning
    /// inside a solid wall, anchors door hinges flush to solid wall jambs, and scales doors so they
    /// fill 100.0% of the entire doorway width from jamb to jamb.
    /// </summary>
    private void NormalizeAndSpawnAllDoorsForFloor(int floorLevel)
    {
        if (_currentFloor == null) return;

        bool[,] handled = new bool[_width, _height];
        var designated = _currentFloor.DesignatedDoorways;

        for (int i = 0; i < designated.Count; i++)
        {
            DesignatedDoorway dd = designated[i];
            int x = dd.StartX;
            int y = dd.StartY;
            int span = dd.Span;

            if (dd.IsHorizontal)
            {
                if (x < 1 || x + span >= _width - 1 || y < 0 || y >= _height) continue;

                // Check if any tile in this doorway was already handled by an earlier identical doorway
                bool alreadyHandled = false;
                for (int s = 0; s < span; s++)
                {
                    if (handled[x + s, y]) { alreadyHandled = true; break; }
                }
                if (alreadyHandled) continue;

                // ── SAFETY CHECK 1: Ensure doorway connects two open walkable spaces (North & South) ──
                // If y is an outer perimeter wall (y == 0 or y == _height - 1), it opens to the outside courtyard.
                // Otherwise, both y - 1 and y + 1 must have walkable open space. If either side is solid wall across
                // the entire span, this doorway leads into a solid wall — DO NOT spawn a door and seal it!
                bool hasPassageNorth = (y == _height - 1);
                bool hasPassageSouth = (y == 0);

                if (y < _height - 1)
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (IsWalkableFloorOrDoorway(x + s, y + 1)) { hasPassageNorth = true; break; }
                    }
                }
                if (y > 0)
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (IsWalkableFloorOrDoorway(x + s, y - 1)) { hasPassageSouth = true; break; }
                    }
                }

                if (!hasPassageNorth || !hasPassageSouth)
                {
                    // Doorway leads into a solid wall — revert all span tiles to solid Wall so no door can spawn inside!
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x + s, y] == CellType.Doorway)
                        {
                            _grid[x + s, y] = CellType.Wall;
                        }
                    }
                    continue;
                }

                // ── SAFETY CHECK 2: Lock solid wall jambs and set all doorway opening tiles ──
                if (x - 1 >= 0 && _grid[x - 1, y] != CellType.Window)
                {
                    _grid[x - 1, y] = CellType.Wall;
                }
                if (x + span < _width && _grid[x + span, y] != CellType.Window)
                {
                    _grid[x + span, y] = CellType.Wall;
                }
                for (int s = 0; s < span; s++)
                {
                    _grid[x + s, y] = CellType.Doorway;
                    handled[x + s, y] = true;
                }

                // ── SAFETY CHECK 3: Clear any cover pillars immediately in front of or behind doorway ──
                for (int s = 0; s < span; s++)
                {
                    if (y > 0 && _grid[x + s, y - 1] == CellType.CoverPillar)
                        _grid[x + s, y - 1] = CellType.HallwayFloor;
                    if (y < _height - 1 && _grid[x + s, y + 1] == CellType.CoverPillar)
                        _grid[x + s, y + 1] = CellType.HallwayFloor;
                }

                // ── SAFETY CHECK 4: Spawn doors flush to wall jambs, filling 100% of doorway width ──
                float totalLeafLen;
                Vector2 leftJambWorld = GridToWorld(x - 0.5f, y);
                Vector2 rightJambWorld = GridToWorld(x + span - 0.5f, y);

                if (span >= 3)
                {
                    float halfLen = span * 0.5f;
                    SpawnSwingDoor(leftJambWorld, 0f, halfLen);
                    SpawnSwingDoor(rightJambWorld, 180f, halfLen);
                    totalLeafLen = halfLen * 2f;
                }
                else
                {
                    float fullLen = span;
                    SpawnSwingDoor(leftJambWorld, 0f, fullLen);
                    totalLeafLen = fullLen;
                }

                _doorwayRecords.Add(new DoorwaySpanRecord
                {
                    FloorLevel = floorLevel,
                    Horizontal = true,
                    StartX = x,
                    StartY = y,
                    Span = span,
                    TotalDoorLeafLength = totalLeafLen
                });
            }
            else // Vertical doorway
            {
                if (x < 0 || x >= _width || y < 1 || y + span >= _height - 1) continue;

                // Check if any tile in this doorway was already handled
                bool alreadyHandled = false;
                for (int s = 0; s < span; s++)
                {
                    if (handled[x, y + s]) { alreadyHandled = true; break; }
                }
                if (alreadyHandled) continue;

                // ── SAFETY CHECK 1: Ensure doorway connects two open walkable spaces (East & West) ──
                bool hasPassageEast = (x == _width - 1);
                bool hasPassageWest = (x == 0);

                if (x < _width - 1)
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (IsWalkableFloorOrDoorway(x + 1, y + s)) { hasPassageEast = true; break; }
                    }
                }
                if (x > 0)
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (IsWalkableFloorOrDoorway(x - 1, y + s)) { hasPassageWest = true; break; }
                    }
                }

                if (!hasPassageEast || !hasPassageWest)
                {
                    // Doorway leads into a solid wall — revert all span tiles to solid Wall so no door can spawn inside!
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x, y + s] == CellType.Doorway)
                        {
                            _grid[x, y + s] = CellType.Wall;
                        }
                    }
                    continue;
                }

                // ── SAFETY CHECK 2: Lock solid wall jambs and set all doorway opening tiles ──
                if (y - 1 >= 0 && _grid[x, y - 1] != CellType.Window)
                {
                    _grid[x, y - 1] = CellType.Wall;
                }
                if (y + span < _height && _grid[x, y + span] != CellType.Window)
                {
                    _grid[x, y + span] = CellType.Wall;
                }
                for (int s = 0; s < span; s++)
                {
                    _grid[x, y + s] = CellType.Doorway;
                    handled[x, y + s] = true;
                }

                // ── SAFETY CHECK 3: Clear any cover pillars immediately on either side of doorway ──
                for (int s = 0; s < span; s++)
                {
                    if (x > 0 && _grid[x - 1, y + s] == CellType.CoverPillar)
                        _grid[x - 1, y + s] = CellType.HallwayFloor;
                    if (x < _width - 1 && _grid[x + 1, y + s] == CellType.CoverPillar)
                        _grid[x + 1, y + s] = CellType.HallwayFloor;
                }

                // ── SAFETY CHECK 4: Spawn doors flush to wall jambs, filling 100% of doorway width ──
                float totalLeafLen;
                Vector2 bottomJambWorld = GridToWorld(x, y - 0.5f);
                Vector2 topJambWorld = GridToWorld(x, y + span - 0.5f);

                if (span >= 3)
                {
                    float halfLen = span * 0.5f;
                    SpawnSwingDoor(bottomJambWorld, 90f, halfLen);
                    SpawnSwingDoor(topJambWorld, -90f, halfLen);
                    totalLeafLen = halfLen * 2f;
                }
                else
                {
                    float fullLen = span;
                    SpawnSwingDoor(bottomJambWorld, 90f, fullLen);
                    totalLeafLen = fullLen;
                }

                _doorwayRecords.Add(new DoorwaySpanRecord
                {
                    FloorLevel = floorLevel,
                    Horizontal = false,
                    StartX = x,
                    StartY = y,
                    Span = span,
                    TotalDoorLeafLength = totalLeafLen
                });
            }
        }

        // ── SAFETY CHECK 5: Convert any leftover rogue CellType.Doorway tiles to HallwayFloor ──
        for (int x = 0; x < _width; x++)
        {
            for (int y = 0; y < _height; y++)
            {
                if (_grid[x, y] == CellType.Doorway && !handled[x, y])
                {
                    _grid[x, y] = CellType.HallwayFloor;
                }
            }
        }
    }

    private void MarkDoorwayClearanceZone(int startX, int startY, int w, int h)
    {
        for (int x = startX; x < startX + w; x++)
        {
            for (int y = startY; y < startY + h; y++)
            {
                if (x >= 0 && x < _width && y >= 0 && y < _height)
                {
                    _entranceProtectedZone[x, y] = true;
                }
            }
        }
    }

    private void SpawnSwingDoor(Vector2 hingeWorld, float closedAngleDeg, float length)
    {
        int idx = _spawnedDoors.Count;
        var doorGo = new GameObject($"SwingDoor_{idx}");
        doorGo.transform.SetParent(_currentFloor.FloorRoot.transform, false);

        var door = doorGo.AddComponent<SwingDoor>();
        Color doorColor = _config != null ? _config.DoorColor : new Color(0.45f, 0.38f, 0.32f, 1f);
        door.Initialize(idx, this, hingeWorld, closedAngleDeg, length, doorColor);

        _spawnedDoors.Add(door);
    }

    private void PlaceInteriorCoverPillars(System.Random rng)
    {
        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.Bounds.width < 7 || room.Bounds.height < 7) continue;

            int pillarSize = room.Archetype == RoomArchetype.Closet ? 1 : 2;
            int targetCount = room.Archetype switch
            {
                RoomArchetype.Closet => rng.Next(0, 2),
                RoomArchetype.StandardRoom => rng.Next(2, 4),
                RoomArchetype.LargeWorkshop => rng.Next(3, 6),
                RoomArchetype.Auditorium => rng.Next(5, 8),
                _ => rng.Next(4, 6)
            };

            int placed = 0;
            int maxAttempts = 36;

            for (int attempt = 0; attempt < maxAttempts && placed < targetCount; attempt++)
            {
                int px = rng.Next(room.Bounds.xMin + 2, room.Bounds.xMax - 1 - pillarSize);
                int py = rng.Next(room.Bounds.yMin + 2, room.Bounds.yMax - 1 - pillarSize);

                if (_entranceProtectedZone[px, py] || IsNearAnyDoorwayOrEntrance(px, py, 4.5f))
                {
                    continue;
                }

                if (room.Archetype == RoomArchetype.ObjectiveHub)
                {
                    int cx = room.Bounds.xMin + room.Bounds.width / 2;
                    int cy = room.Bounds.yMin + room.Bounds.height / 2;
                    if (Mathf.Abs(px - cx) <= 4 && Mathf.Abs(py - cy) <= 4) continue;
                }

                if (HasNearbyCoverPillar(px, py, pillarSize + 2))
                {
                    continue;
                }

                for (int dx = 0; dx < pillarSize; dx++)
                {
                    for (int dy = 0; dy < pillarSize; dy++)
                    {
                        _grid[px + dx, py + dy] = CellType.CoverPillar;
                    }
                }
                placed++;
            }
        }
    }

    private bool IsNearAnyDoorwayOrEntrance(int gx, int gy, float minDistance)
    {
        float minDistSq = minDistance * minDistance;
        var cell = new Vector2(gx, gy);

        for (int i = 0; i < _doorwayCells.Count; i++)
        {
            Vector2 d = _doorwayCells[i];
            if ((cell - d).sqrMagnitude < minDistSq) return true;
        }

        if (_currentFloor.FloorLevel == 0)
        {
            for (int e = 0; e < 4; e++)
            {
                Vector2 ent = _entrances[e].GridCell;
                if ((cell - ent).sqrMagnitude < minDistSq) return true;
            }
        }

        return false;
    }

    private bool HasNearbyCoverPillar(int gx, int gy, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int nx = gx + dx;
                int ny = gy + dy;
                if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
                {
                    if (_grid[nx, ny] == CellType.CoverPillar) return true;
                }
            }
        }
        return false;
    }

    private void EnforceFloorSafetyClearance(int floorLevel, RectInt ringOuter)
    {
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = GetDoorwaySpan();
        int doorOffset = Mathf.Max(0, (hw - doorSpan) / 2);
        int vestibuleDepth = Mathf.Max(4, hw + 2);

        if (floorLevel == 0)
        {
            for (int e = 0; e < 4; e++)
            {
                EntranceData ent = _entrances[e];
                int gx = ent.GridCell.x;
                int gy = ent.GridCell.y;

                if (ent.WallSide == 0)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        _grid[gx + w, _height - 1] = (w >= doorOffset && w < doorOffset + doorSpan)
                            ? CellType.Doorway
                            : CellType.Wall;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(gx + w, _height - 1 - d);
                    }
                }
                else if (ent.WallSide == 1)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        _grid[_width - 1, gy + w] = (w >= doorOffset && w < doorOffset + doorSpan)
                            ? CellType.Doorway
                            : CellType.Wall;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(_width - 1 - d, gy + w);
                    }
                }
                else if (ent.WallSide == 2)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        _grid[gx + w, 0] = (w >= doorOffset && w < doorOffset + doorSpan)
                            ? CellType.Doorway
                            : CellType.Wall;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(gx + w, d);
                    }
                }
                else if (ent.WallSide == 3)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        _grid[0, gy + w] = (w >= doorOffset && w < doorOffset + doorSpan)
                            ? CellType.Doorway
                            : CellType.Wall;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(d, gy + w);
                    }
                }
            }
        }

        // Re-enforce stairwell bay walls and landing throats
        CarveAndReserveStairwellBaysForFloor(floorLevel);

        for (int i = 0; i < _doorwayCells.Count; i++)
        {
            Vector2Int dc = _doorwayCells[i];
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    int nx = dc.x + dx;
                    int ny = dc.y + dy;
                    if (nx > 0 && nx < _width - 1 && ny > 0 && ny < _height - 1)
                    {
                        if (_grid[nx, ny] == CellType.CoverPillar)
                        {
                            _grid[nx, ny] = CellType.RoomFloor;
                        }
                    }
                }
            }
        }

        if (floorLevel == 0)
        {
            EnsureAllEntrancesConnectedViaBfs(ringOuter, hw);
        }

        // Safety Pass: Ensure every HallwayFloor passage across the floor is at least >= 1.5x the player's body wide
        EnforceMinimumHallwayWidthOnGrid();
    }

    /// <summary>
    /// Scans all <see cref="CellType.HallwayFloor"/> tiles on the current floor and guarantees that
    /// no hallway segment is narrower than <see cref="GetMinHallwayWidthTiles"/> (&gt;= 1.5x player body diameter).
    /// Any narrow pinch point is automatically widened by converting adjacent <see cref="CellType.Wall"/> tiles.
    /// </summary>
    private void EnforceMinimumHallwayWidthOnGrid()
    {
        int minTiles = GetMinHallwayWidthTiles();

        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 2; y < _height - 2; y++)
            {
                for (int x = 2; x < _width - 2; x++)
                {
                    if (_grid[x, y] != CellType.HallwayFloor) continue;

                    int spanX = GetContiguousHallwayOrOpenSpan(x, y, true, out int leftWallX, out int rightWallX);
                    int spanY = GetContiguousHallwayOrOpenSpan(x, y, false, out int bottomWallY, out int topWallY);

                    // A hallway tile's corridor width is the smaller of its X and Y open spans
                    int narrowSpan = Mathf.Min(spanX, spanY);
                    if (narrowSpan < minTiles)
                    {
                        if (spanX < minTiles)
                        {
                            if (rightWallX < _width - 2 && _grid[rightWallX, y] == CellType.Wall)
                                _grid[rightWallX, y] = CellType.HallwayFloor;
                            else if (leftWallX > 1 && _grid[leftWallX, y] == CellType.Wall)
                                _grid[leftWallX, y] = CellType.HallwayFloor;
                        }

                        if (spanY < minTiles)
                        {
                            if (topWallY < _height - 2 && _grid[x, topWallY] == CellType.Wall)
                                _grid[x, topWallY] = CellType.HallwayFloor;
                            else if (bottomWallY > 1 && _grid[x, bottomWallY] == CellType.Wall)
                                _grid[x, bottomWallY] = CellType.HallwayFloor;
                        }
                    }
                }
            }
        }

        // Measure minimum hallway width after enforcement
        for (int y = 2; y < _height - 2; y++)
        {
            for (int x = 2; x < _width - 2; x++)
            {
                if (_grid[x, y] != CellType.HallwayFloor) continue;

                int spanX = GetContiguousHallwayOrOpenSpan(x, y, true, out _, out _);
                int spanY = GetContiguousHallwayOrOpenSpan(x, y, false, out _, out _);
                int corridorWidth = Mathf.Min(spanX, spanY);
                if (corridorWidth < MinObservedHallwayWidthWorld)
                {
                    MinObservedHallwayWidthWorld = corridorWidth;
                }
            }
        }
    }

    private int GetContiguousHallwayOrOpenSpan(int startX, int startY, bool horizontal, out int negBound, out int posBound)
    {
        int span = 1;
        int neg = horizontal ? startX - 1 : startY - 1;
        while (neg >= 1 && IsWalkableFloorOrDoorway(horizontal ? neg : startX, horizontal ? startY : neg))
        {
            span++;
            neg--;
        }
        negBound = neg;

        int pos = horizontal ? startX + 1 : startY + 1;
        int limit = horizontal ? _width - 1 : _height - 1;
        while (pos < limit && IsWalkableFloorOrDoorway(horizontal ? pos : startX, horizontal ? startY : pos))
        {
            span++;
            pos++;
        }
        posBound = pos;

        return span;
    }

    private bool IsWalkableFloorOrDoorway(int x, int y)
    {
        CellType t = _grid[x, y];
        return t == CellType.HallwayFloor ||
               t == CellType.RoomFloor ||
               t == CellType.ObjectiveRoomFloor ||
               t == CellType.Doorway;
    }

    private void ClearToHallwayFloor(int x, int y)
    {
        if (x <= 0 || x >= _width - 1 || y <= 0 || y >= _height - 1) return;
        if (_grid[x, y] == CellType.Wall || _grid[x, y] == CellType.CoverPillar)
        {
            _grid[x, y] = CellType.HallwayFloor;
        }
    }

    private void EnsureAllEntrancesConnectedViaBfs(RectInt ringOuter, int hw)
    {
        bool[,] reachable = ComputeReachableFromCentralRoom();

        for (int e = 0; e < 4; e++)
        {
            Vector2Int entCell = _entrances[e].GridCell;
            if (!reachable[entCell.x, entCell.y])
            {
                Vector2Int ringTarget = new Vector2Int(
                    Mathf.Clamp(entCell.x, ringOuter.xMin, ringOuter.xMax - hw),
                    Mathf.Clamp(entCell.y, ringOuter.yMin, ringOuter.yMax - hw));
                CarveLCorridor(entCell, ringTarget, hw, _entrances[e].WallSide == 0 || _entrances[e].WallSide == 2);
            }
        }
    }

    private bool[,] ComputeReachableFromCentralRoom()
    {
        bool[,] visited = new bool[_width, _height];
        var queue = new Queue<Vector2Int>();

        RectInt central = _currentFloor.CentralRoomBounds;
        int startX = central.xMin + central.width / 2;
        int startY = central.yMin + central.height / 2;
        var start = new Vector2Int(startX, startY);
        visited[start.x, start.y] = true;
        queue.Enqueue(start);

        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            Vector2Int cur = queue.Dequeue();
            for (int dir = 0; dir < 4; dir++)
            {
                int nx = cur.x + dx[dir];
                int ny = cur.y + dy[dir];
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
                if (visited[nx, ny]) continue;

                if (IsWalkableFloorOrDoorway(nx, ny))
                {
                    visited[nx, ny] = true;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }

        return visited;
    }

    private void BuildWallAndCoverGeometry()
    {
        EnsureTileSprite();

        Color wallCol = _config != null ? _config.WallColor : new Color(0.36f, 0.37f, 0.38f, 1f);
        Color coverCol = _config != null ? _config.CoverColor : new Color(0.28f, 0.29f, 0.31f, 1f);
        Color darkVoidCol = new Color(0.11f, 0.12f, 0.13f, 1f);

        bool[,] visited = new bool[_width, _height];

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                if (visited[x, y]) continue;

                CellType type = _grid[x, y];
                if (type != CellType.Wall && type != CellType.CoverPillar) continue;

                bool isBorderWall = type == CellType.Wall && IsWallBorderingPlayableOrPerimeter(x, y);

                int runW = 1;
                while (x + runW < _width &&
                       !visited[x + runW, y] &&
                       _grid[x + runW, y] == type &&
                       (type != CellType.Wall || IsWallBorderingPlayableOrPerimeter(x + runW, y) == isBorderWall))
                {
                    runW++;
                }

                int runH = 1;
                bool canExpandY = true;
                while (y + runH < _height && canExpandY)
                {
                    for (int dx = 0; dx < runW; dx++)
                    {
                        int cx = x + dx;
                        int cy = y + runH;
                        if (visited[cx, cy] ||
                            _grid[cx, cy] != type ||
                            (type == CellType.Wall && IsWallBorderingPlayableOrPerimeter(cx, cy) != isBorderWall))
                        {
                            canExpandY = false;
                            break;
                        }
                    }
                    if (canExpandY) runH++;
                }

                for (int dy = 0; dy < runH; dy++)
                {
                    for (int dx = 0; dx < runW; dx++)
                    {
                        visited[x + dx, y + dy] = true;
                    }
                }

                Vector2 centerWorld = GridToWorld(x + (runW - 1) * 0.5f, y + (runH - 1) * 0.5f);
                Color blockColor = type == CellType.CoverPillar
                    ? coverCol
                    : (isBorderWall ? wallCol : darkVoidCol);

                CreateMergedBlock(
                    type == CellType.CoverPillar ? "CoverPillar" : (isBorderWall ? "ConcreteWall" : "StructuralFill"),
                    centerWorld,
                    new Vector2(runW, runH),
                    blockColor,
                    addCollider: isBorderWall || type == CellType.CoverPillar,
                    sortingOrder: isBorderWall ? 8 : 4);
            }
        }
    }

    private bool IsWallBorderingPlayableOrPerimeter(int x, int y)
    {
        if (x == 0 || x == _width - 1 || y == 0 || y == _height - 1) return true;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx;
                int ny = y + dy;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;

                CellType neighbor = _grid[nx, ny];
                if (neighbor == CellType.HallwayFloor ||
                    neighbor == CellType.RoomFloor ||
                    neighbor == CellType.ObjectiveRoomFloor ||
                    neighbor == CellType.Doorway ||
                    neighbor == CellType.Window)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void CreateMergedBlock(
        string name,
        Vector2 worldCenter,
        Vector2 size,
        Color color,
        bool addCollider,
        int sortingOrder)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_currentFloor.FloorRoot.transform, false);
        go.transform.position = new Vector3(worldCenter.x, worldCenter.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedTileSprite;
        sr.color = color;
        sr.sortingOrder = sortingOrder;

        if (addCollider)
        {
            var box = go.AddComponent<BoxCollider2D>();
            box.size = Vector2.one;
        }
    }

    private void SpawnHallwayEmergencyLights(int floorLevel, System.Random rng)
    {
        int spacing = _config != null ? _config.HallwayLightSpacing : 12;
        float steadyRatio = _config != null ? _config.SteadyLightRatio : 0.55f;
        float flickerRatio = _config != null ? _config.FlickerLightRatio : 0.35f;
        float intensity = _config != null ? _config.HallwayLightIntensity : 1.2f;
        float radius = _config != null ? _config.HallwayLightRadius : 13f;
        Color color = _config != null ? _config.HallwayLightColor : new Color(0.88f, 0.74f, 0.42f, 1f);

        var placedPositions = new List<Vector2>();
        float minDistSq = spacing * spacing;

        for (int y = 3; y < _height - 3; y++)
        {
            for (int x = 3; x < _width - 3; x++)
            {
                if (_grid[x, y] != CellType.HallwayFloor) continue;

                if (_grid[x - 1, y] == CellType.Wall || _grid[x + 1, y] == CellType.Wall ||
                    _grid[x, y - 1] == CellType.Wall || _grid[x, y + 1] == CellType.Wall)
                {
                    continue;
                }

                Vector2 worldPos = GridToWorld(x, y);
                bool tooClose = false;
                for (int i = 0; i < placedPositions.Count; i++)
                {
                    if ((placedPositions[i] - worldPos).sqrMagnitude < minDistSq)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                placedPositions.Add(worldPos);

                double roll = rng.NextDouble();
                HallwayLightMode mode;
                if (roll < steadyRatio)
                    mode = HallwayLightMode.Steady;
                else if (roll < steadyRatio + flickerRatio)
                    mode = HallwayLightMode.Flickering;
                else
                    mode = HallwayLightMode.Broken;

                var lightGo = new GameObject($"HallwayLight_{placedPositions.Count}");
                lightGo.transform.SetParent(_currentFloor.FloorRoot.transform, false);
                lightGo.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);

                var hl = lightGo.AddComponent<HallwayLight>();
                hl.Initialize(mode, intensity, radius, color, (float)(rng.NextDouble() * 100.0));
            }
        }

        if (floorLevel == 0)
        {
            for (int e = 0; e < 4; e++)
            {
                var entLightGo = new GameObject($"EntranceLight_{e}");
                entLightGo.transform.SetParent(_currentFloor.FloorRoot.transform, false);
                entLightGo.transform.position = new Vector3(_entrances[e].WorldPosition.x, _entrances[e].WorldPosition.y, 0f);

                var ehl = entLightGo.AddComponent<HallwayLight>();
                ehl.Initialize(HallwayLightMode.Steady, intensity * 1.15f, radius * 1.1f, color, e * 19.7f);
            }
        }
    }

    private void PlaceObjectiveAndTeams(System.Random rng)
    {
        _objectiveEntityRenderers.Clear();
        _objectiveEntityColliders.Clear();
        _patrolDummyBody = null;

        Vector2 objCenter = GridToWorld(
            _objectiveRoomBounds.xMin + (_objectiveRoomBounds.width - 1) * 0.5f,
            _objectiveRoomBounds.yMin + (_objectiveRoomBounds.height - 1) * 0.5f);

        _terminalObject = GameObject.Find("ComputerTerminal");
        if (_terminalObject != null)
        {
            _terminalObject.transform.position = new Vector3(objCenter.x, objCenter.y, 0f);

            _terminalLight = _terminalObject.GetComponent<Light2D>();
            if (_terminalLight == null) _terminalLight = _terminalObject.AddComponent<Light2D>();
            _terminalLight.lightType = Light2D.LightType.Point;
            _terminalLight.color = new Color(0.95f, 0.70f, 0.25f, 1f);
            _terminalLight.intensity = _terminalBaseLightIntensity;
            _terminalLight.pointLightOuterRadius = 5.5f;
            _terminalLight.pointLightInnerRadius = 0.6f;

            var termSrs = _terminalObject.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < termSrs.Length; i++)
            {
                Color c = termSrs[i].color;
                c.a = 1f;
                termSrs[i].color = c;
                _objectiveEntityRenderers.Add((termSrs[i], c));
            }
            _objectiveEntityColliders.AddRange(_terminalObject.GetComponentsInChildren<Collider2D>(true));
        }

        int[] entranceOrder = { 0, 1, 2, 3 };
        for (int i = entranceOrder.Length - 1; i > 0; i--)
        {
            int swapIdx = rng.Next(i + 1);
            (entranceOrder[i], entranceOrder[swapIdx]) = (entranceOrder[swapIdx], entranceOrder[i]);
        }

        EntranceData teamAEntrance = _entrances[entranceOrder[0]];
        EntranceData teamBEntrance = _entrances[entranceOrder[1]];

        var zoneA = GameObject.Find("ExtractionZone_TeamA");
        if (zoneA != null)
        {
            zoneA.transform.position = new Vector3(
                teamAEntrance.CourtyardSpawnPosition.x,
                teamAEntrance.CourtyardSpawnPosition.y,
                0f);
        }

        var player = GameObject.Find("Player");
        if (player != null)
        {
            TeleportCharacter(player, teamAEntrance.CourtyardSpawnPosition);
            if (Camera.main != null)
            {
                var tdCam = Camera.main.GetComponent<TopDownCamera>();
                if (tdCam != null) tdCam.SetTarget(player.transform, snapImmediately: true);
            }
        }

        var friendly = GameObject.Find("FriendlyDummy_TeamA");
        if (friendly != null)
        {
            bool horizWall = (teamAEntrance.WallSide == 0 || teamAEntrance.WallSide == 2);
            Vector2 friendlyOffset = horizWall ? new Vector2(3.2f, 0f) : new Vector2(0f, 3.2f);
            Vector2 patrolAxis = horizWall ? new Vector2(2.5f, 0f) : new Vector2(0f, 2.5f);
            Vector2 friendlySpawn = teamAEntrance.CourtyardSpawnPosition + friendlyOffset;
            TeleportCharacter(friendly, friendlySpawn);

            var dc = friendly.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(friendlySpawn, -patrolAxis, patrolAxis);
        }

        var zoneB = GameObject.Find("ExtractionZone_TeamB");
        if (zoneB != null)
        {
            zoneB.transform.position = new Vector3(
                teamBEntrance.CourtyardSpawnPosition.x,
                teamBEntrance.CourtyardSpawnPosition.y,
                0f);
        }

        var enemyStationary = GameObject.Find("EnemyDummy_Stationary");
        if (enemyStationary != null)
        {
            bool horizWallB = (teamBEntrance.WallSide == 0 || teamBEntrance.WallSide == 2);
            Vector2 sideOffsetB = horizWallB ? new Vector2(-3.2f, 0f) : new Vector2(0f, -3.2f);
            Vector2 stationarySpawn = teamBEntrance.CourtyardSpawnPosition + sideOffsetB;
            TeleportCharacter(enemyStationary, stationarySpawn);
            var dc = enemyStationary.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(stationarySpawn, Vector2.zero, Vector2.zero);
        }

        var enemyPatrol = GameObject.Find("EnemyDummy_Patrol");
        if (enemyPatrol != null)
        {
            Vector2 guardPos = FindSafeHallwayGuardPosition(_objectiveFloorLevel);
            TeleportCharacter(enemyPatrol, guardPos);
            var dc = enemyPatrol.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(guardPos, new Vector2(-3.5f, 0f), new Vector2(3.5f, 0f));

            _patrolDummyBody = enemyPatrol.GetComponent<Rigidbody2D>();
            var patrolSrs = enemyPatrol.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < patrolSrs.Length; i++)
            {
                Color c = patrolSrs[i].color;
                c.a = 1f;
                patrolSrs[i].color = c;
                _objectiveEntityRenderers.Add((patrolSrs[i], c));
            }
            _objectiveEntityColliders.AddRange(enemyPatrol.GetComponentsInChildren<Collider2D>(true));
        }
    }

    private Vector2 FindSafeHallwayGuardPosition(int floorLevel)
    {
        if (!_floorStates.TryGetValue(floorLevel, out var state))
        {
            state = _floorStates[0];
        }

        int hw = GetConfiguredHallwayWidth();
        int preferredX = state.CentralRoomBounds.xMin + state.CentralRoomBounds.width / 2;
        int preferredY = state.CentralRoomBounds.yMax + 1 + (hw / 2);

        for (int y = 4; y < _height - 4; y++)
        {
            for (int x = 4; x < _width - 4; x++)
            {
                if (state.Grid[x, y] == CellType.HallwayFloor && !state.ProtectedZone[x, y])
                {
                    return GridToWorld(x, y);
                }
            }
        }

        return GridToWorld(preferredX, preferredY);
    }

    /// <summary>
    /// Validates:
    /// 1. All 4 perimeter entrances are 100% clear of blocking geometry/dummies.
    /// 2. Every hallway across all floors has width &gt;= 1.5x the player's body diameter.
    /// 3. Every doorway across all floors has spawned <see cref="SwingDoor"/>(s) whose leaf length fills 100% of the doorway span.
    /// </summary>
    private void ValidateEntranceHallwayAndDoorSafety()
    {
        Physics2D.SyncTransforms();
        bool allClear = true;

        // 1. Entrance clearance check
        for (int e = 0; e < 4; e++)
        {
            EntranceData ent = _entrances[e];
            Vector2 inwardDir = ent.WallSide switch
            {
                0 => Vector2.down,
                1 => Vector2.left,
                2 => Vector2.up,
                _ => Vector2.right
            };

            Vector2 boxCenter = ent.WorldPosition + inwardDir * 0.5f;
            Vector2 boxSize = (ent.WallSide == 0 || ent.WallSide == 2)
                ? new Vector2(1.4f, 4.0f)
                : new Vector2(4.0f, 1.4f);

            Collider2D[] hits = Physics2D.OverlapBoxAll(boxCenter, boxSize, 0f);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D col = hits[i];
                if (col == null || col.isTrigger) continue;

                if (col.GetComponent<SwingDoor>() != null || col.GetComponentInParent<SwingDoor>() != null)
                {
                    continue;
                }

                if (col.GetComponent<PlayerController>() != null)
                {
                    continue;
                }

                if (col.gameObject.name == "CoverPillar")
                {
                    Destroy(col.gameObject);
                    continue;
                }

                var dummy = col.GetComponent<DummyController>();
                if (dummy != null)
                {
                    Vector2 perp = new Vector2(-inwardDir.y, inwardDir.x) * 2.5f;
                    Vector2 safePos = ent.CourtyardSpawnPosition + perp;
                    TeleportCharacter(dummy.gameObject, safePos);
                    dummy.SetSpawnOrigin(safePos, Vector2.zero, Vector2.zero);
                    continue;
                }

                allClear = false;
            }
        }
        AllEntrancesVerifiedClear = allClear;

        // 2. Hallway minimum width verification (>= 1.5x player body diameter)
        float minRequiredHallwayWidth = GetMinHallwayWidthWorld();
        if (MinObservedHallwayWidthWorld == float.MaxValue)
        {
            MinObservedHallwayWidthWorld = GetConfiguredHallwayWidth();
        }
        AllHallwaysMeetMinWidth = MinObservedHallwayWidthWorld >= minRequiredHallwayWidth;

        // 3. Doorway full-span coverage verification (100% of doorways have doors filling 100% of the doorway length)
        bool doorsValid = _doorwayRecords.Count > 0 && _spawnedDoors.Count >= _doorwayRecords.Count;
        for (int i = 0; i < _doorwayRecords.Count; i++)
        {
            DoorwaySpanRecord rec = _doorwayRecords[i];
            if (Mathf.Abs(rec.TotalDoorLeafLength - rec.Span) > 0.01f)
            {
                doorsValid = false;
                break;
            }
        }
        AllDoorsFillDoorwaysVerified = doorsValid;
    }

    private static void TeleportCharacter(GameObject character, Vector2 worldPos)
    {
        var playerCtrl = character.GetComponent<PlayerController>();
        if (playerCtrl != null)
        {
            playerCtrl.TeleportTo(worldPos);
            return;
        }

        character.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
        var rb = character.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            var prevInterp = rb.interpolation;
            rb.interpolation = RigidbodyInterpolation2D.None;
            rb.position = worldPos;
            rb.linearVelocity = Vector2.zero;
            Physics2D.SyncTransforms();
            rb.interpolation = prevInterp;
        }
    }

    // ──────────────────────────── Helpers & HUD ────────────────────────────

    private Vector2 GridToWorld(float gx, float gy)
    {
        return new Vector2(
            gx - (_width * 0.5f) + 0.5f,
            gy - (_height * 0.5f) + 0.5f);
    }

    private static void EnsureTileSprite()
    {
        if (_cachedTileSprite != null) return;

        const int size = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();

        _cachedTileSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private void OnGUI()
    {
        if (!_showMapSeedHud) return;

        string floorsDesc = string.Join(", ", _activeFloors.ConvertAll(GetFloorDisplayName));
        string stairStatus = CurrentStairClimbProgress >= 0f
            ? $" (Stairs: {Mathf.RoundToInt(CurrentStairClimbProgress * 100f)}%)"
            : string.Empty;

        GUILayout.BeginArea(new Rect(10f, 48f, 520f, 64f));
        GUILayout.BeginVertical("box");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Seed: {MapSeed} | Floors ({_activeFloors.Count}): {floorsDesc}");
        if (GUILayout.Button("New Map Seed", GUILayout.Width(110f), GUILayout.Height(22f)))
        {
            RequestNewRandomMap();
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(
            $"Current: {GetFloorDisplayName(CurrentLocalFloorLevel)}{stairStatus} | " +
            $"Objective: {GetFloorDisplayName(_objectiveFloorLevel)} | " +
            $"Staircases: {_spawnedStairwells.Count}");
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
