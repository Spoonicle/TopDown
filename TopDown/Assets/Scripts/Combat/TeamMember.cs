using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Network-serializable snapshot of a character's team identity and color.
/// Allows any <see cref="TeamData"/> asset to sync across clients without hardcoded enums.
/// </summary>
public struct NetworkTeamState : INetworkSerializable, IEquatable<NetworkTeamState>
{
    /// <summary>Display name of the team (used as unique team identifier across the network).</summary>
    public FixedString64Bytes TeamName;

    /// <summary>Color tint of the team.</summary>
    public Color TeamColor;

    /// <summary>Serializes team state across the network.</summary>
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref TeamName);
        serializer.SerializeValue(ref TeamColor);
    }

    /// <summary>Checks equality between two network team states.</summary>
    public bool Equals(NetworkTeamState other)
    {
        return TeamName.Equals(other.TeamName) && TeamColor.Equals(other.TeamColor);
    }
}

/// <summary>
/// Networked component that assigns a character to a <see cref="TeamData"/> asset,
/// syncs team affiliation and color across all clients, and enforces the dynamic rule:
/// you cannot shoot/damage your own team, but can shoot all other teams.
/// </summary>
public class TeamMember : NetworkBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Team Settings")]
    [Tooltip("The TeamData asset this character belongs to. Assign any team asset.")]
    [SerializeField] private TeamData _team;

    [Tooltip("If true, automatically tints the character's root SpriteRenderer to the team color.")]
    [SerializeField] private bool _applyTeamColorOnStart = true;

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<NetworkTeamState> _netTeamState = new NetworkVariable<NetworkTeamState>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>The local TeamData asset assigned to this character.</summary>
    public TeamData CurrentTeam => _team;

    /// <summary>The networked or local name of this character's team.</summary>
    public string TeamName
    {
        get
        {
            if (IsSpawned && _netTeamState.Value.TeamName.Length > 0)
            {
                return _netTeamState.Value.TeamName.ToString();
            }
            return _team != null ? _team.TeamName : string.Empty;
        }
    }

    /// <summary>Returns the synced team color (or local TeamData color if not yet spawned).</summary>
    public Color TeamColor
    {
        get
        {
            if (IsSpawned && _netTeamState.Value.TeamName.Length > 0)
            {
                return _netTeamState.Value.TeamColor;
            }
            return _team != null ? _team.TeamColor : Color.white;
        }
    }

    /// <summary>
    /// Sets the character's team at runtime (server-authoritative when networked)
    /// and updates the sprite color.
    /// </summary>
    /// <param name="newTeam">The TeamData asset to assign.</param>
    /// <param name="updateColor">Whether to immediately apply the team color to the SpriteRenderer.</param>
    public void SetTeam(TeamData newTeam, bool updateColor = true)
    {
        _team = newTeam;

        if (IsSpawned && IsServer && _team != null)
        {
            _netTeamState.Value = new NetworkTeamState
            {
                TeamName = new FixedString64Bytes(_team.TeamName),
                TeamColor = _team.TeamColor
            };
        }

        if (updateColor)
        {
            ApplyTeamColor();
        }
    }

    /// <summary>
    /// Returns true if <paramref name="other"/> belongs to the exact same team.
    /// Works seamlessly both offline and across networked clients.
    /// </summary>
    public bool IsTeammate(TeamMember other)
    {
        if (other == null) return false;

        string myTeam = TeamName;
        string otherTeam = other.TeamName;

        if (!string.IsNullOrEmpty(myTeam) && !string.IsNullOrEmpty(otherTeam))
        {
            return string.Equals(myTeam, otherTeam, StringComparison.Ordinal);
        }

        return _team != null && other._team == _team;
    }

    /// <summary>
    /// Returns true if <paramref name="other"/> belongs to any other team.
    /// </summary>
    public bool IsEnemy(TeamMember other)
    {
        return !IsTeammate(other);
    }

    /// <summary>
    /// Dynamic team rule: a character cannot shoot or damage their own team,
    /// but CAN shoot and damage all other teams (regardless of how many teams exist).
    /// </summary>
    public static bool CanDamage(TeamMember attacker, TeamMember target)
    {
        if (attacker == null || target == null) return true;
        return !attacker.IsTeammate(target);
    }

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Start()
    {
        if (_applyTeamColorOnStart)
        {
            ApplyTeamColor();
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _netTeamState.OnValueChanged += OnTeamStateChanged;

        if (IsServer && _team != null)
        {
            _netTeamState.Value = new NetworkTeamState
            {
                TeamName = new FixedString64Bytes(_team.TeamName),
                TeamColor = _team.TeamColor
            };
        }

        if (_applyTeamColorOnStart)
        {
            ApplyTeamColor();
        }
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netTeamState.OnValueChanged -= OnTeamStateChanged;
        base.OnNetworkDespawn();
    }

    private void OnTeamStateChanged(NetworkTeamState previous, NetworkTeamState current)
    {
        if (_applyTeamColorOnStart)
        {
            ApplyTeamColor();
        }
    }

    /// <summary>
    /// Applies the current team color to the root <see cref="SpriteRenderer"/> if present.
    /// </summary>
    public void ApplyTeamColor()
    {
        Color col = TeamColor;
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.color = col;
        }
    }
}
