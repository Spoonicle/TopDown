using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked extraction/spawn zone associated with a <see cref="TeamData"/> asset.
/// When a teammate carrying downloaded terminal data enters this zone, their team
/// completes the objective and wins the round.
/// </summary>
public class ExtractionZone : NetworkBehaviour
{
    // ────────────────────────────── Static Registry ────────────────────────

    private static readonly List<ExtractionZone> ActiveZones = new List<ExtractionZone>();

    /// <summary>
    /// Finds the extraction zone matching the given <see cref="TeamMember"/> without scene searches.
    /// </summary>
    public static ExtractionZone FindZoneForTeam(TeamMember member)
    {
        if (member == null) return null;

        for (int i = 0; i < ActiveZones.Count; i++)
        {
            var zone = ActiveZones[i];
            if (zone != null && zone.MatchesTeam(member))
            {
                return zone;
            }
        }
        return null;
    }

    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Zone Settings")]
    [Tooltip("The team that spawns and extracts at this zone.")]
    [SerializeField] private TeamData _team;

    [Tooltip("Objective configuration asset (defines extraction radius).")]
    [SerializeField] private ObjectiveConfig _config;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<bool> _netRoundExtracted = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<FixedString64Bytes> _netWinningTeamName = new NetworkVariable<FixedString64Bytes>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private bool _localRoundExtracted;
    private string _localWinningTeam = string.Empty;

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired when a team successfully extracts the objective data at this zone.</summary>
    public event Action<string> OnTeamExtracted;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The TeamData assigned to this extraction zone.</summary>
    public TeamData ZoneTeam => _team;

    /// <summary>Radius of the extraction zone in world units.</summary>
    public float Radius => _config != null ? _config.ExtractionRadius : 2.5f;

    /// <summary>True once a carrier has extracted the data here.</summary>
    public bool IsExtracted => IsSpawned ? _netRoundExtracted.Value : _localRoundExtracted;

    /// <summary>Name of the team that extracted the objective here.</summary>
    public string WinningTeamName => IsSpawned ? _netWinningTeamName.Value.ToString() : _localWinningTeam;

    /// <summary>
    /// Returns true if the given <see cref="TeamMember"/> belongs to this extraction zone's team.
    /// </summary>
    public bool MatchesTeam(TeamMember member)
    {
        if (member == null || _team == null) return false;
        return string.Equals(member.TeamName, _team.TeamName, StringComparison.Ordinal)
               || member.CurrentTeam == _team;
    }

    // ────────────────────────────── Cached Refs ────────────────────────────

    private SpriteRenderer _zoneRenderer;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        SetupZoneVisual();
    }

    private void OnEnable()
    {
        if (!ActiveZones.Contains(this))
        {
            ActiveZones.Add(this);
        }
    }

    private void OnDisable()
    {
        ActiveZones.Remove(this);
    }

    private void Start()
    {
        UpdateZoneVisual();
    }

    private void Update()
    {
        bool hasServerAuth = !IsSpawned || IsServer;
        if (!hasServerAuth || IsExtracted) return;

        CheckForCarrierInZoneServer();
    }

    // ──────────────────────────── Server Logic ─────────────────────────────

    private void CheckForCarrierInZoneServer()
    {
        float radiusSq = Radius * Radius;
        Vector2 zonePos = transform.position;

        // Check all ObjectiveCarriers in the scene
        var carriers = FindObjectsByType<ObjectiveCarrier>(FindObjectsSortMode.None);
        for (int i = 0; i < carriers.Length; i++)
        {
            var carrier = carriers[i];
            if (carrier == null || !carrier.IsCarryingData) continue;

            var health = carrier.GetComponent<Health>();
            if (health != null && health.IsDead) continue;

            // Must extract at your own team's spawn/extraction zone
            if (!MatchesTeam(carrier.CarrierTeam)) continue;

            float distSq = ((Vector2)carrier.transform.position - zonePos).sqrMagnitude;
            if (distSq <= radiusSq)
            {
                CompleteExtractionServer(carrier);
                break;
            }
        }
    }

    private void CompleteExtractionServer(ObjectiveCarrier carrier)
    {
        string teamName = _team != null ? _team.TeamName : "Team";
        carrier.SetCarryingDataServer(false);

        if (IsSpawned)
        {
            _netWinningTeamName.Value = new FixedString64Bytes(teamName);
            _netRoundExtracted.Value = true;
            NotifyExtractionCompleteRpc(teamName);
        }
        else
        {
            _localWinningTeam = teamName;
            _localRoundExtracted = true;
            OnTeamExtracted?.Invoke(teamName);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void NotifyExtractionCompleteRpc(string teamName)
    {
        OnTeamExtracted?.Invoke(teamName);
    }

    /// <summary>
    /// Resets the extraction state for another test round (callable from client or server).
    /// </summary>
    public void RequestResetObjective()
    {
        if (IsSpawned)
        {
            ResetObjectiveServerRpc();
        }
        else
        {
            ResetObjectiveLocal();
        }
    }

    [Rpc(SendTo.Server)]
    private void ResetObjectiveServerRpc()
    {
        _netRoundExtracted.Value = false;
        _netWinningTeamName.Value = default;

        var terminal = FindAnyObjectByType<ComputerTerminalObjective>();
        if (terminal != null)
        {
            terminal.ResetTerminalServer();
        }
    }

    private void ResetObjectiveLocal()
    {
        _localRoundExtracted = false;
        _localWinningTeam = string.Empty;

        var terminal = FindAnyObjectByType<ComputerTerminalObjective>();
        if (terminal != null)
        {
            terminal.ResetTerminalServer();
        }
    }

    // ──────────────────────────── Visuals & UI ─────────────────────────────

    private static Sprite _cachedRingSprite;

    private void SetupZoneVisual()
    {
        if (_cachedRingSprite == null)
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size * 0.5f;
            float outerR = center - 1f;
            float innerR = outerR - 5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    if (d <= outerR && d >= innerR)
                    {
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0.75f));
                    }
                    else if (d < innerR)
                    {
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0.12f));
                    }
                    else
                    {
                        tex.SetPixel(x, y, Color.clear);
                    }
                }
            }
            tex.Apply();
            _cachedRingSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
        }

        _zoneRenderer = GetComponent<SpriteRenderer>();
        if (_zoneRenderer == null)
        {
            _zoneRenderer = gameObject.AddComponent<SpriteRenderer>();
        }

        _zoneRenderer.sprite = _cachedRingSprite;
        _zoneRenderer.sortingOrder = -8; // Above FloorGrid (-10), below blood (-5) and players (10)
    }

    private void UpdateZoneVisual()
    {
        if (_zoneRenderer == null) return;

        Color col = _team != null ? _team.TeamColor : new Color(0.3f, 0.8f, 0.5f, 1f);
        _zoneRenderer.color = new Color(col.r, col.g, col.b, 0.65f);

        float diameter = Radius * 2f;
        transform.localScale = new Vector3(diameter * 0.5f, diameter * 0.5f, 1f);
    }

    private void OnGUI()
    {
        if (!IsExtracted) return;

        float width = 460f;
        float height = 110f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, 60f, width, height);

        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Space(8f);

        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };
        titleStyle.normal.textColor = new Color(0.25f, 0.95f, 0.55f);

        var subStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14
        };

        GUILayout.Label($"DATA EXTRACTED — {WinningTeamName.ToUpper()} WINS!", titleStyle);
        GUILayout.Label("Objective data was successfully evacuated to the spawn zone.", subStyle);
        GUILayout.Space(6f);

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reset Objective (Play Again)", GUILayout.Width(220f), GUILayout.Height(28f)))
        {
            RequestResetObjective();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
}
