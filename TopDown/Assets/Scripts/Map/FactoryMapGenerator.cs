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
/// rooms ranging in size from small closets to massive auditoriums with 1–4 entrances/exits,
/// breakable glass <see cref="BreakableWindow"/>s, walk-on <see cref="StairwellZone"/> transitions,
/// physics-pushed <see cref="SwingDoor"/>s, and steady/flickering <see cref="HallwayLight"/>s.
/// </summary>
public class FactoryMapGenerator : NetworkBehaviour
{
    // ────────────────────────────── Constants ──────────────────────────────

    private const float FloorWorldStride = 320f;

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
        public Vector2Int GridCell; // Primary cell of the opening
        public Vector2 WorldPosition;
        public Vector2 CourtyardSpawnPosition;
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
    private readonly List<RoomData> _allRooms = new List<RoomData>();
    private readonly List<SwingDoor> _spawnedDoors = new List<SwingDoor>();
    private readonly List<BreakableWindow> _spawnedWindows = new List<BreakableWindow>();
    private readonly List<StairwellZone> _spawnedStairwells = new List<StairwellZone>();

    private EntranceData[] _entrances = new EntranceData[4];
    private int _objectiveFloorLevel;
    private RectInt _objectiveRoomBounds;

    // Active per-floor working references during generation
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

    /// <summary>Number of breakable windows spawned across the factory.</summary>
    public int SpawnedWindowCount => _spawnedWindows.Count;

    /// <summary>Number of stairwell transition pads spawned across the factory.</summary>
    public int SpawnedStairwellCount => _spawnedStairwells.Count;

    /// <summary>True when all 4 perimeter entrances passed both grid BFS reachability and physics corridor clearance checks.</summary>
    public bool AllEntrancesVerifiedClear { get; private set; }

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

    /// <summary>Returns the world-space center offset for the given floor level.</summary>
    public static Vector2 GetFloorWorldOffset(int floorLevel)
    {
        return new Vector2(floorLevel * FloorWorldStride, 0f);
    }

    /// <summary>Determines which floor level a world position belongs to based on horizontal stride.</summary>
    public static int GetFloorLevelFromWorldPos(Vector2 worldPos)
    {
        return Mathf.RoundToInt(worldPos.x / FloorWorldStride);
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

    // ──────────────────────────── Floor Navigation & Stairwells ────────────

    /// <summary>
    /// Notifies the generator that the local player transitioned to <paramref name="targetFloorLevel"/> via a stairwell.
    /// </summary>
    public void OnLocalPlayerChangedFloor(int targetFloorLevel)
    {
        CurrentLocalFloorLevel = targetFloorLevel;
    }

    /// <summary>
    /// Resolves the immediate navigation target on the same floor as <paramref name="fromWorldPos"/>.
    /// If <paramref name="toWorldPos"/> is on another floor, returns the nearest <see cref="StairwellZone"/>
    /// on the player's current floor that leads toward the destination floor.
    /// </summary>
    public Vector2 ResolveNavigationTarget(Vector2 fromWorldPos, Vector2 toWorldPos)
    {
        int fromFloor = GetFloorLevelFromWorldPos(fromWorldPos);
        int toFloor = GetFloorLevelFromWorldPos(toWorldPos);
        if (fromFloor == toFloor)
        {
            return toWorldPos;
        }

        int desiredNextFloor = toFloor > fromFloor ? fromFloor + 1 : fromFloor - 1;
        StairwellZone bestStair = null;
        float bestDistSq = float.MaxValue;

        for (int i = 0; i < _spawnedStairwells.Count; i++)
        {
            StairwellZone stair = _spawnedStairwells[i];
            if (stair == null) continue;
            if (stair.SourceFloorLevel == fromFloor && stair.TargetFloorLevel == desiredNextFloor)
            {
                float dSq = ((Vector2)stair.transform.position - fromWorldPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestStair = stair;
                }
            }
        }

        return bestStair != null ? (Vector2)bestStair.transform.position : toWorldPos;
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
        _allRooms.Clear();
        _spawnedDoors.Clear();
        _spawnedWindows.Clear();
        _spawnedStairwells.Clear();
        AllEntrancesVerifiedClear = false;
        CurrentLocalFloorLevel = 0;

        // Clear previous generated map container (deactivate immediately so old colliders leave Physics2D this frame)
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

        // Configure ambient darkness and resize 1st-floor FloorGrid to cover building + courtyard
        ApplyAmbientLightingAndFloor();

        // Step 1: Roll the 1-to-3 active floors (strictly enforcing: Basement (-1) and 3rd Floor (2) never coexist)
        DetermineActiveFloors(rng);

        // Step 2: Generate each active floor's grid, rooms (Closets to Auditoriums), doors, windows, cover, and lights
        int minDoorsAcrossMap = int.MaxValue;
        int maxDoorsAcrossMap = 0;

        for (int i = 0; i < _activeFloors.Count; i++)
        {
            int floorLevel = _activeFloors[i];
            GenerateSingleFloor(floorLevel, rng, ref minDoorsAcrossMap, ref maxDoorsAcrossMap);
        }

        MinDoorwaysPerRoom = _allRooms.Count > 0 ? minDoorsAcrossMap : 0;
        MaxDoorwaysPerRoom = maxDoorsAcrossMap;

        // Step 3: Spawn linked walk-on StairwellZones between all adjacent active floors
        SpawnStairwellsBetweenActiveFloors();

        // Step 4: Position the ComputerTerminal in the Objective Room & assign Teams/Dummies on 1st Floor
        PlaceObjectiveAndTeams(rng);

        // Step 5: Final physics verification ensuring zero solid objects or characters block any of the 4 entrances
        ValidateEntranceClearancePhysics();
    }

    /// <summary>
    /// Selects 1 to 3 active floors from the valid combinations:
    /// 1 floor: [0]
    /// 2 floors: [-1, 0] or [0, 1]
    /// 3 floors: [-1, 0, 1] or [0, 1, 2]
    /// Strictly enforces that Basement (-1) and 3rd Floor (2) are mutually exclusive.
    /// </summary>
    private void DetermineActiveFloors(System.Random rng)
    {
        // 5 valid floor configurations:
        // 0: [0]          (1st Floor only)
        // 1: [-1, 0]      (Basement + 1st Floor)
        // 2: [0, 1]       (1st Floor + 2nd Floor)
        // 3: [-1, 0, 1]   (Basement + 1st Floor + 2nd Floor)
        // 4: [0, 1, 2]    (1st Floor + 2nd Floor + 3rd Floor)
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

        // Safety invariant: Basement (-1) and 3rd Floor (2) can never coexist
        if (_activeFloors.Contains(-1) && _activeFloors.Contains(2))
        {
            _activeFloors.Remove(2);
        }

        // Pick which active floor houses the 1 Objective Room (ComputerTerminal)
        _objectiveFloorLevel = _activeFloors[rng.Next(_activeFloors.Count)];
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

        // If this is a non-ground floor, create a dark concrete floor backdrop beneath its footprint
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

        // 2. Carve Central Room (Objective Room on _objectiveFloorLevel, or Central Atrium Hub on other floors)
        bool isObjectiveFloor = (floorLevel == _objectiveFloorLevel);
        PlaceCentralRoom(rng, isObjectiveFloor);

        // 3. Carve the Inner Ring Hallway around the Central Room & reserve Stairwell Bays
        RectInt ringOuter = CarveInnerRingHallway();
        state.RingOuterBounds = ringOuter;
        ReserveStairwellBaysOnRing(ringOuter);

        // 4. On 1st Floor (floorLevel == 0), place and connect the 4 exterior perimeter entrances.
        //    On Basement/2nd/3rd floors, carve internal arterial corridors radiating into the 4 quadrants.
        if (floorLevel == 0)
        {
            PlaceAndConnectFourEntrances(rng, ringOuter);
        }
        else
        {
            CarveNonGroundFloorArterialHallways(rng, ringOuter);
        }

        // 5. Carve 15 rooms across the 4 size archetypes (Auditoriums -> LargeWorkshops -> StandardRooms -> Closets)
        CarveInteriorRoomsByArchetype(rng);

        // 6. Connect rooms with secondary hallways and doorways according to each room's TargetDoorways (1–4)
        ConnectRoomsWithSecondaryHallwaysAndDoors(rng, ringOuter);

        // 7. Place breakable glass windows in room walls bordering hallways or adjacent rooms
        PlaceRoomWindows(rng);

        // 8. Place interior concrete cover pillars scaled to each room's archetype
        PlaceInteriorCoverPillars(rng);

        // 9. Safety pass — enforce 100% entrance, stairwell bay, doorway swing arc, and BFS path clearance
        EnforceFloorSafetyClearance(floorLevel, ringOuter);

        // 10. Instantiate greedy-merged concrete wall colliders and cover pillars for this floor
        BuildWallAndCoverGeometry();

        // 11. Spawn steady & flickering emergency lights along hallways ONLY (rooms stay dark!)
        SpawnHallwayEmergencyLights(floorLevel, rng);

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

    private void CreateNonGroundFloorBackdrop(int floorLevel, Transform parent)
    {
        EnsureTileSprite();
        Vector2 centerWorld = GridToWorld((_width - 1) * 0.5f, (_height - 1) * 0.5f, floorLevel);

        // Outer dark shroud so non-ground floors have no exterior courtyard grass/concrete
        var shroudGo = new GameObject("NonGroundVoidShroud");
        shroudGo.transform.SetParent(parent, false);
        shroudGo.transform.position = new Vector3(centerWorld.x, centerWorld.y, 0f);
        shroudGo.transform.localScale = new Vector3(_width + 90f, _height + 90f, 1f);
        var shroudSr = shroudGo.AddComponent<SpriteRenderer>();
        shroudSr.sprite = _cachedTileSprite;
        shroudSr.color = new Color(0.05f, 0.055f, 0.06f, 1f);
        shroudSr.sortingOrder = -12;

        // Interior concrete floor slab matching the 108x108 building footprint
        var slabGo = new GameObject("FloorConcreteSlab");
        slabGo.transform.SetParent(parent, false);
        slabGo.transform.position = new Vector3(centerWorld.x, centerWorld.y, 0f);
        slabGo.transform.localScale = new Vector3(_width, _height, 1f);
        var slabSr = slabGo.AddComponent<SpriteRenderer>();
        slabSr.sprite = _cachedTileSprite;
        slabSr.color = floorLevel < 0
            ? new Color(0.18f, 0.19f, 0.21f, 1f) // Slightly damp dark slate for Basement
            : new Color(0.22f, 0.23f, 0.24f, 1f); // Upper floor concrete
        slabSr.sortingOrder = -10;
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
        // Keep central room centered so stairwell bays on the ring hallway align across all floors
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

    private int GetDoorwaySpan()
    {
        int hw = _config != null ? _config.HallwayWidth : 6;
        return hw >= 4 ? 4 : 2;
    }

    private RectInt CarveInnerRingHallway()
    {
        int hw = _config != null ? _config.HallwayWidth : 6;
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

        CarveHorizontalDoorway(midX, central.yMax, doorSpan, true);
        CarveHorizontalDoorway(midX, central.yMin - 1, doorSpan, true);
        CarveVerticalDoorway(central.xMax, midY, doorSpan, true);
        CarveVerticalDoorway(central.xMin - 1, midY, doorSpan, true);

        return new RectInt(ringMinX, ringMinY, ringMaxX - ringMinX, ringMaxY - ringMinY);
    }

    /// <summary>
    /// Reserves protected zones around the stairwell pad positions in the inner ring hallway
    /// so no rooms, walls, or cover pillars ever obstruct stairwell transitions.
    /// </summary>
    private void ReserveStairwellBaysOnRing(RectInt ringOuter)
    {
        for (int lowerFloor = -1; lowerFloor <= 1; lowerFloor++)
        {
            GetStairwellGridPositionsForShaft(lowerFloor, ringOuter, out Vector2Int bayA, out Vector2Int bayB);
            MarkDoorwayClearanceZone(bayA.x - 3, bayA.y - 3, 7, 7);
            MarkDoorwayClearanceZone(bayB.x - 3, bayB.y - 3, 7, 7);
        }
    }

    private void GetStairwellGridPositionsForShaft(
        int lowerFloorLevel,
        RectInt ringOuter,
        out Vector2Int primaryBay,
        out Vector2Int secondaryBay)
    {
        int hw = _config != null ? _config.HallwayWidth : 6;
        int halfHw = hw / 2;

        // Each vertical transition (-1<->0, 0<->1, 1<->2) uses distinct corners/sides of the ring hallway
        // so UP and DOWN stairwells on the same floor never overlap and always sit on walkable HallwayFloor.
        if (lowerFloorLevel == -1) // Basement <-> 1st Floor: NW and SE corners of ring hallway
        {
            primaryBay = new Vector2Int(ringOuter.xMin + halfHw, ringOuter.yMax - halfHw - 1);
            secondaryBay = new Vector2Int(ringOuter.xMax - halfHw - 1, ringOuter.yMin + halfHw);
        }
        else if (lowerFloorLevel == 0) // 1st Floor <-> 2nd Floor: NE and SW corners of ring hallway
        {
            primaryBay = new Vector2Int(ringOuter.xMax - halfHw - 1, ringOuter.yMax - halfHw - 1);
            secondaryBay = new Vector2Int(ringOuter.xMin + halfHw, ringOuter.yMin + halfHw);
        }
        else // 2nd Floor <-> 3rd Floor: North-West-Mid and South-East-Mid of ring hallway
        {
            primaryBay = new Vector2Int(ringOuter.xMin + halfHw, ringOuter.yMin + ringOuter.height / 2 + 5);
            secondaryBay = new Vector2Int(ringOuter.xMax - halfHw - 1, ringOuter.yMin + ringOuter.height / 2 - 5);
        }
    }

    private void SpawnStairwellsBetweenActiveFloors()
    {
        if (_activeFloors.Count <= 1) return;

        for (int i = 0; i < _activeFloors.Count - 1; i++)
        {
            int lowerFloor = _activeFloors[i];
            int upperFloor = _activeFloors[i + 1];

            if (!_floorStates.TryGetValue(lowerFloor, out var lowerState) ||
                !_floorStates.TryGetValue(upperFloor, out var upperState))
            {
                continue;
            }

            GetStairwellGridPositionsForShaft(
                lowerFloor,
                lowerState.RingOuterBounds,
                out Vector2Int bayA,
                out Vector2Int bayB);

            SpawnLinkedStairwellPair(lowerFloor, upperFloor, bayA, lowerState.FloorRoot.transform, upperState.FloorRoot.transform);
            SpawnLinkedStairwellPair(lowerFloor, upperFloor, bayB, lowerState.FloorRoot.transform, upperState.FloorRoot.transform);
        }
    }

    private void SpawnLinkedStairwellPair(
        int lowerFloor,
        int upperFloor,
        Vector2Int gridPos,
        Transform lowerParent,
        Transform upperParent)
    {
        Vector2 lowerWorld = GridToWorld(gridPos.x, gridPos.y, lowerFloor);
        Vector2 upperWorld = GridToWorld(gridPos.x, gridPos.y, upperFloor);

        // 1. UP Stairwell on lowerFloor -> teleports to upperFloor
        var upGo = new GameObject($"Stairwell_Up_{lowerFloor}_to_{upperFloor}");
        upGo.transform.SetParent(lowerParent, false);
        var upZone = upGo.AddComponent<StairwellZone>();
        upZone.Initialize(
            this,
            lowerFloor,
            upperFloor,
            lowerWorld,
            upperWorld + new Vector2(0f, -2.1f),
            $"STAIRS UP -> {GetFloorDisplayName(upperFloor)}");
        _spawnedStairwells.Add(upZone);

        // 2. DOWN Stairwell on upperFloor -> teleports to lowerFloor
        var downGo = new GameObject($"Stairwell_Down_{upperFloor}_to_{lowerFloor}");
        downGo.transform.SetParent(upperParent, false);
        var downZone = downGo.AddComponent<StairwellZone>();
        downZone.Initialize(
            this,
            upperFloor,
            lowerFloor,
            upperWorld,
            lowerWorld + new Vector2(0f, 2.1f),
            $"STAIRS DOWN -> {GetFloorDisplayName(lowerFloor)}");
        _spawnedStairwells.Add(downZone);
    }

    private void PlaceAndConnectFourEntrances(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = _config != null ? _config.HallwayWidth : 6;
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
            WorldPosition = GridToWorld(northX + halfHwOffset, _height - 1, 0),
            CourtyardSpawnPosition = GridToWorld(northX + halfHwOffset, _height - 1 + courtyardDist, 0)
        };
        CarveHorizontalDoorway(northX + doorOffset, _height - 1, doorSpan, true);
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
            WorldPosition = GridToWorld(_width - 1, eastY + halfHwOffset, 0),
            CourtyardSpawnPosition = GridToWorld(_width - 1 + courtyardDist, eastY + halfHwOffset, 0)
        };
        CarveVerticalDoorway(_width - 1, eastY + doorOffset, doorSpan, true);
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
            WorldPosition = GridToWorld(southX + halfHwOffset, 0, 0),
            CourtyardSpawnPosition = GridToWorld(southX + halfHwOffset, -courtyardDist, 0)
        };
        CarveHorizontalDoorway(southX + doorOffset, 0, doorSpan, true);
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
            WorldPosition = GridToWorld(0, westY + halfHwOffset, 0),
            CourtyardSpawnPosition = GridToWorld(-courtyardDist, westY + halfHwOffset, 0)
        };
        CarveVerticalDoorway(0, westY + doorOffset, doorSpan, true);
        ReserveEntranceVestibule(1, westY, vestibuleDepth, hw, 2);
        CarveLCorridor(new Vector2Int(vestibuleDepth, westY), new Vector2Int(
            ringOuter.xMin,
            Mathf.Clamp(westY, ringOuter.yMin, ringOuter.yMax - hw)), hw, false);
    }

    /// <summary>
    /// On non-ground floors (Basement, 2nd, 3rd), carves 4 internal arterial corridors radiating from
    /// the central ring hallway into the 4 sectors (stopping inside the perimeter walls).
    /// </summary>
    private void CarveNonGroundFloorArterialHallways(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 18;
        int hw = _config != null ? _config.HallwayWidth : 6;

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

    /// <summary>
    /// Carves rooms in largest-to-smallest order (Auditoriums -> Large Workshops -> Standard Rooms -> Closets)
    /// so every floor features vastly different room sizes from 5x5 closets up to 32x26 auditoriums.
    /// </summary>
    private void CarveInteriorRoomsByArchetype(System.Random rng)
    {
        int targetRooms = _config != null ? _config.TargetRoomCount : 15;
        float doorChance = _config != null ? _config.DoorSpawnChance : 0.9f;

        // Build an ordered queue of desired room archetypes for this floor (largest first for optimal packing)
        var desiredArchetypes = new List<RoomArchetype>();
        int remaining = Mathf.Max(4, targetRooms - 1); // Room 0 is already the Central Room

        int auditoriumCount = Mathf.Clamp(remaining / 7, 1, 2);
        int workshopCount = Mathf.Clamp(remaining / 4, 2, 4);
        int closetCount = Mathf.Clamp(remaining / 4, 2, 4);
        int standardCount = Mathf.Max(1, remaining - auditoriumCount - workshopCount - closetCount);

        for (int i = 0; i < auditoriumCount; i++) desiredArchetypes.Add(RoomArchetype.Auditorium);
        for (int i = 0; i < workshopCount; i++) desiredArchetypes.Add(RoomArchetype.LargeWorkshop);
        for (int i = 0; i < standardCount; i++) desiredArchetypes.Add(RoomArchetype.StandardRoom);
        for (int i = 0; i < closetCount; i++) desiredArchetypes.Add(RoomArchetype.Closet);

        for (int idx = 0; idx < desiredArchetypes.Count && _rooms.Count < targetRooms; idx++)
        {
            RoomArchetype archetype = desiredArchetypes[idx];
            if (!TryPlaceRoomOfArchetype(archetype, rng, doorChance))
            {
                // Fallback down one size tier if the floor is already tightly packed
                RoomArchetype fallback = archetype switch
                {
                    RoomArchetype.Auditorium => RoomArchetype.LargeWorkshop,
                    RoomArchetype.LargeWorkshop => RoomArchetype.StandardRoom,
                    _ => RoomArchetype.Closet
                };
                if (!TryPlaceRoomOfArchetype(fallback, rng, doorChance))
                {
                    TryPlaceRoomOfArchetype(RoomArchetype.Closet, rng, doorChance);
                }
            }
        }
    }

    private bool TryPlaceRoomOfArchetype(RoomArchetype archetype, System.Random rng, float doorChance)
    {
        int maxAttempts = archetype == RoomArchetype.Auditorium ? 450 : 350;
        int doorSpan = archetype == RoomArchetype.Closet ? 2 : GetDoorwaySpan();

        for (int a = 0; a < maxAttempts; a++)
        {
            GetDimensionsForArchetype(archetype, rng, a > maxAttempts / 2, out int rw, out int rh);
            int rx = rng.Next(2, _width - rw - 2);
            int ry = rng.Next(2, _height - rh - 2);

            var candidate = new RectInt(rx, ry, rw, rh);
            if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

            Vector2Int doorCell;
            bool horizontalDoor;
            bool connected =
                TryFindHallwayDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor) ||
                TryCarveBranchHallwayToRoom(candidate, doorSpan, out doorCell, out horizontalDoor) ||
                (a > 120 && TryFindAdjacentRoomDoorwaySpot(candidate, doorSpan, rng, out doorCell, out horizontalDoor));

            if (!connected) continue;

            // Carve room floor
            for (int x = rx; x < rx + rw; x++)
            {
                for (int y = ry; y < ry + rh; y++)
                {
                    _grid[x, y] = CellType.RoomFloor;
                }
            }

            // Carve initial doorway
            bool spawnSwingDoor = rng.NextDouble() <= doorChance;
            if (horizontalDoor)
            {
                CarveHorizontalDoorway(doorCell.x, doorCell.y, doorSpan, spawnSwingDoor);
            }
            else
            {
                CarveVerticalDoorway(doorCell.x, doorCell.y, doorSpan, spawnSwingDoor);
            }

            GetArchetypeDoorAndWindowTargets(archetype, rng, out int targetDoors, out int targetWindows);

            _rooms.Add(new RoomData
            {
                FloorLevel = _currentFloor.FloorLevel,
                Bounds = candidate,
                Archetype = archetype,
                IsObjectiveRoom = false,
                TargetDoorways = targetDoors,
                ActualDoorways = 1,
                TargetWindows = targetWindows
            });
            return true;
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
                targetDoors = 1; // Closets have a single entrance/exit
                targetWindows = 0; // Closets have no windows
                break;

            case RoomArchetype.StandardRoom:
                targetDoors = rng.Next(1, 4); // 1 to 3 entrances/exits
                targetWindows = rng.Next(1, 3); // 1 to 2 windows
                break;

            case RoomArchetype.LargeWorkshop:
                targetDoors = rng.Next(2, 5); // 2 to 4 entrances/exits
                targetWindows = rng.Next(2, 4); // 2 to 3 windows
                break;

            case RoomArchetype.Auditorium:
                targetDoors = rng.Next(3, 5); // 3 to 4 entrances/exits
                targetWindows = rng.Next(2, 5); // 2 to 4 windows
                break;

            default:
                targetDoors = 4;
                targetWindows = 3;
                break;
        }
    }

    /// <summary>
    /// Connects rooms to one another and to neighboring corridors via secondary hallways and doorways
    /// so rooms achieve their archetype's target entrance/exit count (1 for closets, up to 4 for auditoriums/workshops).
    /// </summary>
    private void ConnectRoomsWithSecondaryHallwaysAndDoors(System.Random rng, RectInt ringOuter)
    {
        float doorChance = _config != null ? _config.DoorSpawnChance : 0.9f;

        // Pass 1: Connect additional walls up to each room's TargetDoorways (1–4)
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

                TryConnectRoomWallOutward(bounds, side, rng, doorChance);
            }
        }

        // Pass 2: Ensure LargeWorkshops and Auditoriums always have at least 2–3 entrances
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
                ForceSecondHallwayEntranceForRoom(room.Bounds, ringOuter, rng, doorChance);
                safety++;
            }
        }
    }

    // ──────────────────────────── Breakable Windows ────────────────────────

    /// <summary>
    /// Places breakable glass <see cref="BreakableWindow"/> spans along room walls that border
    /// hallways or adjacent rooms, allowing sight and gunplay through windows while blocking movement.
    /// </summary>
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

        if (wallSide == 0) // North wall (y = room.yMax, outside is y = room.yMax + 1)
        {
            int wy = room.yMax;
            if (wy + 1 < _height - 1)
            {
                for (int x = room.xMin + 2; x <= room.xMax - 2 - span; x++)
                {
                    if (IsValidWindowSpan(x, wy, span, true, 0, 1))
                    {
                        candidates.Add((new Vector2Int(x, wy), true));
                    }
                }
            }
        }
        else if (wallSide == 2) // South wall (y = room.yMin - 1, outside is y = room.yMin - 2)
        {
            int wy = room.yMin - 1;
            if (wy - 1 >= 1)
            {
                for (int x = room.xMin + 2; x <= room.xMax - 2 - span; x++)
                {
                    if (IsValidWindowSpan(x, wy, span, true, 0, -1))
                    {
                        candidates.Add((new Vector2Int(x, wy), true));
                    }
                }
            }
        }
        else if (wallSide == 1) // East wall (x = room.xMax, outside is x = room.xMax + 1)
        {
            int wx = room.xMax;
            if (wx + 1 < _width - 1)
            {
                for (int y = room.yMin + 2; y <= room.yMax - 2 - span; y++)
                {
                    if (IsValidWindowSpan(wx, y, span, false, 1, 0))
                    {
                        candidates.Add((new Vector2Int(wx, y), false));
                    }
                }
            }
        }
        else if (wallSide == 3) // West wall (x = room.xMin - 1, outside is x = room.xMin - 2)
        {
            int wx = room.xMin - 1;
            if (wx - 1 >= 1)
            {
                for (int y = room.yMin + 2; y <= room.yMax - 2 - span; y++)
                {
                    if (IsValidWindowSpan(wx, y, span, false, -1, 0))
                    {
                        candidates.Add((new Vector2Int(wx, y), false));
                    }
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
        Vector2 worldCenter = GridToWorld(centerGx, centerGy, _currentFloor.FloorLevel);
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
        // Keep at least 2 tiles of solid concrete wall on either side of the window (away from doors/other windows)
        for (int s = -2; s < span + 2; s++)
        {
            int gx = horizontal ? startX + s : startX;
            int gy = horizontal ? startY : startY + s;
            if (gx <= 1 || gx >= _width - 2 || gy <= 1 || gy >= _height - 2) return false;

            if (_grid[gx, gy] != CellType.Wall) return false;
        }

        // Verify that the tiles immediately outside and inside the window span are open floor (Hallway or Room)
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

    private bool TryConnectRoomWallOutward(RectInt room, int wallSide, System.Random rng, float doorChance)
    {
        int hw = _config != null ? _config.HallwayWidth : 6;
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 2 : GetDoorwaySpan();
        int effHw = Mathf.Min(hw, Mathf.Min(room.width - 2, room.height - 2));
        if (effHw < doorSpan) return false;

        int doorOffset = Mathf.Max(0, (effHw - doorSpan) / 2);
        const int maxDist = 36;

        int midX = Mathf.Clamp(room.xMin + (room.width - effHw) / 2, room.xMin + 1, Mathf.Max(room.xMin + 1, room.xMax - effHw - 1));
        int midY = Mathf.Clamp(room.yMin + (room.height - effHw) / 2, room.yMin + 1, Mathf.Max(room.yMin + 1, room.yMax - effHw - 1));
        int doorX = midX + doorOffset;
        int doorY = midY + doorOffset;

        bool spawnDoor = rng.NextDouble() <= doorChance;

        if (wallSide == 0) // North (+Y from room.yMax)
        {
            if (doorX + doorSpan >= _width - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cy = room.yMax + dist;
                if (cy >= _height - 2) break;

                if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                {
                    for (int y = room.yMax + 1; y < cy; y++)
                        for (int w = 0; w < effHw; w++)
                            SetHallwayIfWall(midX + w, y);

                    CarveHorizontalDoorway(doorX, room.yMax, doorSpan, spawnDoor);
                    return true;
                }

                if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                {
                    if (dist == 1)
                    {
                        CarveHorizontalDoorway(doorX, room.yMax, doorSpan, spawnDoor);
                    }
                    else
                    {
                        for (int y = room.yMax + 1; y <= cy - 2; y++)
                            for (int w = 0; w < effHw; w++)
                                SetHallwayIfWall(midX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMax, doorSpan, spawnDoor);
                        CarveHorizontalDoorway(doorX, cy - 1, doorSpan, dist >= 4 && spawnDoor);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 2) // South (-Y from room.yMin - 1)
        {
            if (doorX + doorSpan >= _width - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cy = room.yMin - 1 - dist;
                if (cy <= 1) break;

                if (SpanMatchesCellType(doorX, cy, doorSpan, true, CellType.HallwayFloor))
                {
                    for (int y = cy + 1; y <= room.yMin - 2; y++)
                        for (int w = 0; w < effHw; w++)
                            SetHallwayIfWall(midX + w, y);

                    CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan, spawnDoor);
                    return true;
                }

                if (SpanMatchesRoomFloor(doorX, cy, doorSpan, true))
                {
                    if (dist == 1)
                    {
                        CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan, spawnDoor);
                    }
                    else
                    {
                        for (int y = cy + 2; y <= room.yMin - 2; y++)
                            for (int w = 0; w < effHw; w++)
                                SetHallwayIfWall(midX + w, y);

                        CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan, spawnDoor);
                        CarveHorizontalDoorway(doorX, cy + 1, doorSpan, dist >= 4 && spawnDoor);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 1) // East (+X from room.xMax)
        {
            if (doorY + doorSpan >= _height - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cx = room.xMax + dist;
                if (cx >= _width - 2) break;

                if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                {
                    for (int x = room.xMax + 1; x < cx; x++)
                        for (int w = 0; w < effHw; w++)
                            SetHallwayIfWall(x, midY + w);

                    CarveVerticalDoorway(room.xMax, doorY, doorSpan, spawnDoor);
                    return true;
                }

                if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                {
                    if (dist == 1)
                    {
                        CarveVerticalDoorway(room.xMax, doorY, doorSpan, spawnDoor);
                    }
                    else
                    {
                        for (int x = room.xMax + 1; x <= cx - 2; x++)
                            for (int w = 0; w < effHw; w++)
                                SetHallwayIfWall(x, midY + w);

                        CarveVerticalDoorway(room.xMax, doorY, doorSpan, spawnDoor);
                        CarveVerticalDoorway(cx - 1, doorY, doorSpan, dist >= 4 && spawnDoor);
                    }
                    return true;
                }
            }
        }
        else if (wallSide == 3) // West (-X from room.xMin - 1)
        {
            if (doorY + doorSpan >= _height - 2) return false;
            for (int dist = 1; dist <= maxDist; dist++)
            {
                int cx = room.xMin - 1 - dist;
                if (cx <= 1) break;

                if (SpanMatchesCellType(cx, doorY, doorSpan, false, CellType.HallwayFloor))
                {
                    for (int x = cx + 1; x <= room.xMin - 2; x++)
                        for (int w = 0; w < effHw; w++)
                            SetHallwayIfWall(x, midY + w);

                    CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan, spawnDoor);
                    return true;
                }

                if (SpanMatchesRoomFloor(cx, doorY, doorSpan, false))
                {
                    if (dist == 1)
                    {
                        CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan, spawnDoor);
                    }
                    else
                    {
                        for (int x = cx + 2; x <= room.xMin - 2; x++)
                            for (int w = 0; w < effHw; w++)
                                SetHallwayIfWall(x, midY + w);

                        CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan, spawnDoor);
                        CarveVerticalDoorway(cx + 1, doorY, doorSpan, dist >= 4 && spawnDoor);
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

    private void ForceSecondHallwayEntranceForRoom(RectInt room, RectInt ringOuter, System.Random rng, float doorChance)
    {
        int hw = _config != null ? _config.HallwayWidth : 6;
        int doorSpan = (room.width <= 8 || room.height <= 8) ? 2 : GetDoorwaySpan();
        int effHw = Mathf.Clamp(Mathf.Min(room.width - 2, room.height - 2), doorSpan, hw);
        int doorOffset = Mathf.Max(0, (effHw - doorSpan) / 2);

        int midX = Mathf.Clamp(room.xMin + (room.width - effHw) / 2, room.xMin + 1, Mathf.Max(room.xMin + 1, room.xMax - effHw - 1));
        int midY = Mathf.Clamp(room.yMin + (room.height - effHw) / 2, room.yMin + 1, Mathf.Max(room.yMin + 1, room.yMax - effHw - 1));
        int doorX = midX + doorOffset;
        int doorY = midY + doorOffset;
        bool spawnDoor = rng.NextDouble() <= doorChance;

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

            if (side == 0 && room.yMax + effHw + 1 < _height - 1)
            {
                CarveHorizontalDoorway(doorX, room.yMax, doorSpan, spawnDoor);
                var from = new Vector2Int(midX, room.yMax + 1);
                var to = new Vector2Int(Mathf.Clamp(midX, ringOuter.xMin, ringOuter.xMax - effHw), ringOuter.yMax - effHw);
                CarveLCorridor(from, to, effHw, true);
                return;
            }
            if (side == 2 && room.yMin - 1 - effHw > 1)
            {
                CarveHorizontalDoorway(doorX, room.yMin - 1, doorSpan, spawnDoor);
                var from = new Vector2Int(midX, room.yMin - 1 - effHw);
                var to = new Vector2Int(Mathf.Clamp(midX, ringOuter.xMin, ringOuter.xMax - effHw), ringOuter.yMin);
                CarveLCorridor(from, to, effHw, true);
                return;
            }
            if (side == 1 && room.xMax + effHw + 1 < _width - 1)
            {
                CarveVerticalDoorway(room.xMax, doorY, doorSpan, spawnDoor);
                var from = new Vector2Int(room.xMax + 1, midY);
                var to = new Vector2Int(ringOuter.xMax - effHw, Mathf.Clamp(midY, ringOuter.yMin, ringOuter.yMax - effHw));
                CarveLCorridor(from, to, effHw, false);
                return;
            }
            if (side == 3 && room.xMin - 1 - effHw > 1)
            {
                CarveVerticalDoorway(room.xMin - 1, doorY, doorSpan, spawnDoor);
                var from = new Vector2Int(room.xMin - 1 - effHw, midY);
                var to = new Vector2Int(ringOuter.xMin, Mathf.Clamp(midY, ringOuter.yMin, ringOuter.yMax - effHw));
                CarveLCorridor(from, to, effHw, false);
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
        int hw = _config != null ? _config.HallwayWidth : 6;
        int effHw = Mathf.Clamp(Mathf.Min(room.width - 2, room.height - 2), doorSpan, hw);
        int doorOffset = Mathf.Max(0, (effHw - doorSpan) / 2);
        const int maxBranchLength = 40;

        int midX = Mathf.Clamp(room.xMin + (room.width - effHw) / 2, room.xMin + 1, Mathf.Max(room.xMin + 1, room.xMax - effHw - 1));
        int midY = Mathf.Clamp(room.yMin + (room.height - effHw) / 2, room.yMin + 1, Mathf.Max(room.yMin + 1, room.yMax - effHw - 1));

        // 1. North
        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkY = room.yMax + dist;
            if (checkY >= _height - 2) break;
            if (_grid[midX, checkY] == CellType.HallwayFloor && _grid[midX + effHw - 1, checkY] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(midX, room.yMax + 1, effHw, dist - 1))
                {
                    for (int y = room.yMax + 1; y < checkY; y++)
                        for (int w = 0; w < effHw; w++)
                            _grid[midX + w, y] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(midX + doorOffset, room.yMax);
                    horizontalDoor = true;
                    return true;
                }
                break;
            }
            if (_grid[midX, checkY] != CellType.Wall) break;
        }

        // 2. South
        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkY = room.yMin - 1 - dist;
            if (checkY <= 1) break;
            if (_grid[midX, checkY] == CellType.HallwayFloor && _grid[midX + effHw - 1, checkY] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(midX, checkY + 1, effHw, dist - 1))
                {
                    for (int y = checkY + 1; y <= room.yMin - 2; y++)
                        for (int w = 0; w < effHw; w++)
                            _grid[midX + w, y] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(midX + doorOffset, room.yMin - 1);
                    horizontalDoor = true;
                    return true;
                }
                break;
            }
            if (_grid[midX, checkY] != CellType.Wall) break;
        }

        // 3. East
        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkX = room.xMax + dist;
            if (checkX >= _width - 2) break;
            if (_grid[checkX, midY] == CellType.HallwayFloor && _grid[checkX, midY + effHw - 1] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(room.xMax + 1, midY, dist - 1, effHw))
                {
                    for (int x = room.xMax + 1; x < checkX; x++)
                        for (int w = 0; w < effHw; w++)
                            _grid[x, midY + w] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(room.xMax, midY + doorOffset);
                    horizontalDoor = false;
                    return true;
                }
                break;
            }
            if (_grid[checkX, midY] != CellType.Wall) break;
        }

        // 4. West
        for (int dist = 1; dist <= maxBranchLength; dist++)
        {
            int checkX = room.xMin - 1 - dist;
            if (checkX <= 1) break;
            if (_grid[checkX, midY] == CellType.HallwayFloor && _grid[checkX, midY + effHw - 1] == CellType.HallwayFloor)
            {
                if (IsRegionPureWall(checkX + 1, midY, dist - 1, effHw))
                {
                    for (int x = checkX + 1; x <= room.xMin - 2; x++)
                        for (int w = 0; w < effHw; w++)
                            _grid[x, midY + w] = CellType.HallwayFloor;

                    doorCell = new Vector2Int(room.xMin - 1, midY + doorOffset);
                    horizontalDoor = false;
                    return true;
                }
                break;
            }
            if (_grid[checkX, midY] != CellType.Wall) break;
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

    private void CarveHorizontalDoorway(int x, int y, int doorSpan, bool spawnDoor)
    {
        if (x < 0 || x + doorSpan - 1 >= _width || y < 0 || y >= _height) return;

        for (int s = 0; s < doorSpan; s++)
        {
            _grid[x + s, y] = CellType.Doorway;
            _doorwayCells.Add(new Vector2Int(x + s, y));
        }

        MarkDoorwayClearanceZone(x, y - 3, doorSpan, 7);

        if (spawnDoor)
        {
            int fl = _currentFloor.FloorLevel;
            if (doorSpan >= 4)
            {
                Vector2 leftHinge = GridToWorld(x - 0.38f, y, fl);
                Vector2 rightHinge = GridToWorld(x + doorSpan - 1 + 0.38f, y, fl);
                SpawnSwingDoor(leftHinge, 0f, 1.85f);
                SpawnSwingDoor(rightHinge, 180f, 1.85f);
            }
            else
            {
                Vector2 hingeWorld = GridToWorld(x - 0.38f, y, fl);
                SpawnSwingDoor(hingeWorld, 0f, 1.80f);
            }
        }
    }

    private void CarveVerticalDoorway(int x, int y, int doorSpan, bool spawnDoor)
    {
        if (x < 0 || x >= _width || y < 0 || y + doorSpan - 1 >= _height) return;

        for (int s = 0; s < doorSpan; s++)
        {
            _grid[x, y + s] = CellType.Doorway;
            _doorwayCells.Add(new Vector2Int(x, y + s));
        }

        MarkDoorwayClearanceZone(x - 3, y, 7, doorSpan);

        if (spawnDoor)
        {
            int fl = _currentFloor.FloorLevel;
            if (doorSpan >= 4)
            {
                Vector2 bottomHinge = GridToWorld(x, y - 0.38f, fl);
                Vector2 topHinge = GridToWorld(x, y + doorSpan - 1 + 0.38f, fl);
                SpawnSwingDoor(bottomHinge, 90f, 1.85f);
                SpawnSwingDoor(topHinge, -90f, 1.85f);
            }
            else
            {
                Vector2 hingeWorld = GridToWorld(x, y - 0.38f, fl);
                SpawnSwingDoor(hingeWorld, 90f, 1.80f);
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
        int hw = _config != null ? _config.HallwayWidth : 6;
        int doorSpan = GetDoorwaySpan();
        int doorOffset = Mathf.Max(0, (hw - doorSpan) / 2);
        int vestibuleDepth = Mathf.Max(4, hw + 2);

        // 1. On 1st Floor, force all 4 exterior entrances and their inward vestibules to be 100% clear
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
                        if (w >= doorOffset && w < doorOffset + doorSpan)
                            _grid[gx + w, _height - 1] = CellType.Doorway;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(gx + w, _height - 1 - d);
                    }
                }
                else if (ent.WallSide == 1)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        if (w >= doorOffset && w < doorOffset + doorSpan)
                            _grid[_width - 1, gy + w] = CellType.Doorway;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(_width - 1 - d, gy + w);
                    }
                }
                else if (ent.WallSide == 2)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        if (w >= doorOffset && w < doorOffset + doorSpan)
                            _grid[gx + w, 0] = CellType.Doorway;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(gx + w, d);
                    }
                }
                else if (ent.WallSide == 3)
                {
                    for (int w = 0; w < hw; w++)
                    {
                        if (w >= doorOffset && w < doorOffset + doorSpan)
                            _grid[0, gy + w] = CellType.Doorway;
                        for (int d = 1; d <= vestibuleDepth; d++)
                            ClearToHallwayFloor(d, gy + w);
                    }
                }
            }
        }

        // 2. Ensure all Stairwell Bays on the ring hallway are 100% clear HallwayFloor
        for (int lowerFloor = -1; lowerFloor <= 1; lowerFloor++)
        {
            GetStairwellGridPositionsForShaft(lowerFloor, ringOuter, out Vector2Int bayA, out Vector2Int bayB);
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    ClearToHallwayFloor(bayA.x + dx, bayA.y + dy);
                    ClearToHallwayFloor(bayB.x + dx, bayB.y + dy);
                }
            }
        }

        // 3. Ensure no CoverPillar is within 3 tiles of any Doorway cell
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

        // 4. On 1st Floor, BFS connectivity verification from the Central Room to all 4 perimeter entrances
        if (floorLevel == 0)
        {
            EnsureAllEntrancesConnectedViaBfs(ringOuter, hw);
        }
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

                CellType t = _grid[nx, ny];
                if (t == CellType.HallwayFloor ||
                    t == CellType.RoomFloor ||
                    t == CellType.ObjectiveRoomFloor ||
                    t == CellType.Doorway)
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
        int fl = _currentFloor.FloorLevel;

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

                Vector2 centerWorld = GridToWorld(x + (runW - 1) * 0.5f, y + (runH - 1) * 0.5f, fl);
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

                Vector2 worldPos = GridToWorld(x, y, floorLevel);
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
        // 1. Place ComputerTerminal in the center of the Objective Room on _objectiveFloorLevel
        Vector2 objCenter = GridToWorld(
            _objectiveRoomBounds.xMin + (_objectiveRoomBounds.width - 1) * 0.5f,
            _objectiveRoomBounds.yMin + (_objectiveRoomBounds.height - 1) * 0.5f,
            _objectiveFloorLevel);

        var terminalGo = GameObject.Find("ComputerTerminal");
        if (terminalGo != null)
        {
            terminalGo.transform.position = new Vector3(objCenter.x, objCenter.y, 0f);

            var termLight = terminalGo.GetComponent<Light2D>();
            if (termLight == null) termLight = terminalGo.AddComponent<Light2D>();
            termLight.lightType = Light2D.LightType.Point;
            termLight.color = new Color(0.95f, 0.70f, 0.25f, 1f);
            termLight.intensity = 0.85f;
            termLight.pointLightOuterRadius = 5.5f;
            termLight.pointLightInnerRadius = 0.6f;
        }

        // 2. Shuffle the 4 entrances on the 1st Floor so Team A and Team B spawn at random distinct entrances
        int[] entranceOrder = { 0, 1, 2, 3 };
        for (int i = entranceOrder.Length - 1; i > 0; i--)
        {
            int swapIdx = rng.Next(i + 1);
            (entranceOrder[i], entranceOrder[swapIdx]) = (entranceOrder[swapIdx], entranceOrder[i]);
        }

        EntranceData teamAEntrance = _entrances[entranceOrder[0]];
        EntranceData teamBEntrance = _entrances[entranceOrder[1]];

        // 3. Position Team A Extraction Zone, Player, and Friendly Dummy at Team A's random courtyard entrance (1st Floor)
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

        // 4. Position Team B Extraction Zone and Enemy Dummies
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

        // Place EnemyDummy_Patrol on the Objective Floor's ring hallway clear of doorways & stairwells
        var enemyPatrol = GameObject.Find("EnemyDummy_Patrol");
        if (enemyPatrol != null)
        {
            Vector2 guardPos = FindSafeHallwayGuardPosition(_objectiveFloorLevel);
            TeleportCharacter(enemyPatrol, guardPos);
            var dc = enemyPatrol.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(guardPos, new Vector2(-3.5f, 0f), new Vector2(3.5f, 0f));
        }
    }

    private Vector2 FindSafeHallwayGuardPosition(int floorLevel)
    {
        if (!_floorStates.TryGetValue(floorLevel, out var state))
        {
            state = _floorStates[0];
            floorLevel = 0;
        }

        int hw = _config != null ? _config.HallwayWidth : 6;
        int preferredX = state.CentralRoomBounds.xMin + state.CentralRoomBounds.width / 2;
        int preferredY = state.CentralRoomBounds.yMax + 1 + (hw / 2);

        for (int y = 4; y < _height - 4; y++)
        {
            for (int x = 4; x < _width - 4; x++)
            {
                if (state.Grid[x, y] == CellType.HallwayFloor && ! state.ProtectedZone[x, y])
                {
                    return GridToWorld(x, y, floorLevel);
                }
            }
        }

        return GridToWorld(preferredX, preferredY, floorLevel);
    }

    private void ValidateEntranceClearancePhysics()
    {
        Physics2D.SyncTransforms();
        bool allClear = true;

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

    private Vector2 GridToWorld(float gx, float gy, int floorLevel = 0)
    {
        Vector2 offset = GetFloorWorldOffset(floorLevel);
        return new Vector2(
            gx - (_width * 0.5f) + 0.5f + offset.x,
            gy - (_height * 0.5f) + 0.5f + offset.y);
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
        GUILayout.BeginArea(new Rect(10f, 48f, 490f, 64f));
        GUILayout.BeginVertical("box");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Seed: {MapSeed} | Floors ({_activeFloors.Count}): {floorsDesc}");
        if (GUILayout.Button("New Map Seed", GUILayout.Width(110f), GUILayout.Height(22f)))
        {
            RequestNewRandomMap();
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(
            $"Current: {GetFloorDisplayName(CurrentLocalFloorLevel)} | " +
            $"Objective: {GetFloorDisplayName(_objectiveFloorLevel)} | " +
            $"Rooms: {_allRooms.Count} | Windows: {_spawnedWindows.Count}");
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
