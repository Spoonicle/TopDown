using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Networked computer terminal objective. Players approach the terminal, press E to enter
/// the hacking stage, and rapidly type random keys on their keyboard to fill the download
/// progress bar. Once 100% is reached, the hacker receives the data via <see cref="ObjectiveCarrier"/>
/// and must evacuate back to their team's <see cref="ExtractionZone"/>.
/// </summary>
public class ComputerTerminalObjective : NetworkBehaviour
{
    // ────────────────────────────── Constants ──────────────────────────────

    private static readonly string[] HackerCodeLines =
    {
        "ssh -i /root/.rsa_key admin@10.24.88.104 -p 443",
        "[AUTH] Handshake established :: TLS_AES_256_GCM_SHA384",
        "mounting /dev/nvme0n1p3 -> /mnt/classified_payload...",
        "bypass_kernel_guard(0x7FFF8402A100, PRIV_ESCALATE);",
        "[OK] Ring-0 memory page unlocked (4096 bytes)",
        "decrypt_stream --cipher chacha20 --iv 0x99A4F1C2",
        "dumping sector 0x0040 -> 0x00FF [################] 100%",
        "inject_shellcode(target_pid: 4102, payload_len: 512);",
        "reading /var/factory/biohazard_manifest_v4.dat...",
        "stripping checksum_guard && routing via local loopback...",
        "packet_Burst(seq=8841, ack=1, window=65535) -> OK",
        "[TRANSFER] Syncing encrypted archive chunks to drive..."
    };

    // ────────────────────────────── Inspector ──────────────────────────────

    [Header("Configuration")]
    [Tooltip("Tunable objective parameters (radius, progress per keystroke, colors).")]
    [SerializeField] private ObjectiveConfig _config;

    [Header("Input Actions")]
    [SerializeField] private InputAction _interactAction = new InputAction(
        "Interact", InputActionType.Button, "<Keyboard>/e");

    [SerializeField] private InputAction _cancelAction = new InputAction(
        "CancelHack", InputActionType.Button);

    // ────────────────────────────── Network State ──────────────────────────

    private readonly NetworkVariable<float> _netProgress = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> _netIsBeingHacked = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<ulong> _netHackerClientId = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> _netIsDownloaded = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Offline fallbacks
    private float _localProgress;
    private bool _localIsBeingHacked;
    private bool _localIsDownloaded;

    // ────────────────────────────── Events ─────────────────────────────────

    /// <summary>Fired whenever hack progress changes (0–1).</summary>
    public event Action<float> OnProgressChanged;

    /// <summary>Fired when the terminal data download reaches 100%.</summary>
    public event Action<ObjectiveCarrier> OnDownloadCompleted;

    // ────────────────────────────── Public API ─────────────────────────────

    /// <summary>Current download progress normalized from 0 to 1.</summary>
    public float Progress => IsSpawned ? _netProgress.Value : _localProgress;

    /// <summary>True if a player is currently connected and hacking this terminal.</summary>
    public bool IsBeingHacked => IsSpawned ? _netIsBeingHacked.Value : _localIsBeingHacked;

    /// <summary>True once the data has been downloaded from this terminal.</summary>
    public bool IsDownloaded => IsSpawned ? _netIsDownloaded.Value : _localIsDownloaded;

    /// <summary>True if the local player on this client is currently in the hacking stage.</summary>
    public bool IsLocalPlayerHacking => _isLocalPlayerHacking;

    /// <summary>Maximum interaction distance in world units.</summary>
    public float InteractionRadius => _config != null ? _config.InteractionRadius : 2.2f;

    // ────────────────────────────── Runtime State ──────────────────────────

    private PlayerController _localPlayer;
    private WeaponHolder _localWeaponHolder;
    private ObjectiveCarrier _localCarrier;
    private bool _isLocalPlayerInRange;
    private bool _isLocalPlayerHacking;
    private int _hackStartFrame;

    // Typing speed telemetry for UI
    private int _totalKeysTypedSession;
    private float _recentKeystrokesPerSec;
    private int _keysTypedInCurrentSecond;
    private float _secondTimer;

    // World visuals
    private SpriteRenderer _screenRenderer;
    private Transform _worldProgressRoot;
    private Transform _worldProgressFill;
    private SpriteRenderer _worldProgressFillRenderer;

    // ──────────────────────────── Unity & Netcode Callbacks ────────────────

    private void Awake()
    {
        if (_cancelAction.bindings.Count == 0)
        {
            _cancelAction.AddBinding("<Keyboard>/escape");
            _cancelAction.AddBinding("<Mouse>/rightButton");
        }

        SetupTerminalVisuals();
    }

    private void Start()
    {
        UpdateTerminalVisuals();
    }

    /// <inheritdoc/>
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _netProgress.OnValueChanged += OnNetProgressChanged;
        _netIsBeingHacked.OnValueChanged += OnNetStateChanged;
        _netIsDownloaded.OnValueChanged += OnNetDownloadedChanged;

        UpdateTerminalVisuals();
    }

    /// <inheritdoc/>
    public override void OnNetworkDespawn()
    {
        _netProgress.OnValueChanged -= OnNetProgressChanged;
        _netIsBeingHacked.OnValueChanged -= OnNetStateChanged;
        _netIsDownloaded.OnValueChanged -= OnNetDownloadedChanged;

        SetLocalHackingLock(false);
        base.OnNetworkDespawn();
    }

    private void OnEnable()
    {
        _interactAction.Enable();
        _cancelAction.Enable();

        _interactAction.performed += OnInteractPerformed;
        _cancelAction.performed += OnCancelPerformed;
    }

    private void OnDisable()
    {
        _interactAction.performed -= OnInteractPerformed;
        _cancelAction.performed -= OnCancelPerformed;

        _interactAction.Disable();
        _cancelAction.Disable();

        SetLocalHackingLock(false);
    }

    private void Update()
    {
        FindLocalPlayerIfNeeded();
        UpdateRangeCheck();

        if (_isLocalPlayerHacking)
        {
            HandleLocalSpeedTyping();
        }

        // Optional server-side progress decay when nobody is hacking
        bool hasServerAuth = !IsSpawned || IsServer;
        if (hasServerAuth && !IsBeingHacked && !IsDownloaded && Progress > 0f)
        {
            float decay = _config != null ? _config.ProgressDecayPerSecond : 0f;
            if (decay > 0f)
            {
                SetProgressServer(Mathf.Max(0f, Progress - decay * Time.deltaTime));
            }
        }
    }

    // ──────────────────────────── Local Player & Input ─────────────────────

    private void FindLocalPlayerIfNeeded()
    {
        if (_localPlayer != null && _localPlayer.HasInputAuthority) return;

        var players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null && players[i].HasInputAuthority)
            {
                _localPlayer = players[i];
                _localWeaponHolder = _localPlayer.GetComponent<WeaponHolder>();
                _localCarrier = _localPlayer.GetComponent<ObjectiveCarrier>();
                break;
            }
        }
    }

    private void UpdateRangeCheck()
    {
        if (_localPlayer == null || _localPlayer.IsDead)
        {
            _isLocalPlayerInRange = false;
            if (_isLocalPlayerHacking)
            {
                StopLocalHacking();
            }
            return;
        }

        float dist = Vector2.Distance(_localPlayer.transform.position, transform.position);
        _isLocalPlayerInRange = dist <= InteractionRadius;

        if (_isLocalPlayerHacking && (!_isLocalPlayerInRange || IsDownloaded))
        {
            StopLocalHacking();
        }
    }

    private void OnInteractPerformed(InputAction.CallbackContext ctx)
    {
        if (IsDownloaded) return;

        // If not currently hacking and in range, start hacking!
        if (!_isLocalPlayerHacking && _isLocalPlayerInRange && _localPlayer != null && !_localPlayer.IsDead)
        {
            // Only allow one player to hack at a time
            if (IsBeingHacked && IsSpawned && _netHackerClientId.Value != NetworkManager.Singleton.LocalClientId)
            {
                return;
            }

            StartLocalHacking();
        }
    }

    private void OnCancelPerformed(InputAction.CallbackContext ctx)
    {
        if (_isLocalPlayerHacking)
        {
            StopLocalHacking();
        }
    }

    private void StartLocalHacking()
    {
        _isLocalPlayerHacking = true;
        _hackStartFrame = Time.frameCount;
        _keysTypedInCurrentSecond = 0;
        _secondTimer = 0f;

        SetLocalHackingLock(true);

        if (IsSpawned)
        {
            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            SetHackingStateServerRpc(true, localId);
        }
        else
        {
            _localIsBeingHacked = true;
            UpdateTerminalVisuals();
        }
    }

    private void StopLocalHacking()
    {
        if (!_isLocalPlayerHacking) return;

        _isLocalPlayerHacking = false;
        _recentKeystrokesPerSec = 0f;
        SetLocalHackingLock(false);

        if (IsSpawned)
        {
            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            SetHackingStateServerRpc(false, localId);
        }
        else
        {
            _localIsBeingHacked = false;
            UpdateTerminalVisuals();
        }
    }

    private void SetLocalHackingLock(bool locked)
    {
        if (_localPlayer != null)
        {
            _localPlayer.IsHacking = locked;
        }

        if (_localWeaponHolder != null)
        {
            _localWeaponHolder.IsInputBlocked = locked;
        }
    }

    // ──────────────────────────── Speed-Typing Mechanic ────────────────────

    private void HandleLocalSpeedTyping()
    {
        // Skip the exact frame where 'E' was pressed to enter the terminal
        if (Time.frameCount <= _hackStartFrame) return;

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        int maxPerFrame = _config != null ? _config.MaxKeystrokesPerFrame : 4;
        int keysPressedThisFrame = 0;

        // Count distinct new key presses this frame across the keyboard
        var allKeys = keyboard.allKeys;
        for (int i = 0; i < allKeys.Count; i++)
        {
            KeyControl key = allKeys[i];
            if (key == null || !key.wasPressedThisFrame) continue;

            // Ignore Escape so canceling doesn't count as typing
            if (key.keyCode == Key.Escape) continue;

            keysPressedThisFrame++;
            if (keysPressedThisFrame >= maxPerFrame)
            {
                break;
            }
        }

        // Update typing speed telemetry (keys per second)
        _secondTimer += Time.deltaTime;
        _keysTypedInCurrentSecond += keysPressedThisFrame;
        if (_secondTimer >= 0.25f)
        {
            float instantRate = _keysTypedInCurrentSecond / _secondTimer;
            _recentKeystrokesPerSec = Mathf.Lerp(_recentKeystrokesPerSec, instantRate, 0.5f);
            _keysTypedInCurrentSecond = 0;
            _secondTimer = 0f;
        }

        if (keysPressedThisFrame > 0)
        {
            _totalKeysTypedSession += keysPressedThisFrame;
            SubmitTypingKeystrokes(keysPressedThisFrame);
        }
    }

    /// <summary>
    /// Submits a count of newly typed keystrokes to advance the hacking progress bar.
    /// Can also be invoked by automated tests.
    /// </summary>
    /// <param name="keystrokeCount">Number of keystrokes typed.</param>
    /// <param name="hackerCarrier">Optional explicit carrier when called from server tests.</param>
    public void SubmitTypingKeystrokes(int keystrokeCount, ObjectiveCarrier hackerCarrier = null)
    {
        if (keystrokeCount <= 0 || IsDownloaded) return;

        float perKey = _config != null ? _config.ProgressPerKeystroke : 0.016f;
        float delta = keystrokeCount * perKey;

        if (IsSpawned)
        {
            ulong clientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
            if (hackerCarrier != null && hackerCarrier.IsSpawned)
            {
                clientId = hackerCarrier.OwnerClientId;
            }
            SubmitProgressDeltaServerRpc(delta, clientId);
        }
        else
        {
            ObjectiveCarrier targetCarrier = hackerCarrier != null ? hackerCarrier : _localCarrier;
            AdvanceProgressAuthoritative(delta, targetCarrier);
        }
    }

    // ──────────────────────────── Server Authority & RPCs ──────────────────

    [Rpc(SendTo.Server)]
    private void SetHackingStateServerRpc(bool isHacking, ulong clientId)
    {
        if (isHacking && !_netIsDownloaded.Value)
        {
            _netIsBeingHacked.Value = true;
            _netHackerClientId.Value = clientId;
        }
        else if (!isHacking && _netHackerClientId.Value == clientId)
        {
            _netIsBeingHacked.Value = false;
            _netHackerClientId.Value = ulong.MaxValue;
        }
    }

    [Rpc(SendTo.Server)]
    private void SubmitProgressDeltaServerRpc(float delta, ulong hackerClientId)
    {
        if (_netIsDownloaded.Value || delta <= 0f) return;

        ObjectiveCarrier carrier = FindCarrierForClient(hackerClientId);
        AdvanceProgressAuthoritative(delta, carrier);
    }

    private void AdvanceProgressAuthoritative(float delta, ObjectiveCarrier carrier)
    {
        float newProgress = Mathf.Clamp01(Progress + delta);
        SetProgressServer(newProgress);

        if (newProgress >= 1f)
        {
            CompleteDownloadServer(carrier);
        }
    }

    private void SetProgressServer(float value)
    {
        if (IsSpawned)
        {
            _netProgress.Value = value;
        }
        else
        {
            _localProgress = value;
            UpdateTerminalVisuals();
            OnProgressChanged?.Invoke(value);
        }
    }

    private void CompleteDownloadServer(ObjectiveCarrier carrier)
    {
        if (IsSpawned)
        {
            _netIsDownloaded.Value = true;
            _netIsBeingHacked.Value = false;
            _netHackerClientId.Value = ulong.MaxValue;
        }
        else
        {
            _localIsDownloaded = true;
            _localIsBeingHacked = false;
            StopLocalHacking();
            UpdateTerminalVisuals();
        }

        if (carrier != null)
        {
            carrier.SetCarryingDataServer(true);
        }

        OnDownloadCompleted?.Invoke(carrier);
    }

    private ObjectiveCarrier FindCarrierForClient(ulong clientId)
    {
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) &&
            client.PlayerObject != null)
        {
            var carrier = client.PlayerObject.GetComponent<ObjectiveCarrier>();
            if (carrier != null) return carrier;
        }

        // Fallback: check scene PlayerController owned by clientId
        var carriers = FindObjectsByType<ObjectiveCarrier>(FindObjectsSortMode.None);
        for (int i = 0; i < carriers.Length; i++)
        {
            if (carriers[i] != null && carriers[i].OwnerClientId == clientId)
            {
                return carriers[i];
            }
        }

        return _localCarrier;
    }

    /// <summary>
    /// Called on the Server if the player carrying the downloaded data dies before extracting,
    /// allowing the terminal to be hacked again.
    /// </summary>
    public void OnCarrierDiedServer()
    {
        ResetTerminalServer();
    }

    /// <summary>
    /// Resets the terminal back to 0% progress and ready-to-hack state (server-authoritative).
    /// </summary>
    public void ResetTerminalServer()
    {
        if (IsSpawned)
        {
            if (!IsServer) return;
            _netProgress.Value = 0f;
            _netIsBeingHacked.Value = false;
            _netIsDownloaded.Value = false;
            _netHackerClientId.Value = ulong.MaxValue;
        }
        else
        {
            _localProgress = 0f;
            _localIsBeingHacked = false;
            _localIsDownloaded = false;
            UpdateTerminalVisuals();
        }
    }

    private void OnNetProgressChanged(float previousValue, float newValue)
    {
        UpdateTerminalVisuals();
        OnProgressChanged?.Invoke(newValue);
    }

    private void OnNetStateChanged(bool previousValue, bool newValue)
    {
        UpdateTerminalVisuals();
    }

    private void OnNetDownloadedChanged(bool previousValue, bool newValue)
    {
        if (newValue && _isLocalPlayerHacking)
        {
            StopLocalHacking();
        }
        UpdateTerminalVisuals();
    }

    // ──────────────────────────── World Visuals ────────────────────────────

    private static Sprite _cachedSquareSprite;

    private void SetupTerminalVisuals()
    {
        if (_cachedSquareSprite == null)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            _cachedSquareSprite = Sprite.Create(tex, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 8f);
        }

        // 1. Desk / Terminal chassis on root
        var baseSr = GetComponent<SpriteRenderer>();
        if (baseSr == null) baseSr = gameObject.AddComponent<SpriteRenderer>();
        baseSr.sprite = _cachedSquareSprite;
        baseSr.color = new Color(0.18f, 0.20f, 0.22f, 1f);
        baseSr.sortingOrder = 5;

        var col = GetComponent<BoxCollider2D>();
        if (col == null)
        {
            col = gameObject.AddComponent<BoxCollider2D>();
            col.size = new Vector2(1f, 1f);
        }

        // 2. Monitor Screen glow child
        var screenTrans = transform.Find("MonitorScreen");
        if (screenTrans == null)
        {
            var screenGo = new GameObject("MonitorScreen");
            screenGo.transform.SetParent(transform, false);
            screenGo.transform.localPosition = new Vector3(0f, 0.08f, 0f);
            screenGo.transform.localScale = new Vector3(0.72f, 0.48f, 1f);
            screenTrans = screenGo.transform;
        }

        _screenRenderer = screenTrans.GetComponent<SpriteRenderer>();
        if (_screenRenderer == null) _screenRenderer = screenTrans.gameObject.AddComponent<SpriteRenderer>();
        _screenRenderer.sprite = _cachedSquareSprite;
        _screenRenderer.sortingOrder = 6;

        // 3. World-space progress bar above terminal
        var barTrans = transform.Find("TerminalProgressBar");
        if (barTrans != null) Destroy(barTrans.gameObject);

        var barRootGo = new GameObject("TerminalProgressBar");
        barRootGo.transform.SetParent(transform, false);
        barRootGo.transform.localPosition = new Vector3(0f, 0.85f, 0f);
        _worldProgressRoot = barRootGo.transform;

        var bgGo = new GameObject("Bg");
        bgGo.transform.SetParent(_worldProgressRoot, false);
        bgGo.transform.localScale = new Vector3(1.2f, 0.14f, 1f);
        var bgSr = bgGo.AddComponent<SpriteRenderer>();
        bgSr.sprite = _cachedSquareSprite;
        bgSr.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
        bgSr.sortingOrder = 25;

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(_worldProgressRoot, false);
        _worldProgressFill = fillGo.transform;
        _worldProgressFillRenderer = fillGo.AddComponent<SpriteRenderer>();
        _worldProgressFillRenderer.sprite = _cachedSquareSprite;
        _worldProgressFillRenderer.color = new Color(0.2f, 0.95f, 0.55f, 0.95f);
        _worldProgressFillRenderer.sortingOrder = 26;
    }

    private void UpdateTerminalVisuals()
    {
        if (_screenRenderer != null)
        {
            if (IsDownloaded)
            {
                _screenRenderer.color = _config != null ? _config.TerminalCompletedColor : new Color(0.25f, 0.3f, 0.35f, 1f);
            }
            else if (IsBeingHacked)
            {
                _screenRenderer.color = _config != null ? _config.TerminalHackingColor : new Color(0.2f, 0.95f, 0.55f, 1f);
            }
            else
            {
                _screenRenderer.color = _config != null ? _config.TerminalIdleColor : new Color(0.95f, 0.65f, 0.15f, 1f);
            }
        }

        if (_worldProgressFill != null)
        {
            const float fullWidth = 1.14f;
            const float height = 0.09f;
            float pct = Mathf.Clamp01(Progress);
            float w = fullWidth * pct;

            _worldProgressFill.localScale = new Vector3(w, height, 1f);
            _worldProgressFill.localPosition = new Vector3(-(fullWidth - w) * 0.5f, 0f, 0f);
        }
    }

    // ──────────────────────────── Hacking HUD ──────────────────────────────

    private void OnGUI()
    {
        // 1. If local player is carrying the downloaded data, show EVAC banner at top
        if (_localCarrier != null && _localCarrier.IsCarryingData)
        {
            DrawEvacPromptGui();
            return;
        }

        // 2. If local player is actively hacking, draw the Speed-Typing Hacker Terminal window
        if (_isLocalPlayerHacking && !IsDownloaded)
        {
            DrawHackingTerminalGui();
            return;
        }

        // 3. If local player is in range and terminal is ready, show "[E] Hack Terminal" prompt
        if (_isLocalPlayerInRange && !IsDownloaded)
        {
            DrawInteractPromptGui();
        }
    }

    private void DrawInteractPromptGui()
    {
        float w = 300f;
        float h = 42f;
        Rect rect = new Rect((Screen.width - w) * 0.5f, Screen.height - 140f, w, h);

        GUI.Box(rect, string.Empty);
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(1f, 0.85f, 0.3f);

        string label = IsBeingHacked
            ? "TERMINAL BUSY (HACK IN PROGRESS...)"
            : $"[E] Hack Terminal ({Mathf.RoundToInt(Progress * 100f)}%)";

        GUI.Label(rect, label, style);
    }

    private void DrawHackingTerminalGui()
    {
        float w = 540f;
        float h = 230f;
        Rect winRect = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 45f, w, h);

        GUILayout.BeginArea(winRect, GUI.skin.box);
        GUILayout.Space(6f);

        var headerStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };
        headerStyle.normal.textColor = new Color(0.2f, 0.95f, 0.55f);

        var codeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            richText = false
        };
        codeStyle.normal.textColor = new Color(0.35f, 0.9f, 0.55f);

        var hintStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            fontStyle = FontStyle.Bold
        };
        hintStyle.normal.textColor = new Color(1f, 0.9f, 0.4f);

        GUILayout.Label("TERMINAL OVERRIDE // DATA EXTRACTION", headerStyle);
        GUILayout.Label("MASH RANDOM KEYS ON YOUR KEYBOARD AS FAST AS YOU CAN!", hintStyle);
        GUILayout.Space(6f);

        // Scrolling hacker terminal log lines driven by total keys typed
        int visibleLines = Mathf.Clamp(1 + (_totalKeysTypedSession / 3), 1, 4);
        int startIdx = Mathf.Max(0, (_totalKeysTypedSession / 3) - visibleLines + 1);
        for (int i = 0; i < visibleLines; i++)
        {
            string line = HackerCodeLines[(startIdx + i) % HackerCodeLines.Length];
            GUILayout.Label($"> {line}", codeStyle);
        }

        GUILayout.FlexibleSpace();

        // Progress bar
        float pct = Mathf.Clamp01(Progress);
        Rect barBgRect = GUILayoutUtility.GetRect(w - 30f, 24f);
        GUI.color = new Color(0.1f, 0.12f, 0.1f, 1f);
        GUI.DrawTexture(barBgRect, Texture2D.whiteTexture);

        Rect barFillRect = new Rect(barBgRect.x + 2f, barBgRect.y + 2f, (barBgRect.width - 4f) * pct, barBgRect.height - 4f);
        GUI.color = new Color(0.2f, 0.95f, 0.55f, 1f);
        GUI.DrawTexture(barFillRect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        var barTextStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            fontStyle = FontStyle.Bold
        };
        barTextStyle.normal.textColor = Color.white;
        GUI.Label(barBgRect, $"DOWNLOADING: {Mathf.RoundToInt(pct * 100f)}%   ({ _recentKeystrokesPerSec:F1} keys/sec)", barTextStyle);

        GUILayout.Space(4f);
        var footerStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 11
        };
        footerStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label("[ESC or Right-Click] Disconnect from Terminal", footerStyle);
        GUILayout.Space(4f);

        GUILayout.EndArea();
    }

    private void DrawEvacPromptGui()
    {
        float w = 480f;
        float h = 54f;
        Rect rect = new Rect((Screen.width - w) * 0.5f, 55f, w, h);

        GUI.Box(rect, string.Empty);
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(0.2f, 0.95f, 0.65f);

        GUI.Label(rect, "DATA DOWNLOADED — EVACUATE TO YOUR SPAWN ZONE!\n(Follow the green arrow | Sidearm only & reduced speed)", style);
    }
}
