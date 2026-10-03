using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Tilemaps;

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
        public readonly List<Tilemap> Tilemaps = new List<Tilemap>();
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
    [SerializeField] private Vector2 _teamASpawnPos;
    [SerializeField] private Vector2 _teamBSpawnPos;
    [SerializeField] private Vector2 _teamCSpawnPos;
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

    /// <summary>Active factory map generation configuration asset.</summary>
    public FactoryMapConfig Config => _config;

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

    /// <summary>True when 100% of doors across all active floors are verified clear of any obstructing cover pillars or walls.</summary>
    public bool AllDoorsClearOfPillarsVerified { get; private set; }

    /// <summary>Minimum hallway width in world units observed across the generated map.</summary>
    public float MinObservedHallwayWidthWorld { get; private set; }

    /// <summary>Minimum number of distinct entrance/exit doorways on any room in the building.</summary>
    public int MinDoorwaysPerRoom { get; private set; }

    /// <summary>Maximum number of distinct entrance/exit doorways on any room in the building.</summary>
    public int MaxDoorwaysPerRoom { get; private set; }

    /// <summary>World position of the Team A (Blue) courtyard spawn point.</summary>
    public Vector2 TeamASpawnPosition => _teamASpawnPos;

    /// <summary>World position of the Team B (Red) courtyard spawn point.</summary>
    public Vector2 TeamBSpawnPosition => _teamBSpawnPos;

    /// <summary>World position of the Team C (Yellow) courtyard spawn point.</summary>
    public Vector2 TeamCSpawnPosition => _teamCSpawnPos;

    /// <summary>Calculated sprint travel time in seconds for each team from spawn to Objective Room doorway.</summary>
    public float[] TeamSprintTravelTimes { get; private set; } = new float[0];

    /// <summary>Maximum difference in sprint travel time (in seconds) between any two teams.</summary>
    public float MaxTeamTravelTimeDelta { get; private set; }

    /// <summary>Cardinal direction (North, East, South, West) for each team spawn.</summary>
    public string[] TeamCardinalDirections { get; private set; } = new string[0];

    /// <summary>True if all teams have distinct cardinal directions (no two teams share the same side).</summary>
    public bool AllTeamsDistinctCardinalsVerified { get; private set; }

    /// <summary>True if all teams are equidistant within tight tolerance (delta &lt;= 0.6 seconds of sprinting).</summary>
    public bool AllTeamsEquidistantVerified { get; private set; }

    /// <summary>Minimum Euclidean distance in world units between any two team spawn points.</summary>
    public float MinObservedTeamSpawnDistanceWorld { get; private set; }

    /// <summary>Minimum Euclidean distance in world units between any two team entrance doors.</summary>
    public float MinObservedTeamDoorDistanceWorld { get; private set; }

    /// <summary>Minimum Euclidean distance in world units between any two team interior breach points.</summary>
    public float MinObservedTeamInteriorDistanceWorld { get; private set; }

    /// <summary>True if all team spawns and doors maintain safe separation distances to prevent premature combat.</summary>
    public bool AllTeamSpawnsSeparatedVerified { get; private set; }

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

            for (int i = 0; i < state.Tilemaps.Count; i++)
            {
                if (state.Tilemaps[i] != null)
                {
                    state.Tilemaps[i].color = new Color(1f, 1f, 1f, alpha);
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

        // Roll building dimensions — support both fixed and seeded-random aspect ratios
        if (_config != null && _config.UseRandomAspectRatio)
        {
            // Roll width independently of height so the building is not constrained to be square
            int wMin = Mathf.Max(60, _config.BuildingWidthMin);
            int wMax = Mathf.Max(wMin + 10, _config.BuildingWidthMax);
            int hMin = Mathf.Max(60, _config.BuildingHeightMin);
            int hMax = Mathf.Max(hMin + 10, _config.BuildingHeightMax);
            // Round to nearest even number so entrances / hallways stay symmetric
            _width  = (rng.Next(wMin, wMax + 1) / 2) * 2;
            _height = (rng.Next(hMin, hMax + 1) / 2) * 2;
        }
        else
        {
            _width  = _config != null ? _config.BuildingWidth  : 108;
            _height = _config != null ? _config.BuildingHeight : 108;
        }

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
        AllDoorsClearOfPillarsVerified = true;
        MinObservedHallwayWidthWorld = float.MaxValue;
        MinObservedTeamSpawnDistanceWorld = float.MaxValue;
        MinObservedTeamDoorDistanceWorld = float.MaxValue;
        MinObservedTeamInteriorDistanceWorld = float.MaxValue;
        AllTeamSpawnsSeparatedVerified = false;
        CurrentLocalFloorLevel = 0;
        CurrentStairClimbProgress = -1f;

        if (_mapRoot != null)
        {
            if (Application.isPlaying)
            {
                _mapRoot.name = "DestroyedFactoryMap";
                _mapRoot.SetActive(false);
                Destroy(_mapRoot);
            }
            else
            {
                DestroyImmediate(_mapRoot);
            }
        }
        var existingRoot = GameObject.Find("GeneratedFactoryMap");
        if (existingRoot != null)
        {
            if (Application.isPlaying)
            {
                existingRoot.name = "DestroyedFactoryMap";
                existingRoot.SetActive(false);
                Destroy(existingRoot);
            }
            else
            {
                DestroyImmediate(existingRoot);
            }
        }
        _mapRoot = new GameObject("GeneratedFactoryMap");

        ApplyAmbientLightingAndFloor();

        // Step 1: Roll the 1-to-3 active floors (strictly enforcing: Basement (-1) and 3rd Floor (2) never coexist)
        DetermineActiveFloors(rng);

        // Step 2: Plan 1–3 physical staircases per floor transition so shafts align vertically across stacked floors
        PlanStaircasesBetweenActiveFloors(rng);

        // Step 3a: Generate layout for each active floor (stairwells, arterial corridors, rooms, doorways, windows, cover)
        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int floorLevel = _activeFloors[i];
            GenerateSingleFloorLayout(floorLevel, rng);
        }

        // Step 3b: Run multi-floor Dijkstra from Objective Room doorways down to Floor 0,
        // and compute & carve optimal equidistant entrances along distinct cardinal building sides
        ComputeAndCarveEquidistantEntrancesAndSpawns(rng);

        // Step 3c: Finalize floor safety clearance, 100% full-span doors, wall colliders, and emergency lights
        int minDoorsAcrossMap = int.MaxValue;
        int maxDoorsAcrossMap = 0;
        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int floorLevel = _activeFloors[i];
            FinalizeFloorGeometryAndEntities(floorLevel, rng, ref minDoorsAcrossMap, ref maxDoorsAcrossMap);
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

        int hw = Mathf.Max(GetMinHallwayWidthTiles(), _config != null ? _config.HallwayWidth : 6);
        int margin = (_config != null ? _config.EntranceCornerMargin : 18) + hw + 2;

        // Bay is 4 tiles wide (BayX..BayX+3), 10 tiles tall (BayMinY..BayMinY+9).
        // Build candidate bay positions along West and East interior margins.
        int westBayX   = margin;
        int westThroatX = westBayX + 4;  // throat opens to the east, into the interior
        int eastBayX   = _width - margin - 4;
        int eastThroatX = eastBayX - 1;  // throat opens to the west, into the interior

        int stairYMargin = Mathf.Max(8, (_config != null ? _config.EntranceCornerMargin : 14));
        int bayYTop = stairYMargin;
        int bayYBot = _height - stairYMargin - 10;
        int bayYMid = (_height - 10) / 2;

        var candidateSlots = new List<(int bayX, int bayMinY, int throatX, bool opensEast)>
        {
            (westBayX, bayYTop, westThroatX, true),
            (eastBayX, bayYBot, eastThroatX, false),
            (westBayX, bayYBot, westThroatX, true),
            (eastBayX, bayYTop, eastThroatX, false)
        };

        // Only include mid-height slots if vertical space permits at least 12 tiles clearance
        if ((bayYMid - bayYTop >= 12) && (bayYBot - bayYMid >= 12))
        {
            candidateSlots.Add((westBayX, bayYMid, westThroatX, true));
            candidateSlots.Add((eastBayX, bayYMid, eastThroatX, false));
        }

        for (int t = 0; t < _activeFloors.Count - 1; t++)
        {
            int lowerFloor = _activeFloors[t];
            int upperFloor = _activeFloors[t + 1];

            // Shuffle available slots
            int[] order = new int[candidateSlots.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int k = order.Length - 1; k > 0; k--)
            {
                int swap = rng.Next(k + 1);
                (order[k], order[swap]) = (order[swap], order[k]);
            }

            int targetCount = rng.Next(1, 3); // 1 or 2 staircases per floor transition
            int added = 0;

            for (int s = 0; s < order.Length && added < targetCount; s++)
            {
                var slot = candidateSlots[order[s]];
                if (IntersectsAnyStairwellBay(slot.bayX, slot.bayX + 3, slot.bayMinY, slot.bayMinY + 9))
                {
                    continue;
                }

                _plannedStaircases.Add(new StaircaseShaftPlan
                {
                    LowerFloor = lowerFloor,
                    UpperFloor = upperFloor,
                    BayX = slot.bayX,
                    BayMinY = slot.bayMinY,
                    ThroatX = slot.throatX,
                    OpensToEastRing = slot.opensEast
                });
                added++;
            }

            // Guaranteed connectivity fallback: if all candidate slots collided, place at least one staircase
            if (added == 0)
            {
                bool placed = false;
                for (int s = 0; s < order.Length; s++)
                {
                    var slot = candidateSlots[order[s]];
                    bool exactMatch = false;
                    for (int p = 0; p < _plannedStaircases.Count; p++)
                    {
                        if (_plannedStaircases[p].BayX == slot.bayX && _plannedStaircases[p].BayMinY == slot.bayMinY)
                        {
                            exactMatch = true;
                            break;
                        }
                    }
                    if (!exactMatch)
                    {
                        _plannedStaircases.Add(new StaircaseShaftPlan
                        {
                            LowerFloor = lowerFloor,
                            UpperFloor = upperFloor,
                            BayX = slot.bayX,
                            BayMinY = slot.bayMinY,
                            ThroatX = slot.throatX,
                            OpensToEastRing = slot.opensEast
                        });
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    var slot = candidateSlots[order[0]];
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
    }

    private void GenerateSingleFloorLayout(int floorLevel, System.Random rng)
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

        // 2. Reserve + carve stairwell bays FIRST so BSP and corridors avoid them
        CarveAndReserveStairwellBaysForFloor(floorLevel);

        // 3. Carve arterial corridors (Floor 0 receives symmetrical arterial spine & cross grid; non-ground floors receive vertical spine & cross grid)
        RectInt ringOuter = new RectInt(2, 2, _width - 4, _height - 4);
        state.RingOuterBounds = ringOuter;
        if (floorLevel == 0)
        {
            CarveFloor0ArterialCorridors(ringOuter);
        }
        else
        {
            CarveNonGroundFloorArterialHallways(rng, ringOuter);
        }

        // 4. Carve ALL rooms via BSP over the full interior.
        //    On the objective floor the largest qualifying leaf is elected as the Objective Room.
        bool isObjectiveFloor = (floorLevel == _objectiveFloorLevel);
        CarveInteriorRoomsViaBsp(rng, isObjectiveFloor);

        // 4b. Synthesize a CentralRoomBounds from the objective/largest room for compatibility
        //     with guard placement and BFS seeding helpers.
        if (_rooms.Count > 0)
        {
            RoomData hub = _rooms[0];
            for (int ri = 1; ri < _rooms.Count; ri++)
            {
                if (_rooms[ri].IsObjectiveRoom) { hub = _rooms[ri]; break; }
                int hubArea = hub.Bounds.width * hub.Bounds.height;
                int riArea  = _rooms[ri].Bounds.width * _rooms[ri].Bounds.height;
                if (riArea > hubArea) hub = _rooms[ri];
            }
            state.CentralRoomBounds = hub.Bounds;
        }

        // 5. Connect rooms with secondary hallways and doorways according to each room's TargetDoorways (1–4)
        ConnectRoomsWithSecondaryHallwaysAndDoors(rng, ringOuter);

        // 6. Place breakable glass windows in room walls bordering hallways or adjacent rooms
        PlaceRoomWindows(rng);

        // 7. Place interior concrete cover pillars scaled to each room's archetype
        PlaceInteriorCoverPillars(rng);
    }

    private void FinalizeFloorGeometryAndEntities(
        int floorLevel,
        System.Random rng,
        ref int minDoorsAcrossMap,
        ref int maxDoorsAcrossMap)
    {
        var state = _floorStates[floorLevel];
        _currentFloor = state;
        _grid = state.Grid;
        _entranceProtectedZone = state.ProtectedZone;
        _rooms = state.Rooms;
        _doorwayCells = state.DoorwayCells;

        RectInt ringOuter = state.RingOuterBounds;

        // 8. Safety pass — enforce 100% entrance, stairwell bay, doorway swing arc, BFS path, and >=1.5x player hallway width
        EnforceFloorSafetyClearance(floorLevel, ringOuter);

        // 8.5. Normalize all Doorway runs (lock flanking Wall jambs) & spawn full-span SwingDoors filling 100% of every doorway
        NormalizeAndSpawnAllDoorsForFloor(floorLevel);

        // 9. Build the tiled floor using the 13 tile floor palette!
        BuildFloorTilemap(state, rng);

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

        state.Tilemaps.Clear();
        state.Tilemaps.AddRange(state.FloorRoot.GetComponentsInChildren<Tilemap>(true));

        state.Lights.Clear();
        state.Lights.AddRange(state.FloorRoot.GetComponentsInChildren<HallwayLight>(true));

        state.Colliders.Clear();
        state.Colliders.AddRange(state.FloorRoot.GetComponentsInChildren<Collider2D>(true));
    }

    private void BuildFloorTilemap(FloorGenerationState state, System.Random rng)
    {
        TileBase[] floorTiles = _config != null ? _config.FloorTiles : null;
        if (floorTiles == null || floorTiles.Length == 0)
        {
            var fallbackList = new List<TileBase>();
            string[] names = {
                "32x32_tile_floor_1", "32x32_tile_floor_2", "32x32_tile_floor_3", "32x32_tile_floor_4",
                "32x32_worn_tile_floor_1", "32x32_worn_tile_floor_2", "32x32_worn_tile_floor_3", "32x32_worn_tile_floor_4",
                "32x32_veryWorn_tile_floor_1", "32x32_veryWorn_tile_floor_2", "32x32_veryWorn_tile_floor_3", "32x32_veryWorn_tile_floor_4",
                "32x32_4tiled_floor"
            };
#if UNITY_EDITOR
            for (int i = 0; i < names.Length; i++)
            {
                var t = UnityEditor.AssetDatabase.LoadAssetAtPath<TileBase>($"Assets/Data/Tiles/{names[i]}.asset");
                if (t != null) fallbackList.Add(t);
            }
#endif
            if (fallbackList.Count > 0)
            {
                floorTiles = fallbackList.ToArray();
            }
        }
        if (floorTiles == null || floorTiles.Length == 0) return;

        var gridGo = new GameObject($"FloorTilemapGrid_{state.FloorLevel}");
        gridGo.transform.SetParent(state.FloorRoot.transform, false);
        gridGo.transform.position = new Vector3(-_width * 0.5f, -_height * 0.5f, 0f);
        var grid = gridGo.AddComponent<Grid>();
        grid.cellSize = new Vector3(1f, 1f, 0f);

        var tilemapGo = new GameObject("FloorTilemap");
        tilemapGo.transform.SetParent(gridGo.transform, false);
        tilemapGo.transform.localPosition = Vector3.zero;
        var tilemap = tilemapGo.AddComponent<Tilemap>();
        var tr = tilemapGo.AddComponent<TilemapRenderer>();
        tr.sortingOrder = 0;

        bool[,] isFloor = new bool[_width, _height];

        // 1. Mark all room footprints
        for (int ri = 0; ri < state.Rooms.Count; ri++)
        {
            RectInt b = state.Rooms[ri].Bounds;
            for (int rx = b.xMin; rx < b.xMax; rx++)
            {
                for (int ry = b.yMin; ry < b.yMax; ry++)
                {
                    if (rx >= 0 && rx < _width && ry >= 0 && ry < _height)
                    {
                        isFloor[rx, ry] = true;
                    }
                }
            }
        }

        // 2. Mark all hallways, doorways, cover pillars, and windows
        for (int x = 0; x < _width; x++)
        {
            for (int y = 0; y < _height; y++)
            {
                CellType ct = state.Grid[x, y];
                if (ct == CellType.HallwayFloor ||
                    ct == CellType.RoomFloor ||
                    ct == CellType.ObjectiveRoomFloor ||
                    ct == CellType.Doorway ||
                    ct == CellType.CoverPillar ||
                    ct == CellType.Window)
                {
                    isFloor[x, y] = true;
                }
            }
        }

        // 3. Batch set tiles
        int totalFloorCount = 0;
        for (int x = 0; x < _width; x++)
        {
            for (int y = 0; y < _height; y++)
            {
                if (isFloor[x, y]) totalFloorCount++;
            }
        }

        var positions = new Vector3Int[totalFloorCount];
        var tilesToSet = new TileBase[totalFloorCount];
        int idx = 0;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                if (!isFloor[x, y]) continue;

                positions[idx] = new Vector3Int(x, y, 0);
                tilesToSet[idx] = floorTiles[rng.Next(floorTiles.Length)];
                idx++;
            }
        }

        tilemap.SetTiles(positions, tilesToSet);
        state.Tilemaps.Add(tilemap);
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

    /// <summary>
    /// Reserves all planned stairwell bays in <c>_entranceProtectedZone</c> and carves the 4x10 stairwell shaft
    /// on any floor connected by that staircase:
    /// - On <c>LowerFloor</c>: opens ONLY the bottom landing entrance (4 tiles wide) into the hallway, while the
    ///   upper portion of the staircase is covered by solid concrete wall so players cannot walk under the stairs.
    /// - On <c>UpperFloor</c>: opens ONLY the top landing entrance (4 tiles wide) into the hallway, while the
    ///   lower drop-off portion is covered by solid concrete wall.
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
                // On the lower floor, open ONLY the bottom landing entrance (y = BayMinY .. BayMinY + 3).
                // The upper portion (y = BayMinY + 4 .. BayMinY + 9) remains solid Wall, covering the side of the stairs!
                for (int y = plan.BayMinY; y < plan.BayMinY + 4; y++)
                {
                    _grid[plan.ThroatX, y] = CellType.HallwayFloor;
                }
                for (int y = plan.BayMinY + 4; y < plan.BayMinY + 10; y++)
                {
                    _grid[plan.ThroatX, y] = CellType.Wall;
                }
            }
            else if (floorLevel == plan.UpperFloor)
            {
                // On the upper floor, open ONLY the top landing entrance (y = BayMinY + 6 .. BayMinY + 9).
                // The lower drop-off portion (y = BayMinY .. BayMinY + 5) remains solid Wall, covering the side of the stairs!
                for (int y = plan.BayMinY; y < plan.BayMinY + 6; y++)
                {
                    _grid[plan.ThroatX, y] = CellType.Wall;
                }
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

    private void CarveFloor0ArterialCorridors(RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = GetConfiguredHallwayWidth();

        int stairMargin = margin + hw + 2;
        int westThroatX = stairMargin + 4;
        int eastThroatX = _width - stairMargin - 5;
        int eastHallX = eastThroatX - hw + 1;
        int spineX = (_width - hw) / 2;

        int crossY = (_height - hw) / 2;
        int stairYMargin = Mathf.Max(8, (_config != null ? _config.EntranceCornerMargin : 14));
        int flankYMin = Mathf.Clamp(Mathf.Min(margin, stairYMargin - 2), 2, _height - 2);
        int flankYMax = Mathf.Clamp(Mathf.Max(_height - margin, _height - stairYMargin + 2), 2, _height - 2);
        int southCrossY = flankYMin;
        int northCrossY = flankYMax - hw;

        bool hasSpine = (eastHallX - (westThroatX + hw) >= 24);

        // 1. Carve North-South arterial corridors across the building footprint (y = 2 .. _height - 2)
        for (int y = 2; y < _height - 2; y++)
        {
            for (int w = 0; w < hw; w++)
            {
                _grid[westThroatX + w, y] = CellType.HallwayFloor;
                if (hasSpine) _grid[spineX + w, y] = CellType.HallwayFloor;
                _grid[eastHallX + w, y]   = CellType.HallwayFloor;
            }
        }

        // 2. Carve 3 East-West arterial cross-corridors across the building footprint (x = 2 .. _width - 2)
        for (int x = 2; x < _width - 2; x++)
        {
            for (int w = 0; w < hw; w++)
            {
                _grid[x, southCrossY + w] = CellType.HallwayFloor;
                _grid[x, crossY + w]      = CellType.HallwayFloor;
                _grid[x, northCrossY + w] = CellType.HallwayFloor;
            }
        }
    }

    private struct HeapItem
    {
        public float Cost;
        public int Index;
    }

    private class FastMinHeap
    {
        private HeapItem[] _items;
        public int Count { get; private set; }

        public FastMinHeap(int capacity)
        {
            _items = new HeapItem[Mathf.Max(64, capacity)];
            Count = 0;
        }

        public void Push(float cost, int index)
        {
            if (Count == _items.Length)
            {
                System.Array.Resize(ref _items, _items.Length * 2);
            }
            int i = Count++;
            _items[i] = new HeapItem { Cost = cost, Index = index };
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_items[parent].Cost <= _items[i].Cost) break;
                var tmp = _items[i];
                _items[i] = _items[parent];
                _items[parent] = tmp;
                i = parent;
            }
        }

        public HeapItem Pop()
        {
            var root = _items[0];
            Count--;
            if (Count > 0)
            {
                _items[0] = _items[Count];
                int i = 0;
                while (true)
                {
                    int left = (i << 1) + 1;
                    if (left >= Count) break;
                    int right = left + 1;
                    int best = (right < Count && _items[right].Cost < _items[left].Cost) ? right : left;
                    if (_items[i].Cost <= _items[best].Cost) break;
                    var tmp = _items[i];
                    _items[i] = _items[best];
                    _items[best] = tmp;
                    i = best;
                }
            }
            return root;
        }
    }

    private struct EntranceCandidate
    {
        public int WallSide;       // 0=North, 1=East, 2=South, 3=West
        public int EdgeCoord;      // x (for North/South) or y (for East/West)
        public int HitCoord;       // y_hit (for North/South) or x_hit (for East/West)
        public float InwardDist;
        public float InteriorDist;
        public float TotalDist;
        public float TravelTime;
        public Vector2 WorldDoorPos;
        public Vector2 WorldSpawnPos;
        public Vector2 WorldInteriorPos;
    }

    /// <summary>
    /// Checks whether all selected entrance candidates maintain safe Euclidean separation
    /// between spawns (exterior), doors (building perimeter), and interior breach points (interior).
    /// </summary>
    private static bool AreCandidatesSafelySeparated(
        EntranceCandidate[] cands,
        float minSpawnSep,
        float minDoorSep,
        float minInteriorSep)
    {
        if (cands == null || cands.Length <= 1) return true;
        for (int i = 0; i < cands.Length; i++)
        {
            for (int j = i + 1; j < cands.Length; j++)
            {
                if (Vector2.Distance(cands[i].WorldSpawnPos, cands[j].WorldSpawnPos) < minSpawnSep)
                    return false;
                if (Vector2.Distance(cands[i].WorldDoorPos, cands[j].WorldDoorPos) < minDoorSep)
                    return false;
                if (Vector2.Distance(cands[i].WorldInteriorPos, cands[j].WorldInteriorPos) < minInteriorSep)
                    return false;
            }
        }
        return true;
    }

    private bool IsEntranceCorridorClean(int side, int edgeCoord, int hitCoord, int hw, bool requireRoomBuffer = true)
    {
        int minX, maxX, minY, maxY;
        if (side == 0) // North
        {
            minX = edgeCoord; maxX = edgeCoord + hw - 1;
            minY = hitCoord;  maxY = _height - 2;
        }
        else if (side == 1) // East
        {
            minX = hitCoord;  maxX = _width - 2;
            minY = edgeCoord; maxY = edgeCoord + hw - 1;
        }
        else if (side == 2) // South
        {
            minX = edgeCoord; maxX = edgeCoord + hw - 1;
            minY = 1;         maxY = hitCoord;
        }
        else // West
        {
            minX = 1;         maxX = hitCoord;
            minY = edgeCoord; maxY = edgeCoord + hw - 1;
        }

        if (minX < 1 || maxX >= _width - 1 || minY < 1 || maxY >= _height - 1) return false;
        if (IntersectsAnyStairwellBay(minX, maxX, minY, maxY)) return false;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                CellType t = _grid[x, y];
                if (t == CellType.RoomFloor || t == CellType.ObjectiveRoomFloor || t == CellType.Doorway)
                    return false;

                if (requireRoomBuffer)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
                            {
                                CellType nt = _grid[nx, ny];
                                if (nt == CellType.RoomFloor || nt == CellType.ObjectiveRoomFloor)
                                    return false;
                            }
                        }
                    }
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Computes multi-floor Dijkstra travel times from the Objective Room doorways down to Floor 0,
    /// evaluates candidate entrance coordinates along all 4 building perimeters, and selects the
    /// combination of distinct cardinal sides that minimizes max travel time delta across all teams.
    /// Carves the selected entrances and spawn points so all teams are equidistant.
    /// </summary>
    private void ComputeAndCarveEquidistantEntrancesAndSpawns(System.Random rng)
    {
        if (!_floorStates.TryGetValue(0, out var floor0State)) return;
        _currentFloor = floor0State;
        _grid = floor0State.Grid;
        _entranceProtectedZone = floor0State.ProtectedZone;
        _rooms = floor0State.Rooms;
        _doorwayCells = floor0State.DoorwayCells;

        // 1. Run multi-floor Dijkstra from Objective Room doorways
        float[] dist = ComputeMultiFloorDijkstraFromObjective();
        int f0Index = _activeFloors.IndexOf(0);

        int hw = GetConfiguredHallwayWidth();
        int doorSpan = GetDoorwaySpan();
        int doorOffset = Mathf.Max(0, (hw - doorSpan) / 2);
        float halfHwOffset = (hw - 1) * 0.5f;
        const float courtyardDist = 10.0f; // Uniform 10-unit setback across all team spawns
        const float playerSprintSpeed = 8.0f; // 5.0 base move speed * 1.6 sprint multiplier

        var candidatesBySide = new List<EntranceCandidate>[4];
        for (int s = 0; s < 4; s++) candidatesBySide[s] = new List<EntranceCandidate>();

        // ── Side 0: North (y = _height - 1) ──
        for (int pass = 0; pass < 2 && candidatesBySide[0].Count == 0; pass++)
        {
            bool allowObjRoomHit = (pass == 1);
            for (int x = 4; x <= _width - 4 - hw; x++)
            {
                if (IntersectsAnyStairwellBay(x, x + hw - 1, _height - 1 - 20, _height - 1)) continue;

                int hitY = -1;
                for (int y = _height - 2; y >= 2; y--)
                {
                    if (IsWalkableTile(_grid[x + hw / 2, y]))
                    {
                        hitY = y;
                        break;
                    }
                }

                if (hitY >= 2 && (_height - 1 - hitY) <= 24)
                {
                    if (_grid[x + hw / 2, hitY] != CellType.HallwayFloor) continue;
                    if (!IsEntranceCorridorClean(0, x, hitY, hw, pass == 0)) continue;

                    int node = (f0Index * _height + hitY) * _width + (x + hw / 2);
                    float interiorDist = dist[node];
                    if (interiorDist < 5000f)
                    {
                        float inwardDist = (_height - 1) - hitY;
                        float totalDist = courtyardDist + inwardDist + interiorDist;
                        float travelTime = totalDist / playerSprintSpeed;

                        candidatesBySide[0].Add(new EntranceCandidate
                        {
                            WallSide = 0,
                            EdgeCoord = x,
                            HitCoord = hitY,
                            InwardDist = inwardDist,
                            InteriorDist = interiorDist,
                            TotalDist = totalDist,
                            TravelTime = travelTime,
                            WorldDoorPos = GridToWorld(x + halfHwOffset, _height - 1),
                            WorldSpawnPos = GridToWorld(x + halfHwOffset, _height - 1 + courtyardDist),
                            WorldInteriorPos = GridToWorld(x + halfHwOffset, hitY)
                        });
                    }
                }
            }
        }

        // ── Side 1: East (x = _width - 1) ──
        for (int pass = 0; pass < 2 && candidatesBySide[1].Count == 0; pass++)
        {
            bool allowObjRoomHit = (pass == 1);
            for (int y = 4; y <= _height - 4 - hw; y++)
            {
                if (IntersectsAnyStairwellBay(_width - 1 - 20, _width - 1, y, y + hw - 1)) continue;

                int hitX = -1;
                for (int x = _width - 2; x >= 2; x--)
                {
                    if (IsWalkableTile(_grid[x, y + hw / 2]))
                    {
                        hitX = x;
                        break;
                    }
                }

                if (hitX >= 2 && (_width - 1 - hitX) <= 24)
                {
                    if (_grid[hitX, y + hw / 2] != CellType.HallwayFloor) continue;
                    if (!IsEntranceCorridorClean(1, y, hitX, hw, pass == 0)) continue;

                    int node = (f0Index * _height + (y + hw / 2)) * _width + hitX;
                    float interiorDist = dist[node];
                    if (interiorDist < 5000f)
                    {
                        float inwardDist = (_width - 1) - hitX;
                        float totalDist = courtyardDist + inwardDist + interiorDist;
                        float travelTime = totalDist / playerSprintSpeed;

                        candidatesBySide[1].Add(new EntranceCandidate
                        {
                            WallSide = 1,
                            EdgeCoord = y,
                            HitCoord = hitX,
                            InwardDist = inwardDist,
                            InteriorDist = interiorDist,
                            TotalDist = totalDist,
                            TravelTime = travelTime,
                            WorldDoorPos = GridToWorld(_width - 1, y + halfHwOffset),
                            WorldSpawnPos = GridToWorld(_width - 1 + courtyardDist, y + halfHwOffset),
                            WorldInteriorPos = GridToWorld(hitX, y + halfHwOffset)
                        });
                    }
                }
            }
        }

        // ── Side 2: South (y = 0) ──
        for (int pass = 0; pass < 2 && candidatesBySide[2].Count == 0; pass++)
        {
            bool allowObjRoomHit = (pass == 1);
            for (int x = 4; x <= _width - 4 - hw; x++)
            {
                if (IntersectsAnyStairwellBay(x, x + hw - 1, 0, 20)) continue;

                int hitY = -1;
                for (int y = 1; y <= _height - 3; y++)
                {
                    if (IsWalkableTile(_grid[x + hw / 2, y]))
                    {
                        hitY = y;
                        break;
                    }
                }

                if (hitY >= 1 && hitY <= 24)
                {
                    if (_grid[x + hw / 2, hitY] != CellType.HallwayFloor) continue;
                    if (!IsEntranceCorridorClean(2, x, hitY, hw, pass == 0)) continue;

                    int node = (f0Index * _height + hitY) * _width + (x + hw / 2);
                    float interiorDist = dist[node];
                    if (interiorDist < 5000f)
                    {
                        float inwardDist = hitY - 0;
                        float totalDist = courtyardDist + inwardDist + interiorDist;
                        float travelTime = totalDist / playerSprintSpeed;

                        candidatesBySide[2].Add(new EntranceCandidate
                        {
                            WallSide = 2,
                            EdgeCoord = x,
                            HitCoord = hitY,
                            InwardDist = inwardDist,
                            InteriorDist = interiorDist,
                            TotalDist = totalDist,
                            TravelTime = travelTime,
                            WorldDoorPos = GridToWorld(x + halfHwOffset, 0),
                            WorldSpawnPos = GridToWorld(x + halfHwOffset, -courtyardDist),
                            WorldInteriorPos = GridToWorld(x + halfHwOffset, hitY)
                        });
                    }
                }
            }
        }

        // ── Side 3: West (x = 0) ──
        for (int pass = 0; pass < 2 && candidatesBySide[3].Count == 0; pass++)
        {
            bool allowObjRoomHit = (pass == 1);
            for (int y = 4; y <= _height - 4 - hw; y++)
            {
                if (IntersectsAnyStairwellBay(0, 20, y, y + hw - 1)) continue;

                int hitX = -1;
                for (int x = 1; x <= _width - 3; x++)
                {
                    if (IsWalkableTile(_grid[x, y + hw / 2]))
                    {
                        hitX = x;
                        break;
                    }
                }

                if (hitX >= 1 && hitX <= 24)
                {
                    if (_grid[hitX, y + hw / 2] != CellType.HallwayFloor) continue;
                    if (!IsEntranceCorridorClean(3, y, hitX, hw, pass == 0)) continue;

                    int node = (f0Index * _height + (y + hw / 2)) * _width + hitX;
                    float interiorDist = dist[node];
                    if (interiorDist < 5000f)
                    {
                        float inwardDist = hitX - 0;
                        float totalDist = courtyardDist + inwardDist + interiorDist;
                        float travelTime = totalDist / playerSprintSpeed;

                        candidatesBySide[3].Add(new EntranceCandidate
                        {
                            WallSide = 3,
                            EdgeCoord = y,
                            HitCoord = hitX,
                            InwardDist = inwardDist,
                            InteriorDist = interiorDist,
                            TotalDist = totalDist,
                            TravelTime = travelTime,
                            WorldDoorPos = GridToWorld(0, y + halfHwOffset),
                            WorldSpawnPos = GridToWorld(-courtyardDist, y + halfHwOffset),
                            WorldInteriorPos = GridToWorld(hitX, y + halfHwOffset)
                        });
                    }
                }
            }
        }

        // Sort candidates on each side by travel time
        for (int s = 0; s < 4; s++)
        {
            candidatesBySide[s].Sort((a, b) => a.TravelTime.CompareTo(b.TravelTime));
        }

        // Determine team count dynamically
        int teamCount = 3;
        if (GameObject.Find("ExtractionZone_TeamD") != null) teamCount = 4;
        else if (GameObject.Find("ExtractionZone_TeamC") != null) teamCount = 3;
        else if (GameObject.Find("ExtractionZone_TeamB") != null) teamCount = 2;

        float minSpawnSep = Mathf.Min(45.0f, Mathf.Min(_width, _height) * 0.45f);
        float minDoorSep = Mathf.Min(30.0f, Mathf.Min(_width, _height) * 0.30f);
        float minInteriorSep = Mathf.Min(18.0f, Mathf.Min(_width, _height) * 0.20f);

        float bestDelta = float.MaxValue;
        EntranceCandidate[] bestTeamCandidates = null;

        // Multi-tier search:
        // Tier 0: full separation thresholds (minSpawnSep, minDoorSep, minInteriorSep)
        // Tier 1: relaxed separation thresholds (70%)
        // Tier 2: unconstrained fallback
        for (int tier = 0; tier < 3 && bestTeamCandidates == null; tier++)
        {
            float curMinSpawn = tier == 0 ? minSpawnSep : (tier == 1 ? minSpawnSep * 0.7f : 0f);
            float curMinDoor = tier == 0 ? minDoorSep : (tier == 1 ? minDoorSep * 0.7f : 0f);
            float curMinInterior = tier == 0 ? minInteriorSep : (tier == 1 ? minInteriorSep * 0.7f : 0f);

            if (teamCount == 3)
            {
                int[][] sideCombos = new int[][]
                {
                    new int[] { 0, 1, 2 },
                    new int[] { 0, 1, 3 },
                    new int[] { 0, 2, 3 },
                    new int[] { 1, 2, 3 }
                };

                for (int comboIdx = 0; comboIdx < sideCombos.Length; comboIdx++)
                {
                    int[] sides = sideCombos[comboIdx];
                    var list0 = candidatesBySide[sides[0]];
                    var list1 = candidatesBySide[sides[1]];
                    var list2 = candidatesBySide[sides[2]];
                    if (list0.Count == 0 || list1.Count == 0 || list2.Count == 0) continue;

                    for (int i0 = 0; i0 < list0.Count; i0++)
                    {
                        var c0 = list0[i0];
                        float t0 = c0.TravelTime;
                        int idx1 = FindClosestIndex(list1, t0);
                        int idx2 = FindClosestIndex(list2, t0);

                        int k1Min = idx1;
                        while (k1Min > 0 && t0 - list1[k1Min - 1].TravelTime <= 0.65f) k1Min--;
                        k1Min = Mathf.Min(k1Min, Mathf.Max(0, idx1 - 8));

                        int k1Max = idx1;
                        while (k1Max < list1.Count - 1 && list1[k1Max + 1].TravelTime - t0 <= 0.65f) k1Max++;
                        k1Max = Mathf.Max(k1Max, Mathf.Min(list1.Count - 1, idx1 + 8));

                        int k2Min = idx2;
                        while (k2Min > 0 && t0 - list2[k2Min - 1].TravelTime <= 0.65f) k2Min--;
                        k2Min = Mathf.Min(k2Min, Mathf.Max(0, idx2 - 8));

                        int k2Max = idx2;
                        while (k2Max < list2.Count - 1 && list2[k2Max + 1].TravelTime - t0 <= 0.65f) k2Max++;
                        k2Max = Mathf.Max(k2Max, Mathf.Min(list2.Count - 1, idx2 + 8));

                        for (int k1 = k1Min; k1 <= k1Max; k1++)
                        {
                            var c1 = list1[k1];
                            float t1 = c1.TravelTime;
                            if (Mathf.Abs(t0 - t1) >= bestDelta) continue;

                            for (int k2 = k2Min; k2 <= k2Max; k2++)
                            {
                                var c2 = list2[k2];
                                float t2 = c2.TravelTime;

                                float minT = Mathf.Min(t0, Mathf.Min(t1, t2));
                                float maxT = Mathf.Max(t0, Mathf.Max(t1, t2));
                                float delta = maxT - minT;

                                if (delta >= bestDelta) continue;

                                var cands = new EntranceCandidate[] { c0, c1, c2 };
                                if (tier == 2 || AreCandidatesSafelySeparated(cands, curMinSpawn, curMinDoor, curMinInterior))
                                {
                                    bestDelta = delta;
                                    bestTeamCandidates = cands;
                                }
                            }
                        }
                    }
                }
            }
            else if (teamCount == 4)
            {
                var list0 = candidatesBySide[0];
                var list1 = candidatesBySide[1];
                var list2 = candidatesBySide[2];
                var list3 = candidatesBySide[3];

                if (list0.Count > 0 && list1.Count > 0 && list2.Count > 0 && list3.Count > 0)
                {
                    for (int i0 = 0; i0 < list0.Count; i0++)
                    {
                        var c0 = list0[i0];
                        float t0 = c0.TravelTime;
                        int idx1 = FindClosestIndex(list1, t0);
                        int idx2 = FindClosestIndex(list2, t0);
                        int idx3 = FindClosestIndex(list3, t0);

                        int k1Min = idx1;
                        while (k1Min > 0 && t0 - list1[k1Min - 1].TravelTime <= 0.65f) k1Min--;
                        k1Min = Mathf.Min(k1Min, Mathf.Max(0, idx1 - 6));
                        int k1Max = idx1;
                        while (k1Max < list1.Count - 1 && list1[k1Max + 1].TravelTime - t0 <= 0.65f) k1Max++;
                        k1Max = Mathf.Max(k1Max, Mathf.Min(list1.Count - 1, idx1 + 6));

                        int k2Min = idx2;
                        while (k2Min > 0 && t0 - list2[k2Min - 1].TravelTime <= 0.65f) k2Min--;
                        k2Min = Mathf.Min(k2Min, Mathf.Max(0, idx2 - 6));
                        int k2Max = idx2;
                        while (k2Max < list2.Count - 1 && list2[k2Max + 1].TravelTime - t0 <= 0.65f) k2Max++;
                        k2Max = Mathf.Max(k2Max, Mathf.Min(list2.Count - 1, idx2 + 6));

                        int k3Min = idx3;
                        while (k3Min > 0 && t0 - list3[k3Min - 1].TravelTime <= 0.65f) k3Min--;
                        k3Min = Mathf.Min(k3Min, Mathf.Max(0, idx3 - 6));
                        int k3Max = idx3;
                        while (k3Max < list3.Count - 1 && list3[k3Max + 1].TravelTime - t0 <= 0.65f) k3Max++;
                        k3Max = Mathf.Max(k3Max, Mathf.Min(list3.Count - 1, idx3 + 6));

                        for (int k1 = k1Min; k1 <= k1Max; k1++)
                        {
                            var c1 = list1[k1];
                            float t1 = c1.TravelTime;
                            if (Mathf.Abs(t0 - t1) >= bestDelta) continue;

                            for (int k2 = k2Min; k2 <= k2Max; k2++)
                            {
                                var c2 = list2[k2];
                                float t2 = c2.TravelTime;
                                if (Mathf.Abs(t0 - t2) >= bestDelta) continue;

                                for (int k3 = k3Min; k3 <= k3Max; k3++)
                                {
                                    var c3 = list3[k3];
                                    float t3 = c3.TravelTime;

                                    float minT = Mathf.Min(t0, Mathf.Min(t1, Mathf.Min(t2, t3)));
                                    float maxT = Mathf.Max(t0, Mathf.Max(t1, Mathf.Max(t2, t3)));
                                    float delta = maxT - minT;

                                    if (delta >= bestDelta) continue;

                                    var cands = new EntranceCandidate[] { c0, c1, c2, c3 };
                                    if (tier == 2 || AreCandidatesSafelySeparated(cands, curMinSpawn, curMinDoor, curMinInterior))
                                    {
                                        bestDelta = delta;
                                        bestTeamCandidates = cands;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else // teamCount == 2
            {
                int[][] pairCombos = new int[][]
                {
                    new int[] { 0, 1 }, new int[] { 0, 2 }, new int[] { 0, 3 },
                    new int[] { 1, 2 }, new int[] { 1, 3 }, new int[] { 2, 3 }
                };

                for (int comboIdx = 0; comboIdx < pairCombos.Length; comboIdx++)
                {
                    int[] sides = pairCombos[comboIdx];
                    var list0 = candidatesBySide[sides[0]];
                    var list1 = candidatesBySide[sides[1]];
                    if (list0.Count == 0 || list1.Count == 0) continue;

                    for (int i0 = 0; i0 < list0.Count; i0++)
                    {
                        var c0 = list0[i0];
                        float t0 = c0.TravelTime;
                        int idx1 = FindClosestIndex(list1, t0);

                        int k1Min = idx1;
                        while (k1Min > 0 && t0 - list1[k1Min - 1].TravelTime <= 0.65f) k1Min--;
                        k1Min = Mathf.Min(k1Min, Mathf.Max(0, idx1 - 10));

                        int k1Max = idx1;
                        while (k1Max < list1.Count - 1 && list1[k1Max + 1].TravelTime - t0 <= 0.65f) k1Max++;
                        k1Max = Mathf.Max(k1Max, Mathf.Min(list1.Count - 1, idx1 + 10));

                        for (int k1 = k1Min; k1 <= k1Max; k1++)
                        {
                            var c1 = list1[k1];
                            float t1 = c1.TravelTime;
                            float delta = Mathf.Abs(t0 - t1);
                            if (delta >= bestDelta) continue;

                            var cands = new EntranceCandidate[] { c0, c1 };
                            if (tier == 2 || AreCandidatesSafelySeparated(cands, curMinSpawn, curMinDoor, curMinInterior))
                            {
                                bestDelta = delta;
                                bestTeamCandidates = cands;
                            }
                        }
                    }
                }
            }
        }

        // Fallback safety if no candidate combination was resolved
        if (bestTeamCandidates == null || bestTeamCandidates.Length == 0)
        {
            var fallbackList = new List<EntranceCandidate>();
            for (int s = 0; s < 4; s++)
            {
                if (candidatesBySide[s].Count > 0)
                    fallbackList.Add(candidatesBySide[s][0]);
            }
            bestTeamCandidates = fallbackList.GetRange(0, Mathf.Min(teamCount, fallbackList.Count)).ToArray();
        }

        // Calculate average time across chosen teams
        float avgTime = 0f;
        for (int i = 0; i < bestTeamCandidates.Length; i++) avgTime += bestTeamCandidates[i].TravelTime;
        avgTime /= Mathf.Max(1, bestTeamCandidates.Length);

        // Build the final 4 entrances (one per cardinal side: 0=North, 1=East, 2=South, 3=West)
        var final4Entrances = new EntranceCandidate[4];
        for (int i = 0; i < bestTeamCandidates.Length; i++)
        {
            final4Entrances[bestTeamCandidates[i].WallSide] = bestTeamCandidates[i];
        }

        // For any unassigned side, pick candidate closest to avgTime so all 4 perimeter walls have entrances
        for (int s = 0; s < 4; s++)
        {
            if (final4Entrances[s].TotalDist <= 0f)
            {
                var sideList = candidatesBySide[s];
                if (sideList.Count > 0)
                {
                    int bestIdx = FindClosestIndex(sideList, avgTime);
                    final4Entrances[s] = sideList[bestIdx];
                }
                else
                {
                    // Defensive fallback: synthesize safe candidate along perimeter wall
                    int edgeCoord = (s == 0 || s == 2) ? (_width - hw) / 2 : (_height - hw) / 2;
                    int hitCoord = s switch
                    {
                        0 => _height - 1 - Mathf.Max(4, hw + 2),
                        1 => _width - 1 - Mathf.Max(4, hw + 2),
                        2 => 1 + Mathf.Max(4, hw + 2),
                        _ => 1 + Mathf.Max(4, hw + 2)
                    };
                    Vector2 doorPos = s switch
                    {
                        0 => GridToWorld(edgeCoord + halfHwOffset, _height - 1),
                        1 => GridToWorld(_width - 1, edgeCoord + halfHwOffset),
                        2 => GridToWorld(edgeCoord + halfHwOffset, 0),
                        _ => GridToWorld(0, edgeCoord + halfHwOffset)
                    };
                    Vector2 spawnPos = s switch
                    {
                        0 => GridToWorld(edgeCoord + halfHwOffset, _height - 1 + courtyardDist),
                        1 => GridToWorld(_width - 1 + courtyardDist, edgeCoord + halfHwOffset),
                        2 => GridToWorld(edgeCoord + halfHwOffset, -courtyardDist),
                        _ => GridToWorld(-courtyardDist, edgeCoord + halfHwOffset)
                    };
                    Vector2 interiorPos = s switch
                    {
                        0 => GridToWorld(edgeCoord + halfHwOffset, hitCoord),
                        1 => GridToWorld(hitCoord, edgeCoord + halfHwOffset),
                        2 => GridToWorld(edgeCoord + halfHwOffset, hitCoord),
                        _ => GridToWorld(hitCoord, edgeCoord + halfHwOffset)
                    };
                    final4Entrances[s] = new EntranceCandidate
                    {
                        WallSide = s,
                        EdgeCoord = edgeCoord,
                        HitCoord = hitCoord,
                        InwardDist = Mathf.Max(4, hw + 2),
                        InteriorDist = 100f,
                        TotalDist = courtyardDist + Mathf.Max(4, hw + 2) + 100f,
                        TravelTime = (courtyardDist + Mathf.Max(4, hw + 2) + 100f) / playerSprintSpeed,
                        WorldDoorPos = doorPos,
                        WorldSpawnPos = spawnPos,
                        WorldInteriorPos = interiorPos
                    };
                }
            }
        }

        // Carve all 4 entrances into Floor 0 grid
        for (int s = 0; s < 4; s++)
        {
            CarveEntranceIntoFloor0(final4Entrances[s], hw, doorSpan, doorOffset);
            _entrances[s] = new EntranceData
            {
                WallSide = s,
                GridCell = GetGridCellForEntrance(final4Entrances[s]),
                WorldPosition = final4Entrances[s].WorldDoorPos,
                CourtyardSpawnPosition = final4Entrances[s].WorldSpawnPos
            };
        }

        // Assign teams round-robin / shuffled among the chosen equidistant candidates
        int[] teamOrder = new int[bestTeamCandidates.Length];
        for (int i = 0; i < teamOrder.Length; i++) teamOrder[i] = i;
        for (int i = teamOrder.Length - 1; i > 0; i--)
        {
            int sw = rng.Next(i + 1);
            (teamOrder[i], teamOrder[sw]) = (teamOrder[sw], teamOrder[i]);
        }

        _teamASpawnPos = bestTeamCandidates[teamOrder[0]].WorldSpawnPos;
        _teamBSpawnPos = bestTeamCandidates[teamOrder[1]].WorldSpawnPos;
        if (bestTeamCandidates.Length >= 3)
        {
            _teamCSpawnPos = bestTeamCandidates[teamOrder[2]].WorldSpawnPos;
        }

        // Record public equidistant and cardinal verification metrics
        TeamSprintTravelTimes = new float[bestTeamCandidates.Length];
        TeamCardinalDirections = new string[bestTeamCandidates.Length];
        var usedSides = new HashSet<int>();

        for (int i = 0; i < bestTeamCandidates.Length; i++)
        {
            var cand = bestTeamCandidates[teamOrder[i]];
            TeamSprintTravelTimes[i] = cand.TravelTime;
            TeamCardinalDirections[i] = cand.WallSide switch
            {
                0 => "NORTH",
                1 => "EAST",
                2 => "SOUTH",
                _ => "WEST"
            };
            usedSides.Add(cand.WallSide);
        }

        float minObservedTime = float.MaxValue;
        float maxObservedTime = float.MinValue;
        for (int i = 0; i < TeamSprintTravelTimes.Length; i++)
        {
            if (TeamSprintTravelTimes[i] < minObservedTime) minObservedTime = TeamSprintTravelTimes[i];
            if (TeamSprintTravelTimes[i] > maxObservedTime) maxObservedTime = TeamSprintTravelTimes[i];
        }
        MaxTeamTravelTimeDelta = maxObservedTime - minObservedTime;
        AllTeamsDistinctCardinalsVerified = (usedSides.Count == bestTeamCandidates.Length);
        AllTeamsEquidistantVerified = (MaxTeamTravelTimeDelta <= 0.6f);

        float minObservedSpawnDist = float.MaxValue;
        float minObservedDoorDist = float.MaxValue;
        float minObservedInteriorDist = float.MaxValue;

        for (int i = 0; i < bestTeamCandidates.Length; i++)
        {
            for (int j = i + 1; j < bestTeamCandidates.Length; j++)
            {
                float sd = Vector2.Distance(bestTeamCandidates[i].WorldSpawnPos, bestTeamCandidates[j].WorldSpawnPos);
                float dd = Vector2.Distance(bestTeamCandidates[i].WorldDoorPos, bestTeamCandidates[j].WorldDoorPos);
                float id = Vector2.Distance(bestTeamCandidates[i].WorldInteriorPos, bestTeamCandidates[j].WorldInteriorPos);
                if (sd < minObservedSpawnDist) minObservedSpawnDist = sd;
                if (dd < minObservedDoorDist) minObservedDoorDist = dd;
                if (id < minObservedInteriorDist) minObservedInteriorDist = id;
            }
        }

        MinObservedTeamSpawnDistanceWorld = minObservedSpawnDist;
        MinObservedTeamDoorDistanceWorld = minObservedDoorDist;
        MinObservedTeamInteriorDistanceWorld = minObservedInteriorDist;
        AllTeamSpawnsSeparatedVerified = (bestTeamCandidates.Length <= 1) ||
            (minObservedSpawnDist >= minSpawnSep && minObservedDoorDist >= minDoorSep && minObservedInteriorDist >= minInteriorSep);
    }

    private float[] ComputeMultiFloorDijkstraFromObjective()
    {
        int activeFloorCount = _activeFloors.Count;
        int totalNodes = activeFloorCount * _height * _width;
        float[] dist = new float[totalNodes];
        for (int i = 0; i < totalNodes; i++) dist[i] = float.MaxValue;

        int objFloorIndex = _activeFloors.IndexOf(_objectiveFloorLevel);
        if (objFloorIndex < 0 || !_floorStates.TryGetValue(_objectiveFloorLevel, out var objFloorState))
        {
            return dist;
        }

        var sources = new List<Vector2Int>();
        int xMin = Mathf.Max(0, _objectiveRoomBounds.xMin - 1);
        int xMax = Mathf.Min(_width - 1, _objectiveRoomBounds.xMax);
        int yMin = Mathf.Max(0, _objectiveRoomBounds.yMin - 1);
        int yMax = Mathf.Min(_height - 1, _objectiveRoomBounds.yMax);

        for (int x = xMin; x <= xMax; x++)
        {
            for (int y = yMin; y <= yMax; y++)
            {
                if (objFloorState.Grid[x, y] == CellType.Doorway)
                {
                    sources.Add(new Vector2Int(x, y));
                }
            }
        }

        if (sources.Count == 0)
        {
            for (int x = _objectiveRoomBounds.xMin; x < _objectiveRoomBounds.xMax; x++)
            {
                sources.Add(new Vector2Int(x, _objectiveRoomBounds.yMin));
                sources.Add(new Vector2Int(x, Mathf.Max(0, _objectiveRoomBounds.yMax - 1)));
            }
            for (int y = _objectiveRoomBounds.yMin; y < _objectiveRoomBounds.yMax; y++)
            {
                sources.Add(new Vector2Int(_objectiveRoomBounds.xMin, y));
                sources.Add(new Vector2Int(Mathf.Max(0, _objectiveRoomBounds.xMax - 1), y));
            }
        }

        var heap = new FastMinHeap(totalNodes);
        for (int i = 0; i < sources.Count; i++)
        {
            int u = (objFloorIndex * _height + sources[i].y) * _width + sources[i].x;
            dist[u] = 0f;
            heap.Push(0f, u);
        }

        int[] cdx = { 1, -1, 0, 0 };
        int[] cdy = { 0, 0, 1, -1 };
        int[] ddx = { 1, 1, -1, -1 };
        int[] ddy = { 1, -1, 1, -1 };
        const float sqrt2 = 1.41421356f;

        while (heap.Count > 0)
        {
            var item = heap.Pop();
            float cost = item.Cost;
            int u = item.Index;
            if (cost > dist[u]) continue;

            int x = u % _width;
            int rem = u / _width;
            int y = rem % _height;
            int fi = rem / _height;
            int floorLevel = _activeFloors[fi];
            var floorGrid = _floorStates[floorLevel].Grid;

            // 1. Cardinal neighbors
            for (int d = 0; d < 4; d++)
            {
                int nx = x + cdx[d];
                int ny = y + cdy[d];
                if (nx >= 1 && nx < _width - 1 && ny >= 1 && ny < _height - 1)
                {
                    if (IsWalkableTile(floorGrid[nx, ny]))
                    {
                        float nCost = cost + 1.0f;
                        int v = (fi * _height + ny) * _width + nx;
                        if (nCost < dist[v])
                        {
                            dist[v] = nCost;
                            heap.Push(nCost, v);
                        }
                    }
                }
            }

            // 2. Diagonal neighbors
            for (int d = 0; d < 4; d++)
            {
                int nx = x + ddx[d];
                int ny = y + ddy[d];
                if (nx >= 1 && nx < _width - 1 && ny >= 1 && ny < _height - 1)
                {
                    if (IsWalkableTile(floorGrid[nx, ny]))
                    {
                        if (IsWalkableTile(floorGrid[x, ny]) || IsWalkableTile(floorGrid[nx, y]))
                        {
                            float nCost = cost + sqrt2;
                            int v = (fi * _height + ny) * _width + nx;
                            if (nCost < dist[v])
                            {
                                dist[v] = nCost;
                                heap.Push(nCost, v);
                            }
                        }
                    }
                }
            }

            // 3. Stairwells
            for (int s = 0; s < _plannedStaircases.Count; s++)
            {
                var plan = _plannedStaircases[s];
                if (floorLevel == plan.LowerFloor)
                {
                    if (x >= plan.BayX && x < plan.BayX + 4 && y >= plan.BayMinY && y <= plan.BayMinY + 2)
                    {
                        int upperFi = _activeFloors.IndexOf(plan.UpperFloor);
                        if (upperFi >= 0)
                        {
                            int targetY = plan.BayMinY + 7;
                            float nCost = cost + 7.0f;
                            int v = (upperFi * _height + targetY) * _width + x;
                            if (nCost < dist[v])
                            {
                                dist[v] = nCost;
                                heap.Push(nCost, v);
                            }
                        }
                    }
                }
                else if (floorLevel == plan.UpperFloor)
                {
                    if (x >= plan.BayX && x < plan.BayX + 4 && y >= plan.BayMinY + 6 && y <= plan.BayMinY + 9)
                    {
                        int lowerFi = _activeFloors.IndexOf(plan.LowerFloor);
                        if (lowerFi >= 0)
                        {
                            int targetY = plan.BayMinY + 1;
                            float nCost = cost + 7.0f;
                            int v = (lowerFi * _height + targetY) * _width + x;
                            if (nCost < dist[v])
                            {
                                dist[v] = nCost;
                                heap.Push(nCost, v);
                            }
                        }
                    }
                }
            }
        }

        return dist;
    }

    private static bool IsWalkableTile(CellType t)
    {
        return t == CellType.HallwayFloor ||
               t == CellType.RoomFloor ||
               t == CellType.ObjectiveRoomFloor ||
               t == CellType.Doorway;
    }

    private bool IntersectsAnyStairwellBay(int minX, int maxX, int minY, int maxY)
    {
        for (int i = 0; i < _plannedStaircases.Count; i++)
        {
            var plan = _plannedStaircases[i];
            if (maxX >= plan.BayX - 1 && minX <= plan.BayX + 4 &&
                maxY >= plan.BayMinY - 1 && minY <= plan.BayMinY + 10)
            {
                return true;
            }
        }
        return false;
    }

    private static int FindClosestIndex(List<EntranceCandidate> list, float targetTime)
    {
        if (list.Count == 0) return 0;
        int low = 0, high = list.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (list[mid].TravelTime < targetTime) low = mid + 1;
            else high = mid - 1;
        }
        int bestIdx = Mathf.Clamp(low, 0, list.Count - 1);
        if (bestIdx > 0 && Mathf.Abs(list[bestIdx - 1].TravelTime - targetTime) < Mathf.Abs(list[bestIdx].TravelTime - targetTime))
        {
            bestIdx = bestIdx - 1;
        }
        return bestIdx;
    }

    private void CarveEntranceIntoFloor0(EntranceCandidate c, int hw, int doorSpan, int doorOffset)
    {
        int vestibuleDepth = Mathf.Max(4, hw + 2);
        if (c.WallSide == 0) // North
        {
            CarveHorizontalDoorway(c.EdgeCoord + doorOffset, _height - 1, doorSpan);
            ReserveEntranceVestibule(c.EdgeCoord, _height - 1 - vestibuleDepth, hw, vestibuleDepth, 2);
            for (int y = _height - 1 - vestibuleDepth; y >= c.HitCoord; y--)
            {
                for (int w = 0; w < hw; w++)
                {
                    SetHallwayIfWall(c.EdgeCoord + w, y);
                }
            }
        }
        else if (c.WallSide == 1) // East
        {
            CarveVerticalDoorway(_width - 1, c.EdgeCoord + doorOffset, doorSpan);
            ReserveEntranceVestibule(_width - 1 - vestibuleDepth, c.EdgeCoord, vestibuleDepth, hw, 2);
            for (int x = _width - 1 - vestibuleDepth; x >= c.HitCoord; x--)
            {
                for (int w = 0; w < hw; w++)
                {
                    SetHallwayIfWall(x, c.EdgeCoord + w);
                }
            }
        }
        else if (c.WallSide == 2) // South
        {
            CarveHorizontalDoorway(c.EdgeCoord + doorOffset, 0, doorSpan);
            ReserveEntranceVestibule(c.EdgeCoord, 1, hw, vestibuleDepth, 2);
            for (int y = 1 + vestibuleDepth; y <= c.HitCoord; y++)
            {
                for (int w = 0; w < hw; w++)
                {
                    SetHallwayIfWall(c.EdgeCoord + w, y);
                }
            }
        }
        else if (c.WallSide == 3) // West
        {
            CarveVerticalDoorway(0, c.EdgeCoord + doorOffset, doorSpan);
            ReserveEntranceVestibule(1, c.EdgeCoord, vestibuleDepth, hw, 2);
            for (int x = 1 + vestibuleDepth; x <= c.HitCoord; x++)
            {
                for (int w = 0; w < hw; w++)
                {
                    SetHallwayIfWall(x, c.EdgeCoord + w);
                }
            }
        }
    }

    private Vector2Int GetGridCellForEntrance(EntranceCandidate c)
    {
        return c.WallSide switch
        {
            0 => new Vector2Int(c.EdgeCoord, _height - 1),
            1 => new Vector2Int(_width - 1, c.EdgeCoord),
            2 => new Vector2Int(c.EdgeCoord, 0),
            _ => new Vector2Int(0, c.EdgeCoord)
        };
    }

    private EntranceData GetEntranceForSpawnPos(Vector2 spawnPos)
    {
        for (int i = 0; i < 4; i++)
        {
            if ((_entrances[i].CourtyardSpawnPosition - spawnPos).sqrMagnitude < 0.2f)
            {
                return _entrances[i];
            }
        }
        return _entrances[0];
    }

    private void CarveNonGroundFloorArterialHallways(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = GetConfiguredHallwayWidth();

        int stairMargin = margin + hw + 2;
        int westThroatX = stairMargin + 4;
        int eastThroatX = _width - stairMargin - 5;
        int eastHallX = eastThroatX - hw + 1;
        int spineX = (_width - hw) / 2;

        int crossY = (_height - hw) / 2;
        int stairYMargin = Mathf.Max(8, (_config != null ? _config.EntranceCornerMargin : 14));
        int flankYMin = Mathf.Clamp(Mathf.Min(margin, stairYMargin - 2), 2, _height - 2);
        int flankYMax = Mathf.Clamp(Mathf.Max(_height - margin, _height - stairYMargin + 2), 2, _height - 2);
        int southCrossY = flankYMin;
        int northCrossY = flankYMax - hw;

        bool hasSpine = (eastHallX - (westThroatX + hw) >= 24);

        // 1. Carve North-South arterial corridors across the building footprint (y = 2 .. _height - 2)
        for (int y = 2; y < _height - 2; y++)
        {
            for (int w = 0; w < hw; w++)
            {
                _grid[westThroatX + w, y] = CellType.HallwayFloor;
                if (hasSpine) _grid[spineX + w, y] = CellType.HallwayFloor;
                _grid[eastHallX + w, y]   = CellType.HallwayFloor;
            }
        }

        // 2. Carve 3 East-West arterial cross-corridors across the building footprint (x = 2 .. _width - 2)
        for (int x = 2; x < _width - 2; x++)
        {
            for (int w = 0; w < hw; w++)
            {
                _grid[x, southCrossY + w] = CellType.HallwayFloor;
                _grid[x, crossY + w]      = CellType.HallwayFloor;
                _grid[x, northCrossY + w] = CellType.HallwayFloor;
            }
        }
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
    private const int BspMinLeafDim = 11;

    /// <summary>
    /// Maximum BSP tree recursion depth. Controls the granularity of spatial partitioning.
    /// Higher values produce more, smaller rooms; lower values produce fewer, larger rooms.
    /// </summary>
    private const int BspMaxDepth = 4;

    /// <summary>
    /// Computes the rectangular interior sectors bounded by the arterial corridor grid and building outer margins.
    /// This guarantees that rooms carved within sectors never cross or collide with arterial corridors.
    /// </summary>
    private List<RectInt> ComputeFloorSectors()
    {
        int hw = GetConfiguredHallwayWidth();
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int stairMargin = margin + hw + 2;
        int westThroatX = stairMargin + 4;
        int eastThroatX = _width - stairMargin - 5;
        int eastHallX = eastThroatX - hw + 1;
        int crossY = (_height - hw) / 2;
        int stairYMargin = Mathf.Max(8, (_config != null ? _config.EntranceCornerMargin : 14));
        int flankYMin = Mathf.Clamp(Mathf.Min(margin, stairYMargin - 2), 2, _height - 2);
        int flankYMax = Mathf.Clamp(Mathf.Max(_height - margin, _height - stairYMargin + 2), 2, _height - 2);
        int southCrossY = flankYMin;
        int northCrossY = flankYMax - hw;
        int spineX = (_width - hw) / 2;

        bool hasSpine = (eastHallX - (westThroatX + hw) >= 24);
        bool hasCross = (northCrossY - (southCrossY + hw) >= 24);

        int[] xStarts = hasSpine
            ? new int[] { 2, westThroatX + hw, spineX + hw, eastHallX + hw }
            : new int[] { 2, westThroatX + hw, eastHallX + hw };
        int[] xEnds = hasSpine
            ? new int[] { westThroatX, spineX, eastHallX, _width - 2 }
            : new int[] { westThroatX, eastHallX, _width - 2 };

        int[] yStarts = hasCross
            ? new int[] { 2, southCrossY + hw, crossY + hw, northCrossY + hw }
            : new int[] { 2, southCrossY + hw, northCrossY + hw };
        int[] yEnds = hasCross
            ? new int[] { southCrossY, crossY, northCrossY, _height - 2 }
            : new int[] { southCrossY, northCrossY, _height - 2 };

        var sectors = new List<RectInt>(16);
        for (int xi = 0; xi < xStarts.Length; xi++)
        {
            for (int yi = 0; yi < yStarts.Length; yi++)
            {
                int w = xEnds[xi] - xStarts[xi];
                int h = yEnds[yi] - yStarts[yi];
                if (w >= 9 && h >= 9)
                {
                    sectors.Add(new RectInt(xStarts[xi], yStarts[yi], w, h));
                }
            }
        }
        return sectors;
    }

    // ──────────────── BSP Room Placement ─────────────────────────────────

    /// <summary>
    /// Procedurally places rooms across the ENTIRE building interior using sector-based BSP.
    /// Bounded by the arterial corridors and building margins, each sector is partitioned into
    /// spacious rooms (standard, workshop, auditorium) with wall-to-wall footprints and through-doorways.
    /// On the objective floor the largest qualifying leaf is elected as the Objective Room
    /// and carved as <see cref="CellType.ObjectiveRoomFloor"/> with 4 cardinal doorways.
    /// At most 1 small utility closet is permitted across an entire floor.
    /// </summary>
    private void CarveInteriorRoomsViaBsp(System.Random rng, bool isObjectiveFloor)
    {
        int targetRooms = _config != null ? _config.TargetRoomCount : 15;

        // ── Collect BSP leaves across the natural sectors bounded by arterial corridors ──
        var allLeaves = new List<BspNode>(32);
        var sectors = ComputeFloorSectors();
        for (int s = 0; s < sectors.Count; s++)
        {
            // Allow larger sectors (e.g. 28x25) to subdivide into balanced sub-partitions
            BspNode secTree = BuildBspTree(sectors[s], rng, 0, 2);
            CollectBspLeaves(secTree, allLeaves);
        }

        // Sort by area descending — largest leaves get first pick
        allLeaves.Sort((a, b) =>
        {
            int areaA = a.Bounds.width * a.Bounds.height;
            int areaB = b.Bounds.width * b.Bounds.height;
            return areaB.CompareTo(areaA);
        });

        // ── Elect the Objective Room from qualifying leaves, prioritizing proximity to factory center ──
        bool objectivePlaced = false;
        if (isObjectiveFloor)
        {
            int targetObjSize = _config != null ? _config.ObjectiveRoomSize : 24;
            int objMinDim = _config != null ? Mathf.Max(16, targetObjSize - 4) : 16;
            Vector2 factoryCenter = new Vector2(_width * 0.5f, _height * 0.5f);

            var objCandidates = new List<BspNode>(allLeaves);
            objCandidates.Sort((a, b) =>
            {
                float distA = Vector2.Distance(a.Bounds.center, factoryCenter);
                float distB = Vector2.Distance(b.Bounds.center, factoryCenter);
                return distA.CompareTo(distB);
            });

            for (int i = 0; i < objCandidates.Count; i++)
            {
                BspNode leaf = objCandidates[i];
                int availW = leaf.Bounds.width - 2;
                int availH = leaf.Bounds.height - 2;
                if (availW < 10 || availH < 10) continue;

                // Try to place the objective room in the interior of this leaf
                int rw = Mathf.Clamp(targetObjSize + rng.Next(-2, 3), 10, availW);
                int rh = Mathf.Clamp(targetObjSize + rng.Next(-2, 3), 10, availH);

                int maxOffX = Mathf.Max(0, availW - rw);
                int maxOffY = Mathf.Max(0, availH - rh);
                int offX = maxOffX > 0 ? rng.Next(0, maxOffX + 1) : 0;
                int offY = maxOffY > 0 ? rng.Next(0, maxOffY + 1) : 0;
                int rx = leaf.Bounds.xMin + 1 + offX;
                int ry = leaf.Bounds.yMin + 1 + offY;

                if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

                    // Carve objective room floor
                    for (int x = rx; x < rx + rw; x++)
                        for (int y = ry; y < ry + rh; y++)
                            _grid[x, y] = CellType.ObjectiveRoomFloor;

                    var objBounds = new RectInt(rx, ry, rw, rh);
                    _objectiveRoomBounds = objBounds;
                    _currentFloor.CentralRoomBounds = objBounds;

                    // Connect doorways outward toward the surrounding arterial corridors
                    int connectedDoorways = 0;
                    for (int side = 0; side < 4; side++)
                    {
                        if (TryConnectRoomWallOutward(objBounds, side))
                            connectedDoorways++;
                    }

                    // If outward straight rays didn't hit an arterial hallway, guarantee connection via BFS branch corridor
                    int doorSpan = GetDoorwaySpan();
                    int midX = rx + (rw - doorSpan) / 2;
                    int midY = ry + (rh - doorSpan) / 2;

                    if (connectedDoorways == 0)
                    {
                        connectedDoorways = ConnectObjectiveRoomToHallwayNetwork(objBounds, rng);
                    }
                    else
                    {
                        // Ensure all 4 cardinal doorways exist
                        CarveHorizontalDoorway(midX, ry + rh, doorSpan);
                        CarveHorizontalDoorway(midX, ry - 1, doorSpan);
                        CarveVerticalDoorway(rx + rw, midY, doorSpan);
                        CarveVerticalDoorway(rx - 1, midY, doorSpan);
                        connectedDoorways = 4;
                    }

                    GetArchetypeDoorAndWindowTargets(RoomArchetype.Auditorium, rng, out int td, out int tw);
                    _rooms.Add(new RoomData
                    {
                        FloorLevel = _currentFloor.FloorLevel,
                        Bounds     = objBounds,
                        Archetype  = RoomArchetype.ObjectiveHub,
                        IsObjectiveRoom = true,
                        TargetDoorways  = Mathf.Max(connectedDoorways, td),
                        ActualDoorways  = connectedDoorways,
                        TargetWindows   = tw
                    });

                    // Remove leaf from pool so it isn't double-used
                    allLeaves.Remove(leaf);
                    objectivePlaced = true;
                    break;
                }
            }

        // ── Place ordinary rooms in remaining leaves ──
        int targetAuditoriums = 2;
        int targetWorkshops   = 3;
        int targetFromBsp = Mathf.Max(0, targetRooms - _rooms.Count);
        int placed            = 0;
        int placedAuditoriums = 0;
        int placedWorkshops   = 0;
        int floorClosetCount  = 0;
        for (int i = 0; i < allLeaves.Count && placed < targetFromBsp; i++)
        {
            if (TryPlaceRoomInBspLeaf(allLeaves[i], rng, targetAuditoriums, ref placedAuditoriums, targetWorkshops, ref placedWorkshops, ref floorClosetCount))
            {
                placed++;
            }
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
    /// Guarantees that the Objective Room is connected to the active floor's hallway network
    /// by carving branch corridors through uncarved space from its doorways to the nearest arterial hallway.
    /// </summary>
    private int ConnectObjectiveRoomToHallwayNetwork(RectInt objBounds, System.Random rng)
    {
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = GetDoorwaySpan();
        int rx = objBounds.xMin;
        int ry = objBounds.yMin;
        int rw = objBounds.width;
        int rh = objBounds.height;

        int midX = rx + (rw - doorSpan) / 2;
        int midY = ry + (rh - doorSpan) / 2;

        int connected = 0;

        // Try to connect inward-facing sides first toward the center of the factory
        int centerX = _width / 2;
        int centerY = _height / 2;
        int objCenterX = rx + rw / 2;
        int objCenterY = ry + rh / 2;

        // Order sides by priority: prefer sides facing toward the building center
        int[] sides = new int[]
        {
            objCenterY > centerY ? 2 : 0, // South or North (facing center)
            objCenterX > centerX ? 3 : 1, // West or East (facing center)
            objCenterY > centerY ? 0 : 2, // Opposite side
            objCenterX > centerX ? 1 : 3  // Opposite side
        };

        for (int i = 0; i < 4; i++)
        {
            int side = sides[i];
            if (TryCarveCorridorFromObjectiveSideToHallway(objBounds, side, hw, doorSpan))
            {
                connected++;
                if (connected >= 2) break; // Ensure at least 2 robust connections
            }
        }

        // Always carve cardinal doorways on all 4 sides for symmetry/access
        CarveHorizontalDoorway(midX, ry + rh, doorSpan);
        CarveHorizontalDoorway(midX, ry - 1, doorSpan);
        CarveVerticalDoorway(rx + rw, midY, doorSpan);
        CarveVerticalDoorway(rx - 1, midY, doorSpan);

        return Mathf.Max(connected, 4);
    }

    private bool TryCarveCorridorFromObjectiveSideToHallway(RectInt objBounds, int side, int hw, int doorSpan)
    {
        int rx = objBounds.xMin;
        int ry = objBounds.yMin;
        int rw = objBounds.width;
        int rh = objBounds.height;

        int doorX, doorY;
        Vector2Int start;

        if (side == 0) // North
        {
            doorX = rx + (rw - doorSpan) / 2;
            doorY = ry + rh;
            if (doorY + hw >= _height - 1) return false;
            start = new Vector2Int(doorX + doorSpan / 2, doorY + 1);
        }
        else if (side == 1) // East
        {
            doorX = rx + rw;
            doorY = ry + (rh - doorSpan) / 2;
            if (doorX + hw >= _width - 1) return false;
            start = new Vector2Int(doorX + 1, doorY + doorSpan / 2);
        }
        else if (side == 2) // South
        {
            doorX = rx + (rw - doorSpan) / 2;
            doorY = ry - 1;
            if (doorY - hw <= 0) return false;
            start = new Vector2Int(doorX + doorSpan / 2, doorY - 1);
        }
        else // West (side == 3)
        {
            doorX = rx - 1;
            doorY = ry + (rh - doorSpan) / 2;
            if (doorX - hw <= 0) return false;
            start = new Vector2Int(doorX - 1, doorY + doorSpan / 2);
        }

        if (start.x < 2 || start.x >= _width - 2 || start.y < 2 || start.y >= _height - 2)
            return false;

        var queue = new Queue<Vector2Int>();
        var visited = new bool[_width, _height];
        var parent = new Vector2Int[_width, _height];

        queue.Enqueue(start);
        visited[start.x, start.y] = true;
        parent[start.x, start.y] = new Vector2Int(-1, -1);

        Vector2Int target = new Vector2Int(-1, -1);
        int[] cdx = { 0, 0, 1, -1 };
        int[] cdy = { 1, -1, 0, 0 };

        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            if (_grid[curr.x, curr.y] == CellType.HallwayFloor)
            {
                target = curr;
                break;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = curr.x + cdx[d];
                int ny = curr.y + cdy[d];

                if (nx < 2 || nx >= _width - 2 || ny < 2 || ny >= _height - 2) continue;
                if (visited[nx, ny]) continue;
                // Do not enter the objective room interior
                if (nx >= rx && nx < rx + rw && ny >= ry && ny < ry + rh) continue;
                // Do not carve through stairwell bays
                if (IntersectsAnyStairwellBay(nx, nx, ny, ny)) continue;

                visited[nx, ny] = true;
                parent[nx, ny] = curr;
                queue.Enqueue(new Vector2Int(nx, ny));
            }
        }

        if (target.x < 0) return false;

        // Carve hallway along BFS path from target back to start
        var p = target;
        while (p.x != -1 && p.y != -1)
        {
            int hallStartX = Mathf.Clamp(p.x - (hw - 1) / 2, 2, _width - hw - 2);
            int hallStartY = Mathf.Clamp(p.y - (hw - 1) / 2, 2, _height - hw - 2);
            for (int ox = 0; ox < hw; ox++)
            {
                for (int oy = 0; oy < hw; oy++)
                {
                    int cx = hallStartX + ox;
                    int cy = hallStartY + oy;
                    if (!IntersectsAnyStairwellBay(cx, cx, cy, cy) &&
                        !(cx >= rx && cx < rx + rw && cy >= ry && cy < ry + rh))
                    {
                        SetHallwayIfWall(cx, cy);
                    }
                }
            }
            p = parent[p.x, p.y];
        }

        // Carve doorway on this side
        if (side == 0 || side == 2)
            CarveHorizontalDoorway(doorX, doorY, doorSpan);
        else
            CarveVerticalDoorway(doorX, doorY, doorSpan);

        return true;
    }

    /// <summary>
    /// Classifies the best-fitting room archetype for a BSP leaf based on its available
    /// interior dimensions (after subtracting 1-tile wall padding on each side).
    /// </summary>
    private static RoomArchetype ClassifyBspLeafArchetype(int interiorW, int interiorH)
    {
        int minDim = Mathf.Min(interiorW, interiorH);
        int maxDim = Mathf.Max(interiorW, interiorH);

        if (minDim >= 18 && maxDim >= 24) return RoomArchetype.Auditorium;
        if (minDim >= 13 && maxDim >= 17) return RoomArchetype.LargeWorkshop;
        if (minDim >= 9 && maxDim >= 10)  return RoomArchetype.StandardRoom;
        if (minDim >= 5)                  return RoomArchetype.Closet;

        return RoomArchetype.Closet;
    }

    /// <summary>
    /// Attempts to place a room inside a BSP leaf partition. Determines the room archetype from
    /// the leaf dimensions, validates pure wall space, carves the room floor, and connects it
    /// to the hallway network via doorways or branch corridors.
    /// Falls back through smaller archetypes if the primary archetype cannot fit.
    /// </summary>
    private bool TryPlaceRoomInBspLeaf(
        BspNode leaf,
        System.Random rng,
        int targetAuditoriums,
        ref int placedAuditoriums,
        int targetWorkshops,
        ref int placedWorkshops,
        ref int floorClosetCount)
    {
        RectInt lb = leaf.Bounds;

        // Available interior after reserving 1-tile wall padding on each side
        int availW = lb.width - 2;
        int availH = lb.height - 2;
        if (availW < 5 || availH < 5) return false;

        // Determine archetype tiers to try (best fit → fallbacks)
        RoomArchetype primary = ClassifyBspLeafArchetype(availW, availH);
        if (primary == RoomArchetype.Auditorium && placedAuditoriums >= targetAuditoriums)
        {
            primary = RoomArchetype.LargeWorkshop;
        }
        if (primary == RoomArchetype.LargeWorkshop && placedWorkshops >= targetWorkshops)
        {
            primary = RoomArchetype.StandardRoom;
        }

        RoomArchetype[] tiers;
        if (floorClosetCount >= 1)
        {
            // At most 1 closet per floor: do NOT allow Closet tier if 1 already placed!
            tiers = primary switch
            {
                RoomArchetype.Auditorium =>
                    new[] { RoomArchetype.Auditorium, RoomArchetype.LargeWorkshop, RoomArchetype.StandardRoom },
                RoomArchetype.LargeWorkshop =>
                    new[] { RoomArchetype.LargeWorkshop, RoomArchetype.StandardRoom },
                _ =>
                    new[] { RoomArchetype.StandardRoom }
            };
        }
        else
        {
            tiers = primary switch
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
        }

        for (int tier = 0; tier < tiers.Length; tier++)
        {
            RoomArchetype tryArch = tiers[tier];
            if (tryArch == RoomArchetype.Auditorium && placedAuditoriums >= targetAuditoriums) continue;
            if (tryArch == RoomArchetype.LargeWorkshop && placedWorkshops >= targetWorkshops) continue;
            if (tryArch == RoomArchetype.Closet && floorClosetCount >= 1) continue;

            int attemptsPerTier = tryArch == RoomArchetype.Auditorium ? 16 : (tryArch == RoomArchetype.Closet ? 4 : 10);

            for (int attempt = 0; attempt < attemptsPerTier; attempt++)
            {
                int rw, rh;
                if (attempt == 0 && tryArch != RoomArchetype.Closet)
                {
                    // Attempt 0: Fill the leaf wall-to-wall for maximum room size and exploration
                    rw = availW;
                    rh = availH;
                }
                else
                {
                    // Generate room dimensions within archetype range, clamped to leaf interior
                    bool useSmallerFallback = attempt >= 4;
                    GetDimensionsForArchetype(tryArch, rng, useSmallerFallback, out rw, out rh);
                    rw = Mathf.Min(rw, availW);
                    rh = Mathf.Min(rh, availH);
                }
                if (rw < 5 || rh < 5) continue;

                // Position the room within the leaf
                int maxOffX = Mathf.Max(0, availW - rw);
                int maxOffY = Mathf.Max(0, availH - rh);
                int offX, offY;
                if (attempt == 0)
                {
                    offX = (lb.center.x < _width / 2) ? maxOffX : 0;
                    offY = (lb.center.y < _height / 2) ? maxOffY : 0;
                }
                else
                {
                    offX = maxOffX > 0 ? rng.Next(0, maxOffX + 1) : 0;
                    offY = maxOffY > 0 ? rng.Next(0, maxOffY + 1) : 0;
                }
                int rx = lb.xMin + 1 + offX;
                int ry = lb.yMin + 1 + offY;

                // Validate that room footprint (plus 1-tile wall border) is pure uncarved wall
                if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

                var candidate = new RectInt(rx, ry, rw, rh);

                // Connect room to hallway network via doorway or branch corridor
                int doorSpan = tryArch == RoomArchetype.Closet ? 3 : GetDoorwaySpan();
                Vector2Int doorCell;
                bool horizontalDoor;
                int adjDist = 1;
                bool connected =
                    TryFindHallwayDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor) ||
                    TryCarveBranchHallwayToRoom(candidate, doorSpan, out doorCell, out horizontalDoor) ||
                    TryFindAdjacentRoomDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor, out adjDist);

                if (!connected) continue;

                // Carve the room floor tiles
                for (int x = rx; x < rx + rw; x++)
                    for (int y = ry; y < ry + rh; y++)
                        _grid[x, y] = CellType.RoomFloor;

                // Carve the connecting doorway
                if (horizontalDoor)
                {
                    CarveHorizontalDoorway(doorCell.x, doorCell.y, doorSpan);
                    for (int dy = 1; dy < adjDist; dy++)
                    {
                        for (int s = 0; s < doorSpan; s++)
                        {
                            _grid[doorCell.x + s, doorCell.y + dy] = CellType.Doorway;
                            _doorwayCells.Add(new Vector2Int(doorCell.x + s, doorCell.y + dy));
                        }
                    }
                }
                else
                {
                    CarveVerticalDoorway(doorCell.x, doorCell.y, doorSpan);
                    for (int dx = 1; dx < adjDist; dx++)
                    {
                        for (int s = 0; s < doorSpan; s++)
                        {
                            _grid[doorCell.x + dx, doorCell.y + s] = CellType.Doorway;
                            _doorwayCells.Add(new Vector2Int(doorCell.x + dx, doorCell.y + s));
                        }
                    }
                }

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

                if (tryArch == RoomArchetype.Auditorium) placedAuditoriums++;
                else if (tryArch == RoomArchetype.LargeWorkshop) placedWorkshops++;
                else if (tryArch == RoomArchetype.Closet) floorClosetCount++;

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
                width = slightlySmallerFallback ? rng.Next(13, 19) : rng.Next(16, 24);
                height = slightlySmallerFallback ? rng.Next(13, 19) : rng.Next(16, 24);
                break;

            case RoomArchetype.Auditorium:
                bool wideOrientation = rng.Next(2) == 0;
                int longSide = slightlySmallerFallback ? rng.Next(20, 26) : rng.Next(26, 33);
                int shortSide = slightlySmallerFallback ? rng.Next(16, 21) : rng.Next(20, 27);
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
                targetDoors = rng.Next(2, 4);
                targetWindows = rng.Next(1, 3);
                break;

            case RoomArchetype.LargeWorkshop:
                targetDoors = rng.Next(3, 5);
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
        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.IsObjectiveRoom || room.TargetDoorways <= 1) continue;

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

        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.IsObjectiveRoom) continue;

            int minRequired = room.Archetype switch
            {
                RoomArchetype.Auditorium => 3,
                RoomArchetype.LargeWorkshop => 3,
                RoomArchetype.StandardRoom => 2,
                RoomArchetype.Closet => 1,
                _ => 2
            };

            int safety = 0;
            while (GetRoomDoorwayWallCount(room.Bounds) < minRequired && safety < 4)
            {
                ForceSecondHallwayEntranceForRoom(room.Bounds);
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
        Color wallCol = _config != null ? _config.WallColor : new Color(0.36f, 0.37f, 0.38f, 1f);
        window.Initialize(winIdx, this, worldCenter, windowSize, chosen.horizontal, wallCol);
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
                if (_grid[x, y] == CellType.Doorway || _grid[x, y] == CellType.HallwayFloor) return true;
        }
        else if (wallSide == 1)
        {
            int x = room.xMax;
            if (x < 0 || x >= _width) return false;
            for (int y = room.yMin; y < room.yMax; y++)
                if (_grid[x, y] == CellType.Doorway || _grid[x, y] == CellType.HallwayFloor) return true;
        }
        else if (wallSide == 2)
        {
            int y = room.yMin - 1;
            if (y < 0 || y >= _height) return false;
            for (int x = room.xMin; x < room.xMax; x++)
                if (_grid[x, y] == CellType.Doorway || _grid[x, y] == CellType.HallwayFloor) return true;
        }
        else if (wallSide == 3)
        {
            int x = room.xMin - 1;
            if (x < 0 || x >= _width) return false;
            for (int y = room.yMin; y < room.yMax; y++)
                if (_grid[x, y] == CellType.Doorway || _grid[x, y] == CellType.HallwayFloor) return true;
        }
        return false;
    }

    private bool TryConnectRoomWallOutward(RectInt room, int wallSide)
    {
        int hw = GetConfiguredHallwayWidth();
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 3 : GetDoorwaySpan();
        if (room.width < doorSpan + 4 || room.height < doorSpan + 4) return false;

        const int margin = 2;
        int centerDoorX = Mathf.Clamp(room.xMin + (room.width - doorSpan) / 2, room.xMin + margin, room.xMax - doorSpan - margin);
        int centerDoorY = Mathf.Clamp(room.yMin + (room.height - doorSpan) / 2, room.yMin + margin, room.yMax - doorSpan - margin);
        const int maxDist = 36;
        int[] doorOffsets = { 0, -2, 2, -4, 4 };

        if (wallSide == 0) // North
        {
            if (room.yMax >= _height - 2) return false;
            for (int oi = 0; oi < doorOffsets.Length; oi++)
            {
                int doorX = Mathf.Clamp(centerDoorX + doorOffsets[oi], room.xMin + margin, room.xMax - doorSpan - margin);
                if (doorX + doorSpan >= _width - 2) continue;
                if (!HasWallContinuity(doorX - 1, room.yMax, doorX, room.yMax, doorSpan, true) ||
                    !HasWallContinuity(doorX + doorSpan, room.yMax, doorX, room.yMax, doorSpan, true)) continue;

                int hallStartX = Mathf.Clamp(doorX - (hw - doorSpan) / 2, 2, _width - hw - 2);
                bool connected = false;

                for (int dist = 1; dist <= maxDist; dist++)
                {
                    int cy = room.yMax + dist;
                    if (cy >= _height - 2) break;

                    if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                    {
                        if (dist > 1 && !IsRegionPureWall(hallStartX, room.yMax + 1, hw, dist - 1)) break;

                        for (int y = room.yMax + 1; y < cy; y++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(hallStartX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                        return true;
                    }

                    if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                    {
                        if (dist <= 2 && !DoesRoomWallHaveDoorway(room, 0))
                        {
                            CarveHorizontalDoorway(doorX, room.yMax, doorSpan);
                            for (int dy = 1; dy < dist; dy++)
                            {
                                for (int s = 0; s < doorSpan; s++)
                                {
                                    _grid[doorX + s, room.yMax + dy] = CellType.Doorway;
                                    _doorwayCells.Add(new Vector2Int(doorX + s, room.yMax + dy));
                                }
                            }
                            return true;
                        }
                        break;
                    }

                    if (!SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.Wall)) break;
                }
                if (connected) return true;
            }
        }
        else if (wallSide == 2) // South
        {
            if (room.yMin - 1 <= 1) return false;
            for (int oi = 0; oi < doorOffsets.Length; oi++)
            {
                int doorX = Mathf.Clamp(centerDoorX + doorOffsets[oi], room.xMin + margin, room.xMax - doorSpan - margin);
                if (doorX + doorSpan >= _width - 2) continue;
                if (!HasWallContinuity(doorX - 1, room.yMin - 1, doorX, room.yMin - 1, doorSpan, true) ||
                    !HasWallContinuity(doorX + doorSpan, room.yMin - 1, doorX, room.yMin - 1, doorSpan, true)) continue;

                int hallStartX = Mathf.Clamp(doorX - (hw - doorSpan) / 2, 2, _width - hw - 2);

                for (int dist = 1; dist <= maxDist; dist++)
                {
                    int cy = room.yMin - 1 - dist;
                    if (cy <= 1) break;

                    if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                    {
                        if (dist > 1 && !IsRegionPureWall(hallStartX, cy + 1, hw, dist - 1)) break;

                        for (int y = cy + 1; y <= room.yMin - 2; y++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(hallStartX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                        return true;
                    }

                    if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                    {
                        if (dist <= 2 && !DoesRoomWallHaveDoorway(room, 2))
                        {
                            CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan);
                            for (int dy = 1; dy < dist; dy++)
                            {
                                for (int s = 0; s < doorSpan; s++)
                                {
                                    _grid[doorX + s, room.yMin - 1 - dy] = CellType.Doorway;
                                    _doorwayCells.Add(new Vector2Int(doorX + s, room.yMin - 1 - dy));
                                }
                            }
                            return true;
                        }
                        break;
                    }

                    if (!SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.Wall)) break;
                }
            }
        }
        else if (wallSide == 1) // East
        {
            if (room.xMax >= _width - 2) return false;
            for (int oi = 0; oi < doorOffsets.Length; oi++)
            {
                int doorY = Mathf.Clamp(centerDoorY + doorOffsets[oi], room.yMin + margin, room.yMax - doorSpan - margin);
                if (doorY + doorSpan >= _height - 2) continue;
                if (!HasWallContinuity(room.xMax, doorY - 1, room.xMax, doorY, doorSpan, false) ||
                    !HasWallContinuity(room.xMax, doorY + doorSpan, room.xMax, doorY, doorSpan, false)) continue;

                int hallStartY = Mathf.Clamp(doorY - (hw - doorSpan) / 2, 2, _height - hw - 2);

                for (int dist = 1; dist <= maxDist; dist++)
                {
                    int cx = room.xMax + dist;
                    if (cx >= _width - 2) break;

                    if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                    {
                        if (dist > 1 && !IsRegionPureWall(room.xMax + 1, hallStartY, dist - 1, hw)) break;

                        for (int x = room.xMax + 1; x < cx; x++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(x, hallStartY + w);

                        CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                        return true;
                    }

                    if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                    {
                        if (dist <= 2 && !DoesRoomWallHaveDoorway(room, 1))
                        {
                            CarveVerticalDoorway(room.xMax, doorY, doorSpan);
                            for (int dx = 1; dx < dist; dx++)
                            {
                                for (int s = 0; s < doorSpan; s++)
                                {
                                    _grid[room.xMax + dx, doorY + s] = CellType.Doorway;
                                    _doorwayCells.Add(new Vector2Int(room.xMax + dx, doorY + s));
                                }
                            }
                            return true;
                        }
                        break;
                    }

                    if (!SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.Wall)) break;
                }
            }
        }
        else if (wallSide == 3) // West
        {
            if (room.xMin - 1 <= 1) return false;
            for (int oi = 0; oi < doorOffsets.Length; oi++)
            {
                int doorY = Mathf.Clamp(centerDoorY + doorOffsets[oi], room.yMin + margin, room.yMax - doorSpan - margin);
                if (doorY + doorSpan >= _height - 2) continue;
                if (!HasWallContinuity(room.xMin - 1, doorY - 1, room.xMin - 1, doorY, doorSpan, false) ||
                    !HasWallContinuity(room.xMin - 1, doorY + doorSpan, room.xMin - 1, doorY, doorSpan, false)) continue;

                int hallStartY = Mathf.Clamp(doorY - (hw - doorSpan) / 2, 2, _height - hw - 2);

                for (int dist = 1; dist <= maxDist; dist++)
                {
                    int cx = room.xMin - 1 - dist;
                    if (cx <= 1) break;

                    if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                    {
                        if (dist > 1 && !IsRegionPureWall(cx + 1, hallStartY, dist - 1, hw)) break;

                        for (int x = cx + 1; x <= room.xMin - 2; x++)
                            for (int w = 0; w < hw; w++)
                                SetHallwayIfWall(x, hallStartY + w);

                        CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                        return true;
                    }

                    if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                    {
                        if (dist <= 2 && !DoesRoomWallHaveDoorway(room, 3))
                        {
                            CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan);
                            for (int dx = 1; dx < dist; dx++)
                            {
                                for (int s = 0; s < doorSpan; s++)
                                {
                                    _grid[room.xMin - 1 - dx, doorY + s] = CellType.Doorway;
                                    _doorwayCells.Add(new Vector2Int(room.xMin - 1 - dx, doorY + s));
                                }
                            }
                            return true;
                        }
                        break;
                    }

                    if (!SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.Wall)) break;
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

    private void ForceSecondHallwayEntranceForRoom(RectInt room)
    {
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

            if (TryConnectRoomWallOutward(room, side)) return;
        }

        // Direct adjacent room doorway fallback
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 3 : GetDoorwaySpan();
        var rng = new System.Random();
        if (TryFindAdjacentRoomDoorwaySpot(room, doorSpan, rng, out Vector2Int dCell, out bool horiz, out int adjDist))
        {
            if (horiz)
            {
                CarveHorizontalDoorway(dCell.x, dCell.y, doorSpan);
                for (int dy = 1; dy < adjDist; dy++)
                {
                    for (int s = 0; s < doorSpan; s++)
                    {
                        _grid[dCell.x + s, dCell.y + dy] = CellType.Doorway;
                        _doorwayCells.Add(new Vector2Int(dCell.x + s, dCell.y + dy));
                    }
                }
            }
            else
            {
                CarveVerticalDoorway(dCell.x, dCell.y, doorSpan);
                for (int dx = 1; dx < adjDist; dx++)
                {
                    for (int s = 0; s < doorSpan; s++)
                    {
                        _grid[dCell.x + dx, dCell.y + s] = CellType.Doorway;
                        _doorwayCells.Add(new Vector2Int(dCell.x + dx, dCell.y + s));
                    }
                }
            }
            return;
        }

        // Branch hallway fallback
        if (TryCarveBranchHallwayToRoom(room, doorSpan, out Vector2Int bCell, out bool bHoriz))
        {
            if (bHoriz) CarveHorizontalDoorway(bCell.x, bCell.y, doorSpan);
            else CarveVerticalDoorway(bCell.x, bCell.y, doorSpan);
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
        out bool horizontalDoor,
        out int dist)
    {
        var candidates = new List<(Vector2Int cell, bool horiz, int dist)>();

        // North wall: y = room.yMax
        for (int d = 1; d <= 2; d++)
        {
            int checkY = room.yMax + d;
            if (checkY < _height - 1)
            {
                for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
                {
                    if (SpanMatchesRoomFloor(x, checkY, doorSpan, true))
                    {
                        bool pureWall = true;
                        for (int dy = 0; dy < d; dy++)
                            if (!SpanMatchesCellType(x, room.yMax + dy, doorSpan, true, CellType.Wall)) { pureWall = false; break; }
                        if (pureWall) candidates.Add((new Vector2Int(x, room.yMax), true, d));
                    }
                }
            }
        }

        // South wall: y = room.yMin - 1
        for (int d = 1; d <= 2; d++)
        {
            int checkY = room.yMin - 1 - d;
            if (checkY >= 1)
            {
                for (int x = room.xMin + 1; x <= room.xMax - 1 - doorSpan; x++)
                {
                    if (SpanMatchesRoomFloor(x, checkY, doorSpan, true))
                    {
                        bool pureWall = true;
                        for (int dy = 0; dy < d; dy++)
                            if (!SpanMatchesCellType(x, room.yMin - 1 - dy, doorSpan, true, CellType.Wall)) { pureWall = false; break; }
                        if (pureWall) candidates.Add((new Vector2Int(x, room.yMin - 1), true, d));
                    }
                }
            }
        }

        // East wall: x = room.xMax
        for (int d = 1; d <= 2; d++)
        {
            int checkX = room.xMax + d;
            if (checkX < _width - 1)
            {
                for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
                {
                    if (SpanMatchesRoomFloor(checkX, y, doorSpan, false))
                    {
                        bool pureWall = true;
                        for (int dx = 0; dx < d; dx++)
                            if (!SpanMatchesCellType(room.xMax + dx, y, doorSpan, false, CellType.Wall)) { pureWall = false; break; }
                        if (pureWall) candidates.Add((new Vector2Int(room.xMax, y), false, d));
                    }
                }
            }
        }

        // West wall: x = room.xMin - 1
        for (int d = 1; d <= 2; d++)
        {
            int checkX = room.xMin - 1 - d;
            if (checkX >= 1)
            {
                for (int y = room.yMin + 1; y <= room.yMax - 1 - doorSpan; y++)
                {
                    if (SpanMatchesRoomFloor(checkX, y, doorSpan, false))
                    {
                        bool pureWall = true;
                        for (int dx = 0; dx < d; dx++)
                            if (!SpanMatchesCellType(room.xMin - 1 - dx, y, doorSpan, false, CellType.Wall)) { pureWall = false; break; }
                        if (pureWall) candidates.Add((new Vector2Int(room.xMin - 1, y), false, d));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            doorCell = default;
            horizontalDoor = false;
            dist = 1;
            return false;
        }

        var chosen = candidates[rng.Next(candidates.Count)];
        doorCell = chosen.cell;
        horizontalDoor = chosen.horiz;
        dist = chosen.dist;
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
        int marginX = (room.width >= doorSpan + 4) ? 2 : 1;
        int marginY = (room.height >= doorSpan + 4) ? 2 : 1;
        var candidates = new List<(Vector2Int cell, bool horiz)>();

        void CollectCandidates(int mX, int mY)
        {
            candidates.Clear();
            if (room.yMax + 1 < _height - 1)
            {
                for (int x = room.xMin + mX; x <= room.xMax - mX - doorSpan; x++)
                {
                    bool valid = true;
                    for (int s = 0; s < doorSpan; s++)
                    {
                        if (_grid[x + s, room.yMax] != CellType.Wall ||
                            _grid[x + s, room.yMax + 1] != CellType.HallwayFloor ||
                            (_grid[x + s, room.yMax - 1] != CellType.RoomFloor && _grid[x + s, room.yMax - 1] != CellType.ObjectiveRoomFloor))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid &&
                        HasWallContinuity(x - 1, room.yMax, x, room.yMax, doorSpan, true) &&
                        HasWallContinuity(x + doorSpan, room.yMax, x, room.yMax, doorSpan, true))
                    {
                        candidates.Add((new Vector2Int(x, room.yMax), true));
                    }
                }
            }

            if (room.yMin - 2 >= 1)
            {
                for (int x = room.xMin + mX; x <= room.xMax - mX - doorSpan; x++)
                {
                    bool valid = true;
                    for (int s = 0; s < doorSpan; s++)
                    {
                        if (_grid[x + s, room.yMin - 1] != CellType.Wall ||
                            _grid[x + s, room.yMin - 2] != CellType.HallwayFloor ||
                            (_grid[x + s, room.yMin] != CellType.RoomFloor && _grid[x + s, room.yMin] != CellType.ObjectiveRoomFloor))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid &&
                        HasWallContinuity(x - 1, room.yMin - 1, x, room.yMin - 1, doorSpan, true) &&
                        HasWallContinuity(x + doorSpan, room.yMin - 1, x, room.yMin - 1, doorSpan, true))
                    {
                        candidates.Add((new Vector2Int(x, room.yMin - 1), true));
                    }
                }
            }

            if (room.xMax + 1 < _width - 1)
            {
                for (int y = room.yMin + mY; y <= room.yMax - mY - doorSpan; y++)
                {
                    bool valid = true;
                    for (int s = 0; s < doorSpan; s++)
                    {
                        if (_grid[room.xMax, y + s] != CellType.Wall ||
                            _grid[room.xMax + 1, y + s] != CellType.HallwayFloor ||
                            (_grid[room.xMax - 1, y + s] != CellType.RoomFloor && _grid[room.xMax - 1, y + s] != CellType.ObjectiveRoomFloor))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid &&
                        HasWallContinuity(room.xMax, y - 1, room.xMax, y, doorSpan, false) &&
                        HasWallContinuity(room.xMax, y + doorSpan, room.xMax, y, doorSpan, false))
                    {
                        candidates.Add((new Vector2Int(room.xMax, y), false));
                    }
                }
            }

            if (room.xMin - 2 >= 1)
            {
                for (int y = room.yMin + mY; y <= room.yMax - mY - doorSpan; y++)
                {
                    bool valid = true;
                    for (int s = 0; s < doorSpan; s++)
                    {
                        if (_grid[room.xMin - 1, y + s] != CellType.Wall ||
                            _grid[room.xMin - 2, y + s] != CellType.HallwayFloor ||
                            (_grid[room.xMin, y + s] != CellType.RoomFloor && _grid[room.xMin, y + s] != CellType.ObjectiveRoomFloor))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid &&
                        HasWallContinuity(room.xMin - 1, y - 1, room.xMin - 1, y, doorSpan, false) &&
                        HasWallContinuity(room.xMin - 1, y + doorSpan, room.xMin - 1, y, doorSpan, false))
                    {
                        candidates.Add((new Vector2Int(room.xMin - 1, y), false));
                    }
                }
            }
        }

        CollectCandidates(marginX, marginY);
        if (candidates.Count == 0 && (marginX > 1 || marginY > 1))
        {
            CollectCandidates(1, 1);
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

        MarkDoorwayClearanceZone(x - 2, y - 4, doorSpan + 4, 9);
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

        MarkDoorwayClearanceZone(x - 4, y - 2, 9, doorSpan + 4);
    }

    private bool HasWallContinuity(int wx, int wy, int doorStartX, int doorStartY, int span, bool horizontal)
    {
        if (wx < 0 || wx >= _width || wy < 0 || wy >= _height) return false;
        if (_grid[wx, wy] != CellType.Wall && _grid[wx, wy] != CellType.Window) return false;

        // Perimeter walls always have continuity along building perimeter
        if (wx == 0 || wx == _width - 1 || wy == 0 || wy == _height - 1) return true;

        int[] cdx = { 0, 0, -1, 1 };
        int[] cdy = { 1, -1, 0, 0 };
        for (int d = 0; d < 4; d++)
        {
            int nx = wx + cdx[d];
            int ny = wy + cdy[d];
            if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
            // Exclude the doorway opening itself
            if (horizontal && ny == doorStartY && nx >= doorStartX && nx < doorStartX + span) continue;
            if (!horizontal && nx == doorStartX && ny >= doorStartY && ny < doorStartY + span) continue;

            if (_grid[nx, ny] == CellType.Wall || _grid[nx, ny] == CellType.Window)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsDoorwayDuplicate(DesignatedDoorway candidate, List<DesignatedDoorway> approved)
    {
        for (int i = 0; i < approved.Count; i++)
        {
            var other = approved[i];
            if (candidate.IsHorizontal != other.IsHorizontal) continue;

            if (candidate.IsHorizontal)
            {
                if (Mathf.Abs(candidate.StartY - other.StartY) <= 2)
                {
                    int overlapMin = Mathf.Max(candidate.StartX, other.StartX);
                    int overlapMax = Mathf.Min(candidate.StartX + candidate.Span, other.StartX + other.Span);
                    if (overlapMax - overlapMin >= 2) return true;
                }
            }
            else
            {
                if (Mathf.Abs(candidate.StartX - other.StartX) <= 2)
                {
                    int overlapMin = Mathf.Max(candidate.StartY, other.StartY);
                    int overlapMax = Mathf.Min(candidate.StartY + candidate.Span, other.StartY + other.Span);
                    if (overlapMax - overlapMin >= 2) return true;
                }
            }
        }
        return false;
    }

    private bool IsInsideSameRoom(int x1, int y1, int x2, int y2)
    {
        if (_rooms == null) return false;
        var p1 = new Vector2Int(x1, y1);
        var p2 = new Vector2Int(x2, y2);
        for (int i = 0; i < _rooms.Count; i++)
        {
            RectInt b = _rooms[i].Bounds;
            if (b.Contains(p1) && b.Contains(p2)) return true;
        }
        return false;
    }

    /// <summary>
    /// Post-carve pass that validates every designated doorway, prevents any door from spawning
    /// inside a solid wall or floating on an isolated pillar, anchors door hinges flush to solid wall jambs,
    /// and scales doors so they fill 100.0% of the entire doorway width from jamb to jamb.
    /// </summary>
    private void NormalizeAndSpawnAllDoorsForFloor(int floorLevel)
    {
        if (_currentFloor == null) return;

        bool[,] handled = new bool[_width, _height];
        var designated = _currentFloor.DesignatedDoorways;
        var approvedDoorways = new List<DesignatedDoorway>();

        for (int i = 0; i < designated.Count; i++)
        {
            DesignatedDoorway dd = designated[i];
            int x = dd.StartX;
            int y = dd.StartY;
            int span = dd.Span;

            if (dd.IsHorizontal)
            {
                if (x < 1 || x + span >= _width - 1 || y < 0 || y >= _height) continue;

                // Check if any tile in this doorway was already handled
                bool alreadyHandled = false;
                for (int s = 0; s < span; s++)
                {
                    if (handled[x + s, y]) { alreadyHandled = true; break; }
                }
                if (alreadyHandled) continue;

                // Deduplication check: no duplicate or back-to-back parallel doorways within 2 tiles
                if (IsDoorwayDuplicate(dd, approvedDoorways))
                {
                    for (int s = 0; s < span; s++)
                    {
                        handled[x + s, y] = true;
                    }
                    continue;
                }

                // ── SAFETY CHECK 1: Ensure doorway connects two open walkable spaces (North & South) ──
                bool isPerimeter = (y == 0 || y == _height - 1);
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
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x + s, y] == CellType.Doorway)
                            _grid[x + s, y] = CellType.Wall;
                    }
                    continue;
                }

                // ── SAFETY CHECK 2: Distinct spaces check (door cannot be in the middle of a single room) ──
                if (!isPerimeter && IsInsideSameRoom(x + span / 2, y + 1, x + span / 2, y - 1))
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x + s, y] == CellType.Doorway)
                            _grid[x + s, y] = CellType.RoomFloor;
                    }
                    continue;
                }

                // ── SAFETY CHECK 3: Solid contiguous wall jambs (NO standalone 1x1 pillars!) ──
                if (!isPerimeter)
                {
                    // Both jambs must ALREADY be solid Wall or Window
                    if ((_grid[x - 1, y] != CellType.Wall && _grid[x - 1, y] != CellType.Window) ||
                        (_grid[x + span, y] != CellType.Wall && _grid[x + span, y] != CellType.Window))
                    {
                        // Doorway lacks solid wall jambs — do NOT spawn door!
                        for (int s = 0; s < span; s++)
                        {
                            if (_grid[x + s, y] == CellType.Doorway)
                                _grid[x + s, y] = CellType.HallwayFloor;
                        }
                        continue;
                    }

                    // Neither jamb may be an isolated 1x1 pillar!
                    if (!HasWallContinuity(x - 1, y, x, y, span, true) ||
                        !HasWallContinuity(x + span, y, x, y, span, true))
                    {
                        // Isolated pillar jamb detected — do NOT spawn door!
                        for (int s = 0; s < span; s++)
                        {
                            if (_grid[x + s, y] == CellType.Doorway)
                                _grid[x + s, y] = CellType.HallwayFloor;
                        }
                        continue;
                    }
                }

                // All safety checks passed: mark cells and approve
                for (int s = 0; s < span; s++)
                {
                    _grid[x + s, y] = CellType.Doorway;
                    handled[x + s, y] = true;
                }
                approvedDoorways.Add(dd);

                // Clear any cover pillars in the entire door swing and approach zone
                for (int cx = x - 2; cx <= x + span + 1; cx++)
                {
                    for (int cy = y - 4; cy <= y + 4; cy++)
                    {
                        if (cx >= 0 && cx < _width && cy >= 0 && cy < _height)
                        {
                            if (_grid[cx, cy] == CellType.CoverPillar)
                                _grid[cx, cy] = CellType.RoomFloor;
                        }
                    }
                }

                // Spawn doors flush to wall jambs, filling 100% of doorway width
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

                bool alreadyHandled = false;
                for (int s = 0; s < span; s++)
                {
                    if (handled[x, y + s]) { alreadyHandled = true; break; }
                }
                if (alreadyHandled) continue;

                // Deduplication check
                if (IsDoorwayDuplicate(dd, approvedDoorways))
                {
                    for (int s = 0; s < span; s++)
                    {
                        handled[x, y + s] = true;
                    }
                    continue;
                }

                // ── SAFETY CHECK 1: Ensure doorway connects two open walkable spaces (East & West) ──
                bool isPerimeter = (x == 0 || x == _width - 1);
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
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x, y + s] == CellType.Doorway)
                            _grid[x, y + s] = CellType.Wall;
                    }
                    continue;
                }

                // ── SAFETY CHECK 2: Distinct spaces check (door cannot be in the middle of a single room) ──
                if (!isPerimeter && IsInsideSameRoom(x + 1, y + span / 2, x - 1, y + span / 2))
                {
                    for (int s = 0; s < span; s++)
                    {
                        if (_grid[x, y + s] == CellType.Doorway)
                            _grid[x, y + s] = CellType.RoomFloor;
                    }
                    continue;
                }

                // ── SAFETY CHECK 3: Solid contiguous wall jambs (NO standalone 1x1 pillars!) ──
                if (!isPerimeter)
                {
                    // Both jambs must ALREADY be solid Wall or Window
                    if ((_grid[x, y - 1] != CellType.Wall && _grid[x, y - 1] != CellType.Window) ||
                        (_grid[x, y + span] != CellType.Wall && _grid[x, y + span] != CellType.Window))
                    {
                        // Doorway lacks solid wall jambs — do NOT spawn door!
                        for (int s = 0; s < span; s++)
                        {
                            if (_grid[x, y + s] == CellType.Doorway)
                                _grid[x, y + s] = CellType.HallwayFloor;
                        }
                        continue;
                    }

                    // Neither jamb may be an isolated 1x1 pillar!
                    if (!HasWallContinuity(x, y - 1, x, y, span, false) ||
                        !HasWallContinuity(x, y + span, x, y, span, false))
                    {
                        // Isolated pillar jamb detected — do NOT spawn door!
                        for (int s = 0; s < span; s++)
                        {
                            if (_grid[x, y + s] == CellType.Doorway)
                                _grid[x, y + s] = CellType.HallwayFloor;
                        }
                        continue;
                    }
                }

                // All safety checks passed: mark cells and approve
                for (int s = 0; s < span; s++)
                {
                    _grid[x, y + s] = CellType.Doorway;
                    handled[x, y + s] = true;
                }
                approvedDoorways.Add(dd);

                // Clear any cover pillars in the entire door swing and approach zone
                for (int cx = x - 4; cx <= x + 4; cx++)
                {
                    for (int cy = y - 2; cy <= y + span + 1; cy++)
                    {
                        if (cx >= 0 && cx < _width && cy >= 0 && cy < _height)
                        {
                            if (_grid[cx, cy] == CellType.CoverPillar)
                                _grid[cx, cy] = CellType.RoomFloor;
                        }
                    }
                }

                // Spawn doors flush to wall jambs, filling 100% of doorway width
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

                // SAFETY CHECK: Verify EVERY tile of the proposed pillar is clear of doorways, swing arcs, and entrances
                bool blocked = false;
                for (int dx = 0; dx < pillarSize; dx++)
                {
                    for (int dy = 0; dy < pillarSize; dy++)
                    {
                        int cx = px + dx;
                        int cy = py + dy;
                        if (_entranceProtectedZone[cx, cy] || IsNearAnyDoorwayOrEntrance(cx, cy, 5.0f))
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) break;
                }
                if (blocked) continue;

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

        if (_currentFloor != null)
        {
            for (int i = 0; i < _currentFloor.DesignatedDoorways.Count; i++)
            {
                var dd = _currentFloor.DesignatedDoorways[i];
                int minX = dd.IsHorizontal ? dd.StartX - 2 : dd.StartX - 4;
                int maxX = dd.IsHorizontal ? dd.StartX + dd.Span + 1 : dd.StartX + 4;
                int minY = dd.IsHorizontal ? dd.StartY - 4 : dd.StartY - 2;
                int maxY = dd.IsHorizontal ? dd.StartY + 4 : dd.StartY + dd.Span + 1;
                if (gx >= minX && gx <= maxX && gy >= minY && gy <= maxY) return true;

                for (int s = 0; s < dd.Span; s++)
                {
                    Vector2 d = dd.IsHorizontal ? new Vector2(dd.StartX + s, dd.StartY) : new Vector2(dd.StartX, dd.StartY + s);
                    if ((cell - d).sqrMagnitude < minDistSq) return true;
                }
            }
        }

        if (_currentFloor != null && _currentFloor.FloorLevel == 0)
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
                int gx = Mathf.Clamp(ent.GridCell.x, 0, _width - hw);
                int gy = Mathf.Clamp(ent.GridCell.y, 0, _height - hw);

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
            for (int dx = -4; dx <= 4; dx++)
            {
                for (int dy = -4; dy <= 4; dy++)
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

        if (_currentFloor != null)
        {
            for (int i = 0; i < _currentFloor.DesignatedDoorways.Count; i++)
            {
                var dd = _currentFloor.DesignatedDoorways[i];
                int minX = dd.IsHorizontal ? dd.StartX - 2 : dd.StartX - 4;
                int maxX = dd.IsHorizontal ? dd.StartX + dd.Span + 1 : dd.StartX + 4;
                int minY = dd.IsHorizontal ? dd.StartY - 4 : dd.StartY - 2;
                int maxY = dd.IsHorizontal ? dd.StartY + 4 : dd.StartY + dd.Span + 1;

                for (int cx = minX; cx <= maxX; cx++)
                {
                    for (int cy = minY; cy <= maxY; cy++)
                    {
                        if (cx > 0 && cx < _width - 1 && cy > 0 && cy < _height - 1)
                        {
                            if (_grid[cx, cy] == CellType.CoverPillar)
                            {
                                _grid[cx, cy] = CellType.RoomFloor;
                            }
                        }
                    }
                }
            }
        }

        if (floorLevel == 0)
        {
            EnsureAllEntrancesConnectedViaBfs(ringOuter, hw);
        }

        // Safety Pass: Trim any dead-end hallway stubs that lead to nothing back to doorways and intersections
        PruneDeadEndHallwayStubs(floorLevel);

        // Safety Pass: Ensure every HallwayFloor passage across the floor is at least >= 1.5x the player's body wide
        EnforceMinimumHallwayWidthOnGrid();

        // Ensure stairwell bay walls and landing throats remain 100% locked
        CarveAndReserveStairwellBaysForFloor(floorLevel);
    }

    /// <summary>
    /// Scans the floor grid for hallway corridor stubs that terminate against solid walls with no
    /// doorways or branching paths, and iteratively prunes them slice-by-slice back to the nearest
    /// doorway or corridor intersection.
    /// Preserves all entrance vestibules, stairwell landing bays, and doorway clearance zones.
    /// </summary>
    private void PruneDeadEndHallwayStubs(int floorLevel)
    {
        if (_currentFloor == null) return;

        bool prunedAny = true;
        int maxPasses = 100;
        int pass = 0;

        while (prunedAny && pass < maxPasses)
        {
            prunedAny = false;
            pass++;

            // 1. East-facing stubs (pruning vertical column x from East to West)
            for (int x = _width - 3; x >= 2; x--)
            {
                for (int y = 2; y < _height - 2; y++)
                {
                    if (_grid[x, y] == CellType.HallwayFloor)
                    {
                        int yStart = y;
                        while (y < _height - 2 && _grid[x, y] == CellType.HallwayFloor) y++;
                        int yEnd = y - 1;
                        int span = yEnd - yStart + 1;
                        if (span >= 2 && span <= 14)
                        {
                            bool canPrune = true;
                            for (int cy = yStart; cy <= yEnd; cy++)
                            {
                                if (x + 1 < _width && _grid[x + 1, cy] != CellType.Wall) { canPrune = false; break; }
                                if (_entranceProtectedZone[x, cy] || IsNearAnyDoorwayOrEntrance(x, cy, 2.5f)) { canPrune = false; break; }
                            }
                            if (canPrune && _grid[x, yStart - 1] == CellType.Wall && _grid[x, yEnd + 1] == CellType.Wall)
                            {
                                for (int cy = yStart; cy <= yEnd; cy++)
                                {
                                    _grid[x, cy] = CellType.Wall;
                                }
                                prunedAny = true;
                            }
                        }
                    }
                }
            }

            // 2. West-facing stubs (pruning vertical column x from West to East)
            for (int x = 2; x <= _width - 3; x++)
            {
                for (int y = 2; y < _height - 2; y++)
                {
                    if (_grid[x, y] == CellType.HallwayFloor)
                    {
                        int yStart = y;
                        while (y < _height - 2 && _grid[x, y] == CellType.HallwayFloor) y++;
                        int yEnd = y - 1;
                        int span = yEnd - yStart + 1;
                        if (span >= 2 && span <= 14)
                        {
                            bool canPrune = true;
                            for (int cy = yStart; cy <= yEnd; cy++)
                            {
                                if (x - 1 >= 0 && _grid[x - 1, cy] != CellType.Wall) { canPrune = false; break; }
                                if (_entranceProtectedZone[x, cy] || IsNearAnyDoorwayOrEntrance(x, cy, 2.5f)) { canPrune = false; break; }
                            }
                            if (canPrune && _grid[x, yStart - 1] == CellType.Wall && _grid[x, yEnd + 1] == CellType.Wall)
                            {
                                for (int cy = yStart; cy <= yEnd; cy++)
                                {
                                    _grid[x, cy] = CellType.Wall;
                                }
                                prunedAny = true;
                            }
                        }
                    }
                }
            }

            // 3. North-facing stubs (pruning horizontal row y from North to South)
            for (int y = _height - 3; y >= 2; y--)
            {
                for (int x = 2; x < _width - 2; x++)
                {
                    if (_grid[x, y] == CellType.HallwayFloor)
                    {
                        int xStart = x;
                        while (x < _width - 2 && _grid[x, y] == CellType.HallwayFloor) x++;
                        int xEnd = x - 1;
                        int span = xEnd - xStart + 1;
                        if (span >= 2 && span <= 14)
                        {
                            bool canPrune = true;
                            for (int cx = xStart; cx <= xEnd; cx++)
                            {
                                if (y + 1 < _height && _grid[cx, y + 1] != CellType.Wall) { canPrune = false; break; }
                                if (_entranceProtectedZone[cx, y] || IsNearAnyDoorwayOrEntrance(cx, y, 2.5f)) { canPrune = false; break; }
                            }
                            if (canPrune && _grid[xStart - 1, y] == CellType.Wall && _grid[xEnd + 1, y] == CellType.Wall)
                            {
                                for (int cx = xStart; cx <= xEnd; cx++)
                                {
                                    _grid[cx, y] = CellType.Wall;
                                }
                                prunedAny = true;
                            }
                        }
                    }
                }
            }

            // 4. South-facing stubs (pruning horizontal row y from South to North)
            for (int y = 2; y <= _height - 3; y++)
            {
                for (int x = 2; x < _width - 2; x++)
                {
                    if (_grid[x, y] == CellType.HallwayFloor)
                    {
                        int xStart = x;
                        while (x < _width - 2 && _grid[x, y] == CellType.HallwayFloor) x++;
                        int xEnd = x - 1;
                        int span = xEnd - xStart + 1;
                        if (span >= 2 && span <= 14)
                        {
                            bool canPrune = true;
                            for (int cx = xStart; cx <= xEnd; cx++)
                            {
                                if (y - 1 >= 0 && _grid[cx, y - 1] != CellType.Wall) { canPrune = false; break; }
                                if (_entranceProtectedZone[cx, y] || IsNearAnyDoorwayOrEntrance(cx, y, 2.5f)) { canPrune = false; break; }
                            }
                            if (canPrune && _grid[xStart - 1, y] == CellType.Wall && _grid[xEnd + 1, y] == CellType.Wall)
                            {
                                for (int cx = xStart; cx <= xEnd; cx++)
                                {
                                    _grid[cx, y] = CellType.Wall;
                                }
                                prunedAny = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private bool CanWidenHallwayIntoWall(int wx, int wy)
    {
        if (wx <= 1 || wx >= _width - 2 || wy <= 1 || wy >= _height - 2) return false;
        if (_grid[wx, wy] != CellType.Wall) return false;
        if (_entranceProtectedZone != null && _entranceProtectedZone[wx, wy]) return false;

        // 1. Never convert a wall tile adjacent to any doorway within 2 tiles (preserves jambs!)
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                int nx = wx + dx;
                int ny = wy + dy;
                if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
                {
                    if (_grid[nx, ny] == CellType.Doorway) return false;
                }
            }
        }

        // 2. Never convert an exterior room wall (borders RoomFloor or ObjectiveRoomFloor)
        int[] cdx = { 0, 0, -1, 1 };
        int[] cdy = { 1, -1, 0, 0 };
        for (int d = 0; d < 4; d++)
        {
            int nx = wx + cdx[d];
            int ny = wy + cdy[d];
            if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
            {
                CellType t = _grid[nx, ny];
                if (t == CellType.RoomFloor || t == CellType.ObjectiveRoomFloor) return false;
            }
        }

        // 3. Never convert a wall if doing so would isolate any neighbor wall tile into a 1x1 pillar
        for (int d = 0; d < 4; d++)
        {
            int nx = wx + cdx[d];
            int ny = wy + cdy[d];
            if (nx > 0 && nx < _width - 1 && ny > 0 && ny < _height - 1)
            {
                if (_grid[nx, ny] == CellType.Wall)
                {
                    int remainingSolid = 0;
                    for (int nd = 0; nd < 4; nd++)
                    {
                        int nnx = nx + cdx[nd];
                        int nny = ny + cdy[nd];
                        if (nnx == wx && nny == wy) continue;
                        if (nnx >= 0 && nnx < _width && nny >= 0 && nny < _height)
                        {
                            if (_grid[nnx, nny] == CellType.Wall || _grid[nnx, nny] == CellType.Window)
                                remainingSolid++;
                        }
                    }
                    if (remainingSolid == 0) return false;
                }
            }
        }

        return true;
    }

    private bool CanRevertNarrowDeadEndNotch(int x, int y)
    {
        if (x <= 1 || x >= _width - 2 || y <= 1 || y >= _height - 2) return false;
        if (_grid[x, y] != CellType.HallwayFloor) return false;
        if (_entranceProtectedZone != null && _entranceProtectedZone[x, y]) return false;

        // Never revert actual exterior entrances on floor 0
        if (_currentFloor != null && _currentFloor.FloorLevel == 0 && _entrances != null)
        {
            for (int i = 0; i < _entrances.Length; i++)
            {
                var cell = _entrances[i].GridCell;
                if (Mathf.Abs(x - cell.x) <= 2 && Mathf.Abs(y - cell.y) <= 2) return false;
            }
        }

        int wallCount = 0;
        int[] cdx = { 0, 0, -1, 1 };
        int[] cdy = { 1, -1, 0, 0 };
        for (int d = 0; d < 4; d++)
        {
            int nx = x + cdx[d];
            int ny = y + cdy[d];
            if (nx >= 0 && nx < _width && ny >= 0 && ny < _height)
            {
                if (_grid[nx, ny] == CellType.Wall || _grid[nx, ny] == CellType.Window) wallCount++;
            }
        }
        return wallCount >= 3;
    }

    /// <summary>
    /// Scans all <see cref="CellType.HallwayFloor"/> tiles on the current floor and guarantees that
    /// no hallway segment is narrower than <see cref="GetMinHallwayWidthTiles"/> (&gt;= 1.5x player body diameter).
    /// Any narrow pinch point is automatically widened by converting adjacent <see cref="CellType.Wall"/> tiles.
    /// </summary>
    private void EnforceMinimumHallwayWidthOnGrid()
    {
        int minTiles = GetMinHallwayWidthTiles();

        // Safety pass: Pre-clean 1-tile dead-end notches (surrounded by 3+ solid walls) back to Wall
        bool notchCleaned = true;
        int notchPasses = 0;
        while (notchCleaned && notchPasses < 5)
        {
            notchCleaned = false;
            notchPasses++;
            for (int y = 2; y < _height - 2; y++)
            {
                for (int x = 2; x < _width - 2; x++)
                {
                    if (CanRevertNarrowDeadEndNotch(x, y))
                    {
                        _grid[x, y] = CellType.Wall;
                        notchCleaned = true;
                    }
                }
            }
        }

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
                            if (rightWallX < _width - 2 && CanWidenHallwayIntoWall(rightWallX, y))
                                _grid[rightWallX, y] = CellType.HallwayFloor;
                            else if (leftWallX > 1 && CanWidenHallwayIntoWall(leftWallX, y))
                                _grid[leftWallX, y] = CellType.HallwayFloor;
                            else if (CanRevertNarrowDeadEndNotch(x, y))
                                _grid[x, y] = CellType.Wall;
                        }

                        if (spanY < minTiles && _grid[x, y] == CellType.HallwayFloor)
                        {
                            if (topWallY < _height - 2 && CanWidenHallwayIntoWall(x, topWallY))
                                _grid[x, topWallY] = CellType.HallwayFloor;
                            else if (bottomWallY > 1 && CanWidenHallwayIntoWall(x, bottomWallY))
                                _grid[x, bottomWallY] = CellType.HallwayFloor;
                            else if (CanRevertNarrowDeadEndNotch(x, y))
                                _grid[x, y] = CellType.Wall;
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

    private void EnsureAllEntrancesConnectedViaBfs(RectInt interiorBounds, int hw)
    {
        bool[,] reachable = ComputeReachableFloor();

        for (int e = 0; e < 4; e++)
        {
            Vector2Int entCell = _entrances[e].GridCell;
            if (!reachable[entCell.x, entCell.y])
            {
                ConnectEntranceToReachableNetwork(entCell, reachable, hw);
                reachable = ComputeReachableFloor();
            }
        }
    }

    private void ConnectEntranceToReachableNetwork(Vector2Int startCell, bool[,] reachable, int hw)
    {
        var queue = new Queue<Vector2Int>();
        var visited = new bool[_width, _height];
        var parent = new Vector2Int[_width, _height];

        queue.Enqueue(startCell);
        visited[startCell.x, startCell.y] = true;
        parent[startCell.x, startCell.y] = new Vector2Int(-1, -1);

        Vector2Int target = new Vector2Int(-1, -1);
        int[] cdx = { 0, 0, 1, -1 };
        int[] cdy = { 1, -1, 0, 0 };

        while (queue.Count > 0)
        {
            Vector2Int cur = queue.Dequeue();
            if (reachable[cur.x, cur.y] && _grid[cur.x, cur.y] == CellType.HallwayFloor)
            {
                target = cur;
                break;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cur.x + cdx[d];
                int ny = cur.y + cdy[d];

                if (nx <= 1 || nx >= _width - 2 || ny <= 1 || ny >= _height - 2) continue;
                if (visited[nx, ny]) continue;
                if (_grid[nx, ny] == CellType.RoomFloor || _grid[nx, ny] == CellType.ObjectiveRoomFloor) continue;
                if (IntersectsAnyStairwellBay(nx, nx, ny, ny)) continue;

                visited[nx, ny] = true;
                parent[nx, ny] = cur;
                queue.Enqueue(new Vector2Int(nx, ny));
            }
        }

        if (target.x < 0) return;

        Vector2Int p = target;
        while (p.x != -1 && p.y != -1)
        {
            int hx = Mathf.Clamp(p.x - (hw - 1) / 2, 2, _width - hw - 2);
            int hy = Mathf.Clamp(p.y - (hw - 1) / 2, 2, _height - hw - 2);
            for (int ox = 0; ox < hw; ox++)
            {
                for (int oy = 0; oy < hw; oy++)
                {
                    int cx = hx + ox;
                    int cy = hy + oy;
                    if (!IntersectsAnyStairwellBay(cx, cx, cy, cy) &&
                        _grid[cx, cy] != CellType.RoomFloor &&
                        _grid[cx, cy] != CellType.ObjectiveRoomFloor &&
                        _grid[cx, cy] != CellType.Doorway)
                    {
                        if (CanWidenHallwayIntoWall(cx, cy) || _grid[cx, cy] == CellType.HallwayFloor)
                        {
                            _grid[cx, cy] = CellType.HallwayFloor;
                        }
                    }
                }
            }
            p = parent[p.x, p.y];
        }
    }

    /// <summary>
    /// BFS from the dynamic Objective Room (or the largest room on this floor if no objective room
    /// has been placed yet). Seeds connectivity verification for all 4 perimeter entrances.
    /// </summary>
    private bool[,] ComputeReachableFloor()
    {
        bool[,] visited = new bool[_width, _height];
        var queue = new Queue<Vector2Int>();

        // Seed from the objective room center; fall back to the CentralRoomBounds if needed
        RectInt seedBounds = (_objectiveRoomBounds.width > 0)
            ? _objectiveRoomBounds
            : _currentFloor.CentralRoomBounds;

        // If still zero (floor has no objective room), seed from the first room we can find
        if (seedBounds.width <= 0 && _rooms.Count > 0)
        {
            seedBounds = _rooms[0].Bounds;
        }

        int startX = Mathf.Clamp(seedBounds.xMin + seedBounds.width / 2, 1, _width - 2);
        int startY = Mathf.Clamp(seedBounds.yMin + seedBounds.height / 2, 1, _height - 2);

        // Walk inward until we land on a walkable tile
        bool found = false;
        for (int r = 0; r <= Mathf.Max(_width, _height) / 2 && !found; r++)
        {
            for (int dx2 = -r; dx2 <= r && !found; dx2++)
            {
                for (int dy2 = -r; dy2 <= r && !found; dy2++)
                {
                    int tx = startX + dx2;
                    int ty = startY + dy2;
                    if (tx < 0 || tx >= _width || ty < 0 || ty >= _height) continue;
                    if (IsWalkableFloorOrDoorway(tx, ty))
                    {
                        startX = tx;
                        startY = ty;
                        found = true;
                    }
                }
            }
        }

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

            var termComp = _terminalObject.GetComponent<ComputerTerminalObjective>();
            if (termComp != null)
            {
                termComp.SetFloorLevelServer(_objectiveFloorLevel);
            }

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

        EntranceData teamAEntrance = GetEntranceForSpawnPos(_teamASpawnPos);
        EntranceData teamBEntrance = GetEntranceForSpawnPos(_teamBSpawnPos);
        EntranceData teamCEntrance = GetEntranceForSpawnPos(_teamCSpawnPos);

        var zoneA = GameObject.Find("ExtractionZone_TeamA");
        if (zoneA != null)
        {
            zoneA.transform.position = new Vector3(_teamASpawnPos.x, _teamASpawnPos.y, 0f);
        }

        var player = GameObject.Find("Player");
        if (player != null)
        {
            TeleportCharacter(player, _teamASpawnPos);
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
            Vector2 friendlySpawn = _teamASpawnPos + friendlyOffset;
            TeleportCharacter(friendly, friendlySpawn);

            var dc = friendly.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(friendlySpawn, -patrolAxis, patrolAxis);
        }

        var zoneB = GameObject.Find("ExtractionZone_TeamB");
        if (zoneB != null)
        {
            zoneB.transform.position = new Vector3(_teamBSpawnPos.x, _teamBSpawnPos.y, 0f);
        }

        var zoneC = GameObject.Find("ExtractionZone_TeamC");
        if (zoneC != null)
        {
            zoneC.transform.position = new Vector3(_teamCSpawnPos.x, _teamCSpawnPos.y, 0f);
        }

        string cardA = TeamCardinalDirections.Length > 0 ? TeamCardinalDirections[0] : "NORTH";
        string cardB = TeamCardinalDirections.Length > 1 ? TeamCardinalDirections[1] : "SOUTH";
        string cardC = TeamCardinalDirections.Length > 2 ? TeamCardinalDirections[2] : "WEST";

        float timeA = TeamSprintTravelTimes.Length > 0 ? TeamSprintTravelTimes[0] : 0f;
        float timeB = TeamSprintTravelTimes.Length > 1 ? TeamSprintTravelTimes[1] : 0f;
        float timeC = TeamSprintTravelTimes.Length > 2 ? TeamSprintTravelTimes[2] : 0f;

        string labelA = timeA > 0f ? $"TEAM A (BLUE, {cardA}) - {timeA:F1}s" : "TEAM A (BLUE)";
        string labelB = timeB > 0f ? $"TEAM B (RED, {cardB}) - {timeB:F1}s" : "TEAM B (RED)";
        string labelC = timeC > 0f ? $"TEAM C (YELLOW, {cardC}) - {timeC:F1}s" : "TEAM C (YELLOW)";

        // Spawn high-visibility bright blue, red, and yellow circles for each team spawn in the Scene tab
        SpawnOrUpdateTeamSpawnMarker("TeamSpawnMarker_Blue", _teamASpawnPos, new Color(0.0f, 0.75f, 1.0f, 1.0f), labelA);
        SpawnOrUpdateTeamSpawnMarker("TeamSpawnMarker_Red", _teamBSpawnPos, new Color(1.0f, 0.2f, 0.2f, 1.0f), labelB);
        SpawnOrUpdateTeamSpawnMarker("TeamSpawnMarker_Yellow", _teamCSpawnPos, new Color(1.0f, 0.95f, 0.05f, 1.0f), labelC);

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

        // Derive a preferred position near the objective/largest room rather than a fixed center
        int hw = GetConfiguredHallwayWidth();
        RectInt hub = state.CentralRoomBounds.width > 0
            ? state.CentralRoomBounds
            : new RectInt(_width / 2 - 4, _height / 2 - 4, 8, 8);
        int preferredX = hub.xMin + hub.width / 2;
        int preferredY = Mathf.Clamp(hub.yMax + 1 + hw / 2, 4, _height - 4);

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
                    if (Application.isPlaying)
                        Destroy(col.gameObject);
                    else
                        DestroyImmediate(col.gameObject);
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

        // 4. Doorway swing clearance & cover pillar obstruction verification
        // Ensure no door leaf swing path is obstructed by a cover pillar or wall stub
        for (int i = 0; i < _spawnedDoors.Count; i++)
        {
            var door = _spawnedDoors[i];
            if (door == null) continue;

            Vector2 hingePos = door.HingeWorldPos;
            float checkRadius = door.DoorLength + 0.35f;
            Collider2D[] doorHits = Physics2D.OverlapCircleAll(hingePos, checkRadius);
            for (int h = 0; h < doorHits.Length; h++)
            {
                var hit = doorHits[h];
                if (hit == null || hit.isTrigger) continue;

                if (hit.GetComponent<SwingDoor>() != null || hit.GetComponentInParent<SwingDoor>() != null) continue;
                if (hit.GetComponent<PlayerController>() != null || hit.GetComponent<DummyController>() != null) continue;

                // If it's a CoverPillar, it obstructs the door swing/approach! Immediately disable and destroy it!
                if (hit.gameObject.name == "CoverPillar")
                {
                    hit.gameObject.SetActive(false);
                    if (Application.isPlaying)
                        Destroy(hit.gameObject);
                    else
                        DestroyImmediate(hit.gameObject);
                }
            }
        }

        Physics2D.SyncTransforms();
        bool allDoorsClear = true;
        for (int i = 0; i < _spawnedDoors.Count; i++)
        {
            var door = _spawnedDoors[i];
            if (door == null) continue;

            Vector2 hingePos = door.HingeWorldPos;
            float checkRadius = door.DoorLength + 0.35f;
            Collider2D[] remainingHits = Physics2D.OverlapCircleAll(hingePos, checkRadius);
            for (int h = 0; h < remainingHits.Length; h++)
            {
                var hit = remainingHits[h];
                if (hit == null || hit.isTrigger || !hit.gameObject.activeInHierarchy) continue;
                if (hit.GetComponent<SwingDoor>() != null || hit.GetComponentInParent<SwingDoor>() != null) continue;
                if (hit.GetComponent<PlayerController>() != null || hit.GetComponent<DummyController>() != null) continue;

                if (hit.gameObject.name == "CoverPillar")
                {
                    allDoorsClear = false;
                    break;
                }
            }
            if (!allDoorsClear) break;
        }
        AllDoorsClearOfPillarsVerified = allDoorsClear;
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

        GUILayout.BeginArea(new Rect(10f, 48f, 620f, 108f));
        GUILayout.BeginVertical("box");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Seed: {MapSeed} | Map: {_width}×{_height} | Floors ({_activeFloors.Count}): {floorsDesc}");
        if (GUILayout.Button("New Map Seed", GUILayout.Width(110f), GUILayout.Height(22f)))
        {
            RequestNewRandomMap();
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(
            $"Current: {GetFloorDisplayName(CurrentLocalFloorLevel)}{stairStatus} | " +
            $"Objective: {GetFloorDisplayName(_objectiveFloorLevel)} | " +
            $"Rooms: {TotalGeneratedRoomCount} | Staircases: {_spawnedStairwells.Count}");

        if (TeamSprintTravelTimes != null && TeamSprintTravelTimes.Length >= 2)
        {
            string deltaStatus = AllTeamsEquidistantVerified ? "EQUIDISTANT" : "BALANCED";
            string teamSpawnsInfo = $"Spawns [{deltaStatus} Δ {MaxTeamTravelTimeDelta:F2}s]: ";
            for (int i = 0; i < TeamSprintTravelTimes.Length; i++)
            {
                char tName = (char)('A' + i);
                teamSpawnsInfo += $"{tName}({TeamSprintTravelTimes[i]:F1}s, {TeamCardinalDirections[i]}) ";
            }
            GUILayout.Label(teamSpawnsInfo);
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private static Sprite _cachedMarkerCircleSprite;

    private static void EnsureMarkerCircleSprite()
    {
        if (_cachedMarkerCircleSprite != null) return;

        const int size = 128;
        float center = (size - 1) * 0.5f;
        float radius = center;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / radius;
                if (dist > 1.0f)
                {
                    pixels[y * size + x] = Color.clear;
                }
                else if (dist > 0.84f)
                {
                    float edgeAlpha = Mathf.Clamp01((1.0f - dist) / 0.05f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, edgeAlpha * 0.95f);
                }
                else if (dist > 0.76f)
                {
                    pixels[y * size + x] = new Color(1f, 1f, 1f, 0.25f);
                }
                else if (dist > 0.68f)
                {
                    pixels[y * size + x] = new Color(1f, 1f, 1f, 0.75f);
                }
                else if (dist < 0.20f)
                {
                    float coreAlpha = Mathf.Clamp01((0.20f - dist) / 0.04f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Max(0.5f, coreAlpha));
                }
                else
                {
                    pixels[y * size + x] = new Color(1f, 1f, 1f, 0.35f);
                }
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();

        _cachedMarkerCircleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 128f);
    }

    private void SpawnOrUpdateTeamSpawnMarker(string name, Vector2 pos, Color color, string teamLabel)
    {
        EnsureMarkerCircleSprite();

        Transform existing = _mapRoot != null ? _mapRoot.transform.Find(name) : null;
        GameObject markerGo;
        if (existing != null)
        {
            markerGo = existing.gameObject;
        }
        else
        {
            markerGo = new GameObject(name);
            if (_mapRoot != null) markerGo.transform.SetParent(_mapRoot.transform, false);
        }

        markerGo.transform.position = new Vector3(pos.x, pos.y, 0f);
        markerGo.transform.localScale = new Vector3(5.0f, 5.0f, 1f);

        var sr = markerGo.GetComponent<SpriteRenderer>();
        if (sr == null) sr = markerGo.AddComponent<SpriteRenderer>();
        sr.sprite = _cachedMarkerCircleSprite;
        sr.color = color;
        sr.sortingOrder = 12;

        Transform textChild = markerGo.transform.Find("Label");
        TextMesh tm;
        if (textChild == null)
        {
            var textGo = new GameObject("Label");
            textGo.transform.SetParent(markerGo.transform, false);
            textGo.transform.localPosition = new Vector3(0f, 0.65f, 0f);
            textGo.transform.localScale = new Vector3(0.12f, 0.12f, 1f);
            tm = textGo.AddComponent<TextMesh>();
        }
        else
        {
            tm = textChild.GetComponent<TextMesh>();
        }

        if (tm != null)
        {
            tm.text = teamLabel;
            tm.fontSize = 28;
            tm.alignment = TextAlignment.Center;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = color;
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 13;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Vector2 spawnA = _teamASpawnPos;
        Vector2 spawnB = _teamBSpawnPos;
        Vector2 spawnC = _teamCSpawnPos;

        if (spawnA == Vector2.zero)
        {
            var za = GameObject.Find("ExtractionZone_TeamA");
            if (za != null) spawnA = za.transform.position;
        }
        if (spawnB == Vector2.zero)
        {
            var zb = GameObject.Find("ExtractionZone_TeamB");
            if (zb != null) spawnB = zb.transform.position;
        }
        if (spawnC == Vector2.zero)
        {
            var zc = GameObject.Find("ExtractionZone_TeamC");
            if (zc != null) spawnC = zc.transform.position;
        }

        string cardA = TeamCardinalDirections.Length > 0 ? TeamCardinalDirections[0] : "";
        string cardB = TeamCardinalDirections.Length > 1 ? TeamCardinalDirections[1] : "";
        string cardC = TeamCardinalDirections.Length > 2 ? TeamCardinalDirections[2] : "";

        float timeA = TeamSprintTravelTimes.Length > 0 ? TeamSprintTravelTimes[0] : 0f;
        float timeB = TeamSprintTravelTimes.Length > 1 ? TeamSprintTravelTimes[1] : 0f;
        float timeC = TeamSprintTravelTimes.Length > 2 ? TeamSprintTravelTimes[2] : 0f;

        string lblA = timeA > 0f ? $"TEAM A (BLUE, {cardA}) - {timeA:F1}s sprint" : "TEAM A SPAWN (BLUE)";
        string lblB = timeB > 0f ? $"TEAM B (RED, {cardB}) - {timeB:F1}s sprint" : "TEAM B SPAWN (RED)";
        string lblC = timeC > 0f ? $"TEAM C (YELLOW, {cardC}) - {timeC:F1}s sprint" : "TEAM C SPAWN (YELLOW)";

        DrawTeamSpawnGizmo(spawnA, new Color(0.0f, 0.75f, 1.0f, 0.35f), new Color(0.0f, 0.85f, 1.0f, 1.0f), lblA);
        DrawTeamSpawnGizmo(spawnB, new Color(1.0f, 0.2f, 0.2f, 0.35f), new Color(1.0f, 0.25f, 0.25f, 1.0f), lblB);
        DrawTeamSpawnGizmo(spawnC, new Color(1.0f, 0.95f, 0.05f, 0.35f), new Color(1.0f, 1.0f, 0.1f, 1.0f), lblC);
    }

    private void DrawTeamSpawnGizmo(Vector2 center, Color fillColor, Color outlineColor, string label)
    {
        if (center == Vector2.zero) return;

        UnityEditor.Handles.color = fillColor;
        UnityEditor.Handles.DrawSolidDisc(center, Vector3.forward, 2.5f);

        UnityEditor.Handles.color = outlineColor;
        UnityEditor.Handles.DrawWireDisc(center, Vector3.forward, 2.5f);
        UnityEditor.Handles.DrawWireDisc(center, Vector3.forward, 2.55f);
        UnityEditor.Handles.DrawWireDisc(center, Vector3.forward, 1.25f);

        Gizmos.color = outlineColor;
        Gizmos.DrawSphere(center, 0.45f);

        var style = new GUIStyle();
        style.normal.textColor = outlineColor;
        style.fontSize = 14;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.MiddleCenter;
        UnityEditor.Handles.Label(center + new Vector2(0f, 3.2f), label, style);
    }
#endif
}
