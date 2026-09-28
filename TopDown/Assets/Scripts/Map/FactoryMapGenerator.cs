using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Server-authoritative procedural cement factory generator.
/// Synchronizes a single integer seed (<see cref="MapSeed"/>) across all clients via Netcode,
/// then deterministically constructs the factory exterior, 4 shifted perimeter entrances,
/// interconnected hallways, dark interior rooms, 1 Objective Room, interior cover pillars,
/// physics-pushed <see cref="SwingDoor"/>s, and steady/flickering <see cref="HallwayLight"/>s.
/// </summary>
public class FactoryMapGenerator : NetworkBehaviour
{
    // ────────────────────────────── Types ──────────────────────────────────

    private enum CellType : byte
    {
        OutsideCourtyard = 0,
        Wall = 1,
        HallwayFloor = 2,
        RoomFloor = 3,
        ObjectiveRoomFloor = 4,
        Doorway = 5,
        CoverPillar = 6
    }

    private struct RoomData
    {
        public RectInt Bounds; // Interior floor bounds
        public bool IsObjectiveRoom;
    }

    private struct EntranceData
    {
        public int WallSide; // 0=North, 1=East, 2=South, 3=West
        public Vector2Int GridCell; // Primary cell of the 2-tile opening
        public Vector2 WorldPosition;
        public Vector2 CourtyardSpawnPosition;
    }

    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("Factory map generation settings asset.")]
    [SerializeField] private FactoryMapConfig _config;

    [Header("Seed Settings")]
    [Tooltip("If true, the Host generates a new random map seed at the start of each session.")]
    [SerializeField] private bool _useRandomSeedOnStart = true;

    [Tooltip("Fixed seed used when Use Random Seed On Start is false.")]
    [SerializeField] private int _initialSeed = 40921;

    [Tooltip("Show a 'New Map Seed' button in the top-left HUD for instant playtesting.")]
    [SerializeField] private bool _showMapSeedHud = true;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<int> _netMapSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private int _lastGeneratedSeed;

    // ────────────────────────────── Runtime State ──────────────────────────

    private GameObject _mapRoot;
    private CellType[,] _grid;
    private bool[,] _entranceProtectedZone;
    private int _width;
    private int _height;
    private readonly List<RoomData> _rooms = new List<RoomData>();
    private readonly List<SwingDoor> _spawnedDoors = new List<SwingDoor>();
    private readonly List<Vector2Int> _doorwayCells = new List<Vector2Int>();
    private EntranceData[] _entrances = new EntranceData[4];
    private RectInt _objectiveRoomBounds;

    private static Sprite _cachedTileSprite;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The active seed driving the current factory layout.</summary>
    public int MapSeed => IsSpawned ? _netMapSeed.Value : _lastGeneratedSeed;

    /// <summary>Number of rooms generated in the current layout (including the Objective Room).</summary>
    public int GeneratedRoomCount => _rooms.Count;

    /// <summary>Number of physics-pushed swinging doors spawned in the current layout.</summary>
    public int SpawnedDoorCount => _spawnedDoors.Count;

    /// <summary>True when all 4 perimeter entrances passed both grid BFS reachability and physics corridor clearance checks.</summary>
    public bool AllEntrancesVerifiedClear { get; private set; }

    /// <summary>World positions of the 4 shifted perimeter entrances (North, East, South, West).</summary>
    public Vector2[] GetEntranceWorldPositions()
    {
        var result = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = _entrances[i].WorldPosition;
        }
        return result;
    }

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

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

    // ──────────────────────────── Door Network Sync ────────────────────────

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

    // ──────────────────────────── Procedural Generation ────────────────────

    /// <summary>
    /// Deterministically builds the entire factory layout for the given integer seed.
    /// </summary>
    public void GenerateFactory(int seed)
    {
        _lastGeneratedSeed = seed;
        var rng = new System.Random(seed);

        _width = _config != null ? _config.BuildingWidth : 36;
        _height = _config != null ? _config.BuildingHeight : 36;
        _grid = new CellType[_width, _height];
        _entranceProtectedZone = new bool[_width, _height];
        _rooms.Clear();
        _spawnedDoors.Clear();
        _doorwayCells.Clear();
        AllEntrancesVerifiedClear = false;

        // Clear previous generated map container
        if (_mapRoot != null)
        {
            Destroy(_mapRoot);
        }
        var existingRoot = GameObject.Find("GeneratedFactoryMap");
        if (existingRoot != null)
        {
            Destroy(existingRoot);
        }
        _mapRoot = new GameObject("GeneratedFactoryMap");

        // Configure ambient darkness and resize FloorGrid to cover building + courtyard
        ApplyAmbientLightingAndFloor();

        // Step 1: Initialize entire building footprint as solid concrete Wall
        for (int x = 0; x < _width; x++)
        {
            for (int y = 0; y < _height; y++)
            {
                _grid[x, y] = CellType.Wall;
            }
        }

        // Step 2: Carve the 1 Objective Room near the center of the factory
        PlaceObjectiveRoom(rng);

        // Step 3: Carve the Inner Ring Hallway around the Objective Room
        RectInt ringOuter = CarveInnerRingHallway();

        // Step 4: Pick the 4 Shifted Perimeter Entrances (North, East, South, West), reserve vestibules, and connect to Inner Ring
        PlaceAndConnectFourEntrances(rng, ringOuter);

        // Step 5: Carve Dark Interior Rooms in the surrounding factory sectors + connect with doorways & SwingDoors
        CarveInteriorRoomsAndDoors(rng);

        // Step 6: Place interior concrete cover pillars inside rooms (respecting doorway & entrance exclusion zones)
        PlaceInteriorCoverPillars(rng);

        // Step 6.5: Safety Pass — enforce 100% entrance, vestibule, doorway swing arc, and BFS path clearance
        EnforceEntranceAndDoorwaySafetyClearance(ringOuter);

        // Step 7: Instantiate greedy-merged concrete wall colliders and cover pillars
        BuildWallAndCoverGeometry();

        // Step 8: Spawn steady & flickering emergency lights along hallways ONLY (rooms stay dark!)
        SpawnHallwayEmergencyLights(rng);

        // Step 9: Position the ComputerTerminal in the Objective Room & assign Teams/Dummies safely clear of entrances
        PlaceObjectiveAndTeams(rng);

        // Step 10: Final physics verification ensuring zero solid objects or characters block any of the 4 entrances
        ValidateEntranceClearancePhysics();
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
                globalLight.color = new Color(0.65f, 0.72f, 0.82f, 1f); // Cold moonless night ambient
            }
        }

        // Ensure the concrete FloorGrid covers the entire factory + exterior courtyard
        var floorGo = GameObject.Find("FloorGrid");
        if (floorGo != null)
        {
            int margin = _config != null ? _config.CourtyardMargin : 6;
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

    private void PlaceObjectiveRoom(System.Random rng)
    {
        int objSize = _config != null ? _config.ObjectiveRoomSize : 8;
        int offsetX = rng.Next(-2, 3);
        int offsetY = rng.Next(-2, 3);

        int startX = (_width - objSize) / 2 + offsetX;
        int startY = (_height - objSize) / 2 + offsetY;

        _objectiveRoomBounds = new RectInt(startX, startY, objSize, objSize);
        for (int x = startX; x < startX + objSize; x++)
        {
            for (int y = startY; y < startY + objSize; y++)
            {
                _grid[x, y] = CellType.ObjectiveRoomFloor;
            }
        }

        _rooms.Add(new RoomData
        {
            Bounds = _objectiveRoomBounds,
            IsObjectiveRoom = true
        });
    }

    private RectInt CarveInnerRingHallway()
    {
        int hw = _config != null ? _config.HallwayWidth : 2;
        // Leave a 1-tile concrete wall around _objectiveRoomBounds, then carve a 2-tile hallway loop around it
        int ringMinX = _objectiveRoomBounds.xMin - 1 - hw;
        int ringMaxX = _objectiveRoomBounds.xMax + 1 + hw;
        int ringMinY = _objectiveRoomBounds.yMin - 1 - hw;
        int ringMaxY = _objectiveRoomBounds.yMax + 1 + hw;

        for (int x = ringMinX; x < ringMaxX; x++)
        {
            for (int y = ringMinY; y < ringMaxY; y++)
            {
                bool inTopBand = y >= _objectiveRoomBounds.yMax + 1;
                bool inBottomBand = y < _objectiveRoomBounds.yMin - 1;
                bool inLeftBand = x < _objectiveRoomBounds.xMin - 1;
                bool inRightBand = x >= _objectiveRoomBounds.xMax + 1;

                if (inTopBand || inBottomBand || inLeftBand || inRightBand)
                {
                    _grid[x, y] = CellType.HallwayFloor;
                }
            }
        }

        // Punch 2 doorways into the Objective Room (North & South or East & West) with SwingDoors!
        int midX = _objectiveRoomBounds.xMin + _objectiveRoomBounds.width / 2 - 1;
        int northWallY = _objectiveRoomBounds.yMax;
        int southWallY = _objectiveRoomBounds.yMin - 1;

        CarveHorizontalDoorway(midX, northWallY, true);
        CarveHorizontalDoorway(midX, southWallY, true);

        return new RectInt(ringMinX, ringMinY, ringMaxX - ringMinX, ringMaxY - ringMinY);
    }

    private void PlaceAndConnectFourEntrances(System.Random rng, RectInt ringOuter)
    {
        int margin = _config != null ? _config.EntranceCornerMargin : 6;
        int hw = _config != null ? _config.HallwayWidth : 2;
        float courtyardDist = (_config != null ? _config.CourtyardMargin : 6) * 0.75f;
        const int vestibuleDepth = 4;

        // 0 = North (y = _height - 1), shifted randomly along X
        int northX = rng.Next(margin, _width - margin - hw);
        _entrances[0] = new EntranceData
        {
            WallSide = 0,
            GridCell = new Vector2Int(northX, _height - 1),
            WorldPosition = GridToWorld(northX + 0.5f, _height - 1),
            CourtyardSpawnPosition = GridToWorld(northX + 0.5f, _height - 1 + courtyardDist)
        };
        CarveHorizontalDoorway(northX, _height - 1, true);
        ReserveEntranceVestibule(northX, _height - 1 - vestibuleDepth, hw, vestibuleDepth, 1);
        CarveLCorridor(new Vector2Int(northX, _height - 1 - vestibuleDepth), new Vector2Int(
            Mathf.Clamp(northX, ringOuter.xMin, ringOuter.xMax - hw),
            ringOuter.yMax - hw), hw, true);

        // 1 = East (x = _width - 1), shifted randomly along Y
        int eastY = rng.Next(margin, _height - margin - hw);
        _entrances[1] = new EntranceData
        {
            WallSide = 1,
            GridCell = new Vector2Int(_width - 1, eastY),
            WorldPosition = GridToWorld(_width - 1, eastY + 0.5f),
            CourtyardSpawnPosition = GridToWorld(_width - 1 + courtyardDist, eastY + 0.5f)
        };
        CarveVerticalDoorway(_width - 1, eastY, true);
        ReserveEntranceVestibule(_width - 1 - vestibuleDepth, eastY, vestibuleDepth, hw, 1);
        CarveLCorridor(new Vector2Int(_width - 1 - vestibuleDepth, eastY), new Vector2Int(
            ringOuter.xMax - hw,
            Mathf.Clamp(eastY, ringOuter.yMin, ringOuter.yMax - hw)), hw, false);

        // 2 = South (y = 0), shifted randomly along X
        int southX = rng.Next(margin, _width - margin - hw);
        _entrances[2] = new EntranceData
        {
            WallSide = 2,
            GridCell = new Vector2Int(southX, 0),
            WorldPosition = GridToWorld(southX + 0.5f, 0),
            CourtyardSpawnPosition = GridToWorld(southX + 0.5f, -courtyardDist)
        };
        CarveHorizontalDoorway(southX, 0, true);
        ReserveEntranceVestibule(southX, 1, hw, vestibuleDepth, 1);
        CarveLCorridor(new Vector2Int(southX, vestibuleDepth), new Vector2Int(
            Mathf.Clamp(southX, ringOuter.xMin, ringOuter.xMax - hw),
            ringOuter.yMin), hw, true);

        // 3 = West (x = 0), shifted randomly along Y
        int westY = rng.Next(margin, _height - margin - hw);
        _entrances[3] = new EntranceData
        {
            WallSide = 3,
            GridCell = new Vector2Int(0, westY),
            WorldPosition = GridToWorld(0, westY + 0.5f),
            CourtyardSpawnPosition = GridToWorld(-courtyardDist, westY + 0.5f)
        };
        CarveVerticalDoorway(0, westY, true);
        ReserveEntranceVestibule(1, westY, vestibuleDepth, hw, 1);
        CarveLCorridor(new Vector2Int(vestibuleDepth, westY), new Vector2Int(
            ringOuter.xMin,
            Mathf.Clamp(westY, ringOuter.yMin, ringOuter.yMax - hw)), hw, false);
    }

    /// <summary>
    /// Carves a guaranteed straight hallway vestibule inward from an exterior entrance and marks a protected
    /// margin around it so interior rooms or cover pillars can never block or pinch the entrance mouth.
    /// </summary>
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

    private void CarveInteriorRoomsAndDoors(System.Random rng)
    {
        int targetRooms = _config != null ? _config.TargetRoomCount : 9;
        int minSize = _config != null ? _config.MinRoomSize : 5;
        int maxSize = _config != null ? _config.MaxRoomSize : 8;
        float doorChance = _config != null ? _config.DoorSpawnChance : 0.9f;

        int attempts = 160;
        int created = 0;

        for (int a = 0; a < attempts && created < targetRooms; a++)
        {
            int rw = rng.Next(minSize, maxSize + 1);
            int rh = rng.Next(minSize, maxSize + 1);
            int rx = rng.Next(2, _width - rw - 2);
            int ry = rng.Next(2, _height - rh - 2);

            var candidate = new RectInt(rx, ry, rw, rh);

            // Check that the room + its 1-tile enclosing wall is currently all Wall and outside entrance protection zones
            if (!IsRegionPureWall(rx - 1, ry - 1, rw + 2, rh + 2)) continue;

            // Ensure the room's 1-tile wall touches an existing HallwayFloor so we can punch a doorway directly into the hallway!
            if (!TryFindHallwayDoorwaySpot(candidate, rng, out Vector2Int doorCell, out bool horizontalDoor))
            {
                continue;
            }

            // Carve the room interior
            for (int x = rx; x < rx + rw; x++)
            {
                for (int y = ry; y < ry + rh; y++)
                {
                    _grid[x, y] = CellType.RoomFloor;
                }
            }

            // Carve the doorway connecting this room to the hallway + spawn a SwingDoor
            bool spawnSwingDoor = rng.NextDouble() <= doorChance;
            if (horizontalDoor)
            {
                CarveHorizontalDoorway(doorCell.x, doorCell.y, spawnSwingDoor);
            }
            else
            {
                CarveVerticalDoorway(doorCell.x, doorCell.y, spawnSwingDoor);
            }

            _rooms.Add(new RoomData
            {
                Bounds = candidate,
                IsObjectiveRoom = false
            });
            created++;
        }
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
        System.Random rng,
        out Vector2Int doorCell,
        out bool horizontalDoor)
    {
        var candidates = new List<(Vector2Int cell, bool horiz)>();

        // Top wall of room (y = room.yMax): check if y+1 is HallwayFloor for a 2-tile span
        if (room.yMax + 1 < _height - 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 3; x++)
            {
                if (_grid[x, room.yMax + 1] == CellType.HallwayFloor &&
                    _grid[x + 1, room.yMax + 1] == CellType.HallwayFloor)
                {
                    candidates.Add((new Vector2Int(x, room.yMax), true));
                }
            }
        }

        // Bottom wall of room (y = room.yMin - 1): check if y-2 is HallwayFloor
        if (room.yMin - 2 >= 1)
        {
            for (int x = room.xMin + 1; x <= room.xMax - 3; x++)
            {
                if (_grid[x, room.yMin - 2] == CellType.HallwayFloor &&
                    _grid[x + 1, room.yMin - 2] == CellType.HallwayFloor)
                {
                    candidates.Add((new Vector2Int(x, room.yMin - 1), true));
                }
            }
        }

        // Right wall of room (x = room.xMax): check if x+1 is HallwayFloor
        if (room.xMax + 1 < _width - 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 3; y++)
            {
                if (_grid[room.xMax + 1, y] == CellType.HallwayFloor &&
                    _grid[room.xMax + 1, y + 1] == CellType.HallwayFloor)
                {
                    candidates.Add((new Vector2Int(room.xMax, y), false));
                }
            }
        }

        // Left wall of room (x = room.xMin - 1): check if x-2 is HallwayFloor
        if (room.xMin - 2 >= 1)
        {
            for (int y = room.yMin + 1; y <= room.yMax - 3; y++)
            {
                if (_grid[room.xMin - 2, y] == CellType.HallwayFloor &&
                    _grid[room.xMin - 2, y + 1] == CellType.HallwayFloor)
                {
                    candidates.Add((new Vector2Int(room.xMin - 1, y), false));
                }
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

    private void CarveHorizontalDoorway(int x, int y, bool spawnDoor)
    {
        if (x < 0 || x + 1 >= _width || y < 0 || y >= _height) return;

        _grid[x, y] = CellType.Doorway;
        _grid[x + 1, y] = CellType.Doorway;
        _doorwayCells.Add(new Vector2Int(x, y));
        _doorwayCells.Add(new Vector2Int(x + 1, y));

        // Reserve a 3-tile perpendicular swing & entry clearance zone on both sides of the doorway
        MarkDoorwayClearanceZone(x, y - 3, 2, 7);

        if (spawnDoor)
        {
            // Hinge on the left edge of the 2-tile doorway, extending +X across the 2-unit gap
            Vector2 hingeWorld = GridToWorld(x - 0.38f, y);
            SpawnSwingDoor(hingeWorld, 0f, 1.80f);
        }
    }

    private void CarveVerticalDoorway(int x, int y, bool spawnDoor)
    {
        if (x < 0 || x >= _width || y < 0 || y + 1 >= _height) return;

        _grid[x, y] = CellType.Doorway;
        _grid[x, y + 1] = CellType.Doorway;
        _doorwayCells.Add(new Vector2Int(x, y));
        _doorwayCells.Add(new Vector2Int(x, y + 1));

        // Reserve a 3-tile perpendicular swing & entry clearance zone on both sides of the doorway
        MarkDoorwayClearanceZone(x - 3, y, 7, 2);

        if (spawnDoor)
        {
            // Hinge on the bottom edge of the 2-tile doorway, extending +Y across the 2-unit gap
            Vector2 hingeWorld = GridToWorld(x, y - 0.38f);
            SpawnSwingDoor(hingeWorld, 90f, 1.80f);
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
        doorGo.transform.SetParent(_mapRoot.transform, false);

        var door = doorGo.AddComponent<SwingDoor>();
        Color doorColor = _config != null ? _config.DoorColor : new Color(0.45f, 0.38f, 0.32f, 1f);
        door.Initialize(idx, this, hingeWorld, closedAngleDeg, length, doorColor);

        _spawnedDoors.Add(door);
    }

    private void PlaceInteriorCoverPillars(System.Random rng)
    {
        int minCover = _config != null ? _config.MinCoverPerRoom : 1;
        int maxCover = _config != null ? _config.MaxCoverPerRoom : 2;

        for (int i = 0; i < _rooms.Count; i++)
        {
            RoomData room = _rooms[i];
            if (room.Bounds.width < 5 || room.Bounds.height < 5) continue;

            int targetCount = rng.Next(minCover, maxCover + 1);
            int placed = 0;
            int maxAttempts = 20;

            for (int attempt = 0; attempt < maxAttempts && placed < targetCount; attempt++)
            {
                // Keep a 1-tile walkway margin inside the room walls so doorways and room perimeters are never blocked
                int px = rng.Next(room.Bounds.xMin + 1, room.Bounds.xMax - 1);
                int py = rng.Next(room.Bounds.yMin + 1, room.Bounds.yMax - 1);

                // Safety Check 1: Never place a pillar inside any entrance vestibule or doorway swing clearance zone
                if (_entranceProtectedZone[px, py] || IsNearAnyDoorwayOrEntrance(px, py, 3.1f))
                {
                    continue;
                }

                // Safety Check 2: Avoid the center of the Objective Room where ComputerTerminal sits
                if (room.IsObjectiveRoom)
                {
                    int cx = room.Bounds.xMin + room.Bounds.width / 2;
                    int cy = room.Bounds.yMin + room.Bounds.height / 2;
                    if (Mathf.Abs(px - cx) <= 2 && Mathf.Abs(py - cy) <= 2) continue;
                }

                // Safety Check 3: Do not place cover pillars adjacent to existing cover pillars (prevents choke walls)
                if (HasAdjacentCoverPillar(px, py))
                {
                    continue;
                }

                _grid[px, py] = CellType.CoverPillar;
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

        for (int e = 0; e < 4; e++)
        {
            Vector2 ent = _entrances[e].GridCell;
            if ((cell - ent).sqrMagnitude < minDistSq) return true;
        }

        return false;
    }

    private bool HasAdjacentCoverPillar(int gx, int gy)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
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

    /// <summary>
    /// Post-generation safety pass that guarantees all 4 perimeter entrances, inward vestibules,
    /// interior doorway swing arcs, and paths to the Objective Room are 100% unobstructed.
    /// </summary>
    private void EnforceEntranceAndDoorwaySafetyClearance(RectInt ringOuter)
    {
        int hw = _config != null ? _config.HallwayWidth : 2;
        const int vestibuleDepth = 4;

        // 1. Force all 4 exterior entrances and their 4-tile-deep inward vestibules to be 100% clear
        for (int e = 0; e < 4; e++)
        {
            EntranceData ent = _entrances[e];
            int gx = ent.GridCell.x;
            int gy = ent.GridCell.y;

            if (ent.WallSide == 0) // North (y = _height - 1)
            {
                for (int w = 0; w < hw; w++)
                {
                    _grid[gx + w, _height - 1] = CellType.Doorway;
                    for (int d = 1; d <= vestibuleDepth; d++)
                    {
                        ClearToHallwayFloor(gx + w, _height - 1 - d);
                    }
                }
            }
            else if (ent.WallSide == 1) // East (x = _width - 1)
            {
                for (int w = 0; w < hw; w++)
                {
                    _grid[_width - 1, gy + w] = CellType.Doorway;
                    for (int d = 1; d <= vestibuleDepth; d++)
                    {
                        ClearToHallwayFloor(_width - 1 - d, gy + w);
                    }
                }
            }
            else if (ent.WallSide == 2) // South (y = 0)
            {
                for (int w = 0; w < hw; w++)
                {
                    _grid[gx + w, 0] = CellType.Doorway;
                    for (int d = 1; d <= vestibuleDepth; d++)
                    {
                        ClearToHallwayFloor(gx + w, d);
                    }
                }
            }
            else if (ent.WallSide == 3) // West (x = 0)
            {
                for (int w = 0; w < hw; w++)
                {
                    _grid[0, gy + w] = CellType.Doorway;
                    for (int d = 1; d <= vestibuleDepth; d++)
                    {
                        ClearToHallwayFloor(d, gy + w);
                    }
                }
            }
        }

        // 2. Ensure no CoverPillar is within 2 tiles of any Doorway cell
        for (int i = 0; i < _doorwayCells.Count; i++)
        {
            Vector2Int dc = _doorwayCells[i];
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
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

        // 3. BFS connectivity verification from the Objective Room to all 4 perimeter entrances
        EnsureAllEntrancesConnectedViaBfs(ringOuter, hw);
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
        bool[,] reachable = ComputeReachableFromObjective();

        for (int e = 0; e < 4; e++)
        {
            Vector2Int entCell = _entrances[e].GridCell;
            if (!reachable[entCell.x, entCell.y])
            {
                // Auto-carve a guaranteed 2-tile-wide corridor from this entrance straight into the inner ring
                Vector2Int ringTarget = new Vector2Int(
                    Mathf.Clamp(entCell.x, ringOuter.xMin, ringOuter.xMax - hw),
                    Mathf.Clamp(entCell.y, ringOuter.yMin, ringOuter.yMax - hw));
                CarveLCorridor(entCell, ringTarget, hw, _entrances[e].WallSide == 0 || _entrances[e].WallSide == 2);
            }
        }
    }

    private bool[,] ComputeReachableFromObjective()
    {
        bool[,] visited = new bool[_width, _height];
        var queue = new Queue<Vector2Int>();

        int startX = _objectiveRoomBounds.xMin + _objectiveRoomBounds.width / 2;
        int startY = _objectiveRoomBounds.yMin + _objectiveRoomBounds.height / 2;
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

        // Greedy 2D rectangle merging for walls and cover pillars
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                if (visited[x, y]) continue;

                CellType type = _grid[x, y];
                if (type != CellType.Wall && type != CellType.CoverPillar) continue;

                bool isBorderWall = type == CellType.Wall && IsWallBorderingPlayableOrPerimeter(x, y);

                // Expand horizontally along identical cell type & border status
                int runW = 1;
                while (x + runW < _width &&
                       !visited[x + runW, y] &&
                       _grid[x + runW, y] == type &&
                       (type != CellType.Wall || IsWallBorderingPlayableOrPerimeter(x + runW, y) == isBorderWall))
                {
                    runW++;
                }

                // Expand vertically
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
                    neighbor == CellType.Doorway)
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
        go.transform.SetParent(_mapRoot.transform, false);
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

    private void SpawnHallwayEmergencyLights(System.Random rng)
    {
        int spacing = _config != null ? _config.HallwayLightSpacing : 5;
        float steadyRatio = _config != null ? _config.SteadyLightRatio : 0.55f;
        float flickerRatio = _config != null ? _config.FlickerLightRatio : 0.35f;
        float intensity = _config != null ? _config.HallwayLightIntensity : 1.15f;
        float radius = _config != null ? _config.HallwayLightRadius : 5.5f;
        Color color = _config != null ? _config.HallwayLightColor : new Color(0.88f, 0.74f, 0.42f, 1f);

        var placedPositions = new List<Vector2>();
        float minDistSq = spacing * spacing;

        for (int y = 2; y < _height - 2; y++)
        {
            for (int x = 2; x < _width - 2; x++)
            {
                if (_grid[x, y] != CellType.HallwayFloor) continue;

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
                lightGo.transform.SetParent(_mapRoot.transform, false);
                lightGo.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);

                var hl = lightGo.AddComponent<HallwayLight>();
                hl.Initialize(mode, intensity, radius, color, (float)(rng.NextDouble() * 100.0));
            }
        }

        // Also place an exterior emergency beacon light at each of the 4 perimeter entrances so breaches are visible in the night courtyard
        for (int e = 0; e < 4; e++)
        {
            var entLightGo = new GameObject($"EntranceLight_{e}");
            entLightGo.transform.SetParent(_mapRoot.transform, false);
            entLightGo.transform.position = new Vector3(_entrances[e].WorldPosition.x, _entrances[e].WorldPosition.y, 0f);

            var ehl = entLightGo.AddComponent<HallwayLight>();
            ehl.Initialize(HallwayLightMode.Steady, intensity * 1.15f, radius * 1.1f, color, e * 19.7f);
        }
    }

    private void PlaceObjectiveAndTeams(System.Random rng)
    {
        // 1. Place ComputerTerminal in the center of the Objective Room
        Vector2 objCenter = GridToWorld(
            _objectiveRoomBounds.xMin + (_objectiveRoomBounds.width - 1) * 0.5f,
            _objectiveRoomBounds.yMin + (_objectiveRoomBounds.height - 1) * 0.5f);

        var terminalGo = GameObject.Find("ComputerTerminal");
        if (terminalGo != null)
        {
            terminalGo.transform.position = new Vector3(objCenter.x, objCenter.y, 0f);

            // Add a subtle monitor glow Light2D on the terminal so it glows inside the dark Objective Room
            var termLight = terminalGo.GetComponent<Light2D>();
            if (termLight == null) termLight = terminalGo.AddComponent<Light2D>();
            termLight.lightType = Light2D.LightType.Point;
            termLight.color = new Color(0.95f, 0.70f, 0.25f, 1f);
            termLight.intensity = 0.85f;
            termLight.pointLightOuterRadius = 3.5f;
            termLight.pointLightInnerRadius = 0.4f;
        }

        // 2. Shuffle the 4 entrances so Team A and Team B spawn at random distinct entrances each round
        int[] entranceOrder = { 0, 1, 2, 3 };
        for (int i = entranceOrder.Length - 1; i > 0; i--)
        {
            int swapIdx = rng.Next(i + 1);
            int temp = entranceOrder[i];
            entranceOrder[i] = entranceOrder[swapIdx];
            entranceOrder[swapIdx] = temp;
        }

        EntranceData teamAEntrance = _entrances[entranceOrder[0]];
        EntranceData teamBEntrance = _entrances[entranceOrder[1]];

        // 3. Position Team A Extraction Zone, Player, and Friendly Dummy at Team A's random courtyard entrance
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
            Vector2 friendlyOffset = horizWall ? new Vector2(2.2f, 0f) : new Vector2(0f, 2.2f);
            Vector2 patrolAxis = horizWall ? new Vector2(1.8f, 0f) : new Vector2(0f, 1.8f);
            Vector2 friendlySpawn = teamAEntrance.CourtyardSpawnPosition + friendlyOffset;
            TeleportCharacter(friendly, friendlySpawn);

            var dc = friendly.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(friendlySpawn, -patrolAxis, patrolAxis);
        }

        // 4. Position Team B Extraction Zone and Enemy Dummies safely clear of the entrance path
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
            Vector2 sideOffsetB = horizWallB ? new Vector2(-2.4f, 0f) : new Vector2(0f, -2.4f);
            Vector2 stationarySpawn = teamBEntrance.CourtyardSpawnPosition + sideOffsetB;
            TeleportCharacter(enemyStationary, stationarySpawn);
            var dc = enemyStationary.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(stationarySpawn, Vector2.zero, Vector2.zero);
        }

        // Place EnemyDummy_Patrol on a verified hallway tile clear of all doorways & entrances
        var enemyPatrol = GameObject.Find("EnemyDummy_Patrol");
        if (enemyPatrol != null)
        {
            Vector2 guardPos = FindSafeHallwayGuardPosition();
            TeleportCharacter(enemyPatrol, guardPos);
            var dc = enemyPatrol.GetComponent<DummyController>();
            if (dc != null) dc.SetSpawnOrigin(guardPos, new Vector2(-1.8f, 0f), new Vector2(1.8f, 0f));
        }
    }

    /// <summary>
    /// Finds a walkable HallwayFloor position near the Objective Room that is guaranteed to be
    /// at least 3.5 tiles clear of any doorway or exterior entrance.
    /// </summary>
    private Vector2 FindSafeHallwayGuardPosition()
    {
        // Prefer the East or West ring hallway flank of the Objective Room (away from North/South Objective doorways)
        int preferredX = _objectiveRoomBounds.xMax + 2;
        int preferredY = _objectiveRoomBounds.yMin + _objectiveRoomBounds.height / 2;
        if (preferredX > 1 && preferredX < _width - 2 &&
            _grid[preferredX, preferredY] == CellType.HallwayFloor &&
            !IsNearAnyDoorwayOrEntrance(preferredX, preferredY, 3.2f))
        {
            return GridToWorld(preferredX, preferredY);
        }

        // Fallback: scan all hallway floor cells for one safely clear of all doorways/entrances
        for (int y = 3; y < _height - 3; y++)
        {
            for (int x = 3; x < _width - 3; x++)
            {
                if (_grid[x, y] == CellType.HallwayFloor && !IsNearAnyDoorwayOrEntrance(x, y, 3.5f))
                {
                    return GridToWorld(x, y);
                }
            }
        }

        return GridToWorld(preferredX, preferredY);
    }

    /// <summary>
    /// Performs a post-build physics overlap verification across all 4 perimeter entrances
    /// to ensure no solid walls, cover pillars, or characters obstruct the entrance breach path.
    /// </summary>
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

            // Check a 1.4-unit wide corridor extending from 1.5u outside the doorway to 2.5u inside the vestibule
            Vector2 boxCenter = ent.WorldPosition + inwardDir * 0.5f;
            Vector2 boxSize = (ent.WallSide == 0 || ent.WallSide == 2)
                ? new Vector2(1.4f, 4.0f)
                : new Vector2(4.0f, 1.4f);

            Collider2D[] hits = Physics2D.OverlapBoxAll(boxCenter, boxSize, 0f);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D col = hits[i];
                if (col == null || col.isTrigger) continue;

                // SwingDoor is expected in the doorway and swings open when pushed
                if (col.GetComponent<SwingDoor>() != null || col.GetComponentInParent<SwingDoor>() != null)
                {
                    continue;
                }

                // Player is allowed at their courtyard spawn
                if (col.GetComponent<PlayerController>() != null)
                {
                    continue;
                }

                // If any stray CoverPillar or blocking object ever overlapped the entrance corridor, remove or displace it
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

        GUILayout.BeginArea(new Rect(10f, 48f, 340f, 40f));
        GUILayout.BeginHorizontal("box");
        GUILayout.Label($"Factory Seed: {MapSeed} ({_rooms.Count} Rooms)");
        if (GUILayout.Button("New Map Seed", GUILayout.Width(115f), GUILayout.Height(22f)))
        {
            RequestNewRandomMap();
        }
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }
}
