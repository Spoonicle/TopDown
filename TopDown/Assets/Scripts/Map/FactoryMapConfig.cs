using UnityEngine;

/// <summary>
/// ScriptableObject configuration for the procedural cement factory generator.
/// Controls building dimensions, room counts, hallway widths, 4 perimeter entrances,
/// interior cover, swinging doors, and emergency hallway lighting.
/// Create via Assets → Create → TopDown → Factory Map Config.
/// </summary>
[CreateAssetMenu(fileName = "FactoryMapConfig", menuName = "TopDown/Factory Map Config")]
public class FactoryMapConfig : ScriptableObject
{
    // ───────────────────────────── Building Footprint ───────────────────────

    [Header("Building Dimensions")]
    [Tooltip("If true, width and height are rolled randomly per seed within the min/max ranges below. "
           + "If false, the fixed Width and Height fields are used.")]
    [SerializeField] private bool _useRandomAspectRatio = true;

    [Tooltip("Width of the concrete factory building in tiles when NOT using random aspect ratio.")]
    [Range(24, 160)]
    [SerializeField] private int _buildingWidth = 108;

    [Tooltip("Height of the concrete factory building in tiles when NOT using random aspect ratio.")]
    [Range(24, 160)]
    [SerializeField] private int _buildingHeight = 108;

    [Tooltip("Minimum building width in tiles when using random aspect ratio.")]
    [Range(60, 160)]
    [SerializeField] private int _buildingWidthMin = 80;

    [Tooltip("Maximum building width in tiles when using random aspect ratio.")]
    [Range(60, 200)]
    [SerializeField] private int _buildingWidthMax = 140;

    [Tooltip("Minimum building height in tiles when using random aspect ratio.")]
    [Range(60, 160)]
    [SerializeField] private int _buildingHeightMin = 70;

    [Tooltip("Maximum building height in tiles when using random aspect ratio.")]
    [Range(60, 200)]
    [SerializeField] private int _buildingHeightMax = 130;

    [Tooltip("Width of the exterior courtyard around the factory where teams spawn and extract.")]
    [Range(5, 30)]
    [SerializeField] private int _courtyardMargin = 14;

    [Tooltip("Minimum distance in tiles from any building corner for the 4 shifted perimeter entrances.")]
    [Range(4, 36)]
    [SerializeField] private int _entranceCornerMargin = 18;

    // ───────────────────────────── Rooms & Hallways ─────────────────────────

    [Header("Rooms & Hallways")]
    [Tooltip("Width and height in tiles of the central Objective Room.")]
    [Range(6, 36)]
    [SerializeField] private int _objectiveRoomSize = 24;

    [Tooltip("Target total number of rooms inside the factory building (including the Objective Room).")]
    [Range(5, 30)]
    [SerializeField] private int _targetRoomCount = 18;

    [Tooltip("Minimum interior room width/height in tiles.")]
    [Range(4, 24)]
    [SerializeField] private int _minRoomSize = 11;

    [Tooltip("Maximum interior room width/height in tiles.")]
    [Range(6, 36)]
    [SerializeField] private int _maxRoomSize = 18;

    [Tooltip("Width of concrete hallways in tiles.")]
    [Range(2, 10)]
    [SerializeField] private int _hallwayWidth = 6;

    // ───────────────────────────── Doors & Cover ────────────────────────────

    [Header("Doors & Interior Cover")]
    [Tooltip("Probability (0–1) that a room doorway spawns a physics-pushed swinging door.")]
    [Range(0f, 1f)]
    [SerializeField] private float _doorSpawnChance = 1f;

    [Tooltip("Minimum number of concrete cover pillars/crates spawned inside each room.")]
    [Range(0, 8)]
    [SerializeField] private int _minCoverPerRoom = 2;

    [Tooltip("Maximum number of concrete cover pillars/crates spawned inside each room.")]
    [Range(0, 10)]
    [SerializeField] private int _maxCoverPerRoom = 4;

    // ───────────────────────────── Lighting & Darkness ──────────────────────

    [Header("Lighting & Atmosphere")]
    [Tooltip("Ambient Global Light 2D intensity (low value keeps rooms dark).")]
    [Range(0.02f, 0.4f)]
    [SerializeField] private float _ambientLightIntensity = 0.08f;

    [Tooltip("Spacing in tiles between overhead emergency lights along hallways.")]
    [Range(3, 24)]
    [SerializeField] private int _hallwayLightSpacing = 12;

    [Tooltip("Probability (0–1) that a hallway light is steady on (remaining lights flicker or are broken).")]
    [Range(0f, 1f)]
    [SerializeField] private float _steadyLightRatio = 0.55f;

    [Tooltip("Probability (0–1) that a hallway light flickers intermittently.")]
    [Range(0f, 1f)]
    [SerializeField] private float _flickerLightRatio = 0.35f;

    [Tooltip("Base intensity of hallway emergency point lights.")]
    [Range(0.3f, 3f)]
    [SerializeField] private float _hallwayLightIntensity = 1.2f;

    [Tooltip("Outer radius in world units of hallway emergency point lights.")]
    [Range(2f, 24f)]
    [SerializeField] private float _hallwayLightRadius = 13f;

    // ───────────────────────────── Palette Colors ───────────────────────────

    [Header("Factory Palette (GDD Section 6)")]
    [Tooltip("Concrete wall color.")]
    [SerializeField] private Color _wallColor = new Color(0.36f, 0.37f, 0.38f, 1f);

    [Tooltip("Interior cover pillar/crate color.")]
    [SerializeField] private Color _coverColor = new Color(0.28f, 0.29f, 0.31f, 1f);

    [Tooltip("Swinging industrial door color.")]
    [SerializeField] private Color _doorColor = new Color(0.45f, 0.38f, 0.32f, 1f);

    [Tooltip("Warm yellow/amber color for hallway emergency lights (#c4a35a).")]
    [SerializeField] private Color _hallwayLightColor = new Color(0.88f, 0.74f, 0.42f, 1f);

    // ───────────────────────────── Floor Tilemapping ────────────────────────

    [Header("Floor Tilemapping")]
    [Tooltip("Floor tiles randomly selected to tile the rooms and hallways.")]
    [SerializeField] private UnityEngine.Tilemaps.TileBase[] _floorTiles;

    // ───────────────────────────── Public Accessors ─────────────────────────

    /// <summary>Floor tiles randomly selected to tile the rooms and hallways.</summary>
    public UnityEngine.Tilemaps.TileBase[] FloorTiles => _floorTiles;

    /// <summary>If true, building dimensions are rolled per seed within the min/max ranges.</summary>
    public bool UseRandomAspectRatio => _useRandomAspectRatio;

    /// <summary>Fixed building width in tiles (used when UseRandomAspectRatio is false).</summary>
    public int BuildingWidth => _buildingWidth;

    /// <summary>Fixed building height in tiles (used when UseRandomAspectRatio is false).</summary>
    public int BuildingHeight => _buildingHeight;

    /// <summary>Minimum building width when using random aspect ratio.</summary>
    public int BuildingWidthMin => _buildingWidthMin;

    /// <summary>Maximum building width when using random aspect ratio.</summary>
    public int BuildingWidthMax => _buildingWidthMax;

    /// <summary>Minimum building height when using random aspect ratio.</summary>
    public int BuildingHeightMin => _buildingHeightMin;

    /// <summary>Maximum building height when using random aspect ratio.</summary>
    public int BuildingHeightMax => _buildingHeightMax;

    /// <summary>Outer courtyard margin in tiles.</summary>
    public int CourtyardMargin => _courtyardMargin;

    /// <summary>Minimum corner spacing for the 4 shifted perimeter entrances.</summary>
    public int EntranceCornerMargin => _entranceCornerMargin;

    /// <summary>Width/height of the central Objective Room in tiles.</summary>
    public int ObjectiveRoomSize => _objectiveRoomSize;

    /// <summary>Target number of interior rooms to generate.</summary>
    public int TargetRoomCount => _targetRoomCount;

    /// <summary>Minimum interior room width/height in tiles.</summary>
    public int MinRoomSize => _minRoomSize;

    /// <summary>Maximum interior room width/height in tiles.</summary>
    public int MaxRoomSize => _maxRoomSize;

    /// <summary>Width of concrete hallways in tiles.</summary>
    public int HallwayWidth => _hallwayWidth;

    /// <summary>Probability of spawning a swinging door in a room doorway.</summary>
    public float DoorSpawnChance => _doorSpawnChance;

    /// <summary>Minimum cover pillars per room.</summary>
    public int MinCoverPerRoom => _minCoverPerRoom;

    /// <summary>Maximum cover pillars per room.</summary>
    public int MaxCoverPerRoom => _maxCoverPerRoom;

    /// <summary>Ambient Global Light 2D intensity.</summary>
    public float AmbientLightIntensity => _ambientLightIntensity;

    /// <summary>Tile spacing between hallway lights.</summary>
    public int HallwayLightSpacing => _hallwayLightSpacing;

    /// <summary>Probability that a hallway light is steady on.</summary>
    public float SteadyLightRatio => _steadyLightRatio;

    /// <summary>Probability that a hallway light flickers.</summary>
    public float FlickerLightRatio => _flickerLightRatio;

    /// <summary>Intensity of hallway emergency lights.</summary>
    public float HallwayLightIntensity => _hallwayLightIntensity;

    /// <summary>Outer radius of hallway emergency lights.</summary>
    public float HallwayLightRadius => _hallwayLightRadius;

    /// <summary>Concrete wall color.</summary>
    public Color WallColor => _wallColor;

    /// <summary>Interior cover pillar/crate color.</summary>
    public Color CoverColor => _coverColor;

    /// <summary>Swinging industrial door color.</summary>
    public Color DoorColor => _doorColor;

    /// <summary>Warm hallway light color.</summary>
    public Color HallwayLightColor => _hallwayLightColor;
}
