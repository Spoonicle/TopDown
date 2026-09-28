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
    [Tooltip("Width of the concrete factory building in tiles (1 tile = 1 world unit).")]
    [Range(24, 64)]
    [SerializeField] private int _buildingWidth = 36;

    [Tooltip("Height of the concrete factory building in tiles (1 tile = 1 world unit).")]
    [Range(24, 64)]
    [SerializeField] private int _buildingHeight = 36;

    [Tooltip("Width of the exterior courtyard around the factory where teams spawn and extract.")]
    [Range(5, 16)]
    [SerializeField] private int _courtyardMargin = 6;

    [Tooltip("Minimum distance in tiles from any building corner for the 4 shifted perimeter entrances.")]
    [Range(4, 12)]
    [SerializeField] private int _entranceCornerMargin = 6;

    // ───────────────────────────── Rooms & Hallways ─────────────────────────

    [Header("Rooms & Hallways")]
    [Tooltip("Width and height in tiles of the central Objective Room.")]
    [Range(6, 12)]
    [SerializeField] private int _objectiveRoomSize = 8;

    [Tooltip("Target number of additional interior rooms to carve inside the factory.")]
    [Range(5, 16)]
    [SerializeField] private int _targetRoomCount = 9;

    [Tooltip("Minimum interior room width/height in tiles.")]
    [Range(4, 8)]
    [SerializeField] private int _minRoomSize = 5;

    [Tooltip("Maximum interior room width/height in tiles.")]
    [Range(6, 12)]
    [SerializeField] private int _maxRoomSize = 8;

    [Tooltip("Width of concrete hallways in tiles.")]
    [Range(2, 4)]
    [SerializeField] private int _hallwayWidth = 2;

    // ───────────────────────────── Doors & Cover ────────────────────────────

    [Header("Doors & Interior Cover")]
    [Tooltip("Probability (0–1) that a room doorway spawns a physics-pushed swinging door.")]
    [Range(0f, 1f)]
    [SerializeField] private float _doorSpawnChance = 0.9f;

    [Tooltip("Minimum number of concrete cover pillars/crates spawned inside each room.")]
    [Range(0, 4)]
    [SerializeField] private int _minCoverPerRoom = 1;

    [Tooltip("Maximum number of concrete cover pillars/crates spawned inside each room.")]
    [Range(0, 5)]
    [SerializeField] private int _maxCoverPerRoom = 2;

    // ───────────────────────────── Lighting & Darkness ──────────────────────

    [Header("Lighting & Atmosphere")]
    [Tooltip("Ambient Global Light 2D intensity (low value keeps rooms dark).")]
    [Range(0.02f, 0.4f)]
    [SerializeField] private float _ambientLightIntensity = 0.08f;

    [Tooltip("Spacing in tiles between overhead emergency lights along hallways.")]
    [Range(3, 10)]
    [SerializeField] private int _hallwayLightSpacing = 5;

    [Tooltip("Probability (0–1) that a hallway light is steady on (remaining lights flicker or are broken).")]
    [Range(0f, 1f)]
    [SerializeField] private float _steadyLightRatio = 0.55f;

    [Tooltip("Probability (0–1) that a hallway light flickers intermittently.")]
    [Range(0f, 1f)]
    [SerializeField] private float _flickerLightRatio = 0.35f;

    [Tooltip("Base intensity of hallway emergency point lights.")]
    [Range(0.3f, 3f)]
    [SerializeField] private float _hallwayLightIntensity = 1.15f;

    [Tooltip("Outer radius in world units of hallway emergency point lights.")]
    [Range(2f, 10f)]
    [SerializeField] private float _hallwayLightRadius = 5.5f;

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

    // ───────────────────────────── Public Accessors ─────────────────────────

    /// <summary>Width of the factory building in tiles.</summary>
    public int BuildingWidth => _buildingWidth;

    /// <summary>Height of the factory building in tiles.</summary>
    public int BuildingHeight => _buildingHeight;

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
