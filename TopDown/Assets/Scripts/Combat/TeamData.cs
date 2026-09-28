using UnityEngine;

/// <summary>
/// ScriptableObject defining a team/faction.
/// Create as many teams as needed via Assets → Create → TopDown → Team Data
/// without modifying any code.
/// </summary>
[CreateAssetMenu(fileName = "NewTeam", menuName = "TopDown/Team Data")]
public class TeamData : ScriptableObject
{
    [Header("Team Identity")]
    [Tooltip("Display name of the team (e.g., 'Team A', 'Team B', 'Team C').")]
    [SerializeField] private string _teamName = "New Team";

    [Tooltip("Color tint applied to members of this team.")]
    [SerializeField] private Color _teamColor = new Color(0.290f, 0.416f, 0.541f, 1f);

    /// <summary>Display name of the team.</summary>
    public string TeamName => _teamName;

    /// <summary>Color tint applied to members of this team.</summary>
    public Color TeamColor => _teamColor;
}
