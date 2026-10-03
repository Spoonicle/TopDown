using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Manages local and multiplayer session startup for Netcode for GameObjects.
/// Automatically starts as Host by default for frictionless solo testing in the Editor,
/// spawns player prefabs for joining clients on opposing teams, and provides a compact
/// on-screen Host/Client connection bar.
/// </summary>
[RequireComponent(typeof(NetworkManager))]
public class NetworkBootstrap : MonoBehaviour
{
    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Startup")]
    [Tooltip("If true, automatically starts as Host on Play so solo testing requires zero extra clicks.")]
    [SerializeField] private bool _autoStartHost = true;

    [Tooltip("Whether to display the compact top-left Host/Client connection status bar.")]
    [SerializeField] private bool _showNetworkHud = true;

    [Header("Dynamic Client Spawning")]
    [Tooltip("Player prefab spawned for remote clients who join after the Host.")]
    [SerializeField] private NetworkObject _clientPlayerPrefab;

    [Tooltip("Available TeamData assets assigned round-robin to joining clients.")]
    [SerializeField] private TeamData[] _availableTeams;

    [Tooltip("Spawn position offset for newly joining clients.")]
    [SerializeField] private Vector2 _clientSpawnOrigin = new Vector2(2.5f, -2.5f);

    // ────────────────────────────── Cached Refs ────────────────────────────

    private NetworkManager _networkManager;

    // ──────────────────────────── Unity Callbacks ──────────────────────────

    private void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
    }

    private void Start()
    {
        if (_networkManager == null) return;

        _networkManager.OnClientConnectedCallback += HandleClientConnected;

        if (_autoStartHost && !_networkManager.IsListening)
        {
            _networkManager.StartHost();
        }
    }

    private void OnDestroy()
    {
        if (_networkManager != null)
        {
            _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        }
    }

    // ──────────────────────────── Client Spawning ──────────────────────────

    private void HandleClientConnected(ulong clientId)
    {
        if (_networkManager == null || !_networkManager.IsServer) return;

        // The Host (ServerClientId) already uses the scene-placed Player object.
        if (clientId == NetworkManager.ServerClientId) return;

        // If this joining client already has a PlayerObject, do nothing.
        if (_networkManager.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
        {
            return;
        }

        if (_clientPlayerPrefab == null) return;

        int teamIndex = (_availableTeams != null && _availableTeams.Length > 0)
            ? (int)(clientId % (ulong)_availableTeams.Length)
            : 0;

        Vector3 spawnPos;
        if (FactoryMapGenerator.Instance != null)
        {
            Vector2 teamSpawn = teamIndex == 0 ? FactoryMapGenerator.Instance.TeamASpawnPosition :
                                teamIndex == 1 ? FactoryMapGenerator.Instance.TeamBSpawnPosition :
                                teamIndex == 2 ? FactoryMapGenerator.Instance.TeamCSpawnPosition :
                                FactoryMapGenerator.Instance.TeamASpawnPosition;
            spawnPos = new Vector3(teamSpawn.x + ((clientId % 4) * 0.8f), teamSpawn.y, 0f);
        }
        else
        {
            spawnPos = new Vector3(
                _clientSpawnOrigin.x + (clientId * 1.5f),
                _clientSpawnOrigin.y,
                0f);
        }

        NetworkObject playerInstance = Instantiate(_clientPlayerPrefab, spawnPos, Quaternion.identity);

        // Assign team before/upon spawning (e.g., Client 1 gets Team B)
        var teamMember = playerInstance.GetComponent<TeamMember>();
        if (teamMember != null && _availableTeams != null && _availableTeams.Length > 0)
        {
            teamMember.SetTeam(_availableTeams[teamIndex], true);
        }

        playerInstance.SpawnAsPlayerObject(clientId, true);
    }

    // ──────────────────────────── Minimal HUD ──────────────────────────────

    private void OnGUI()
    {
        if (!_showNetworkHud || _networkManager == null) return;

        const float pad = 10f;
        GUILayout.BeginArea(new Rect(pad, pad, 340f, 90f));
        GUILayout.BeginHorizontal("box");

        if (!_networkManager.IsClient && !_networkManager.IsServer)
        {
            if (GUILayout.Button("Start Host", GUILayout.Height(26f)))
            {
                _networkManager.StartHost();
            }
            if (GUILayout.Button("Join Client", GUILayout.Height(26f)))
            {
                _networkManager.StartClient();
            }
        }
        else
        {
            string mode = _networkManager.IsHost ? "HOST" : (_networkManager.IsServer ? "SERVER" : "CLIENT");
            int count = _networkManager.IsServer ? _networkManager.ConnectedClientsIds.Count : 1;
            GUILayout.Label($"Netcode: {mode} (Players: {count})");

            if (GUILayout.Button("Disconnect", GUILayout.Width(90f), GUILayout.Height(22f)))
            {
                _networkManager.Shutdown();
            }
        }

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }
}
