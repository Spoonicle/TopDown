# AI Agents Guidelines

Welcome to this project. All AI agents, coding assistants, and automated tools working on this repository must strictly adhere to the project's specific conventions and instructions.

### 1. Unity Project Scope
- Treat this repository as a Unity project first.
- Follow Unity-first workflows for gameplay, scenes, prefabs, assets, and C# scripts.
- Prefer Unity MCP tooling for validation, inspection, and editor-safe automation when useful.
- Never use dot net commands to validate. Use Unity MCP instead.
- Do not hand-author or manually edit `.meta` files.
- Unity-generated `.meta` files are expected and valid when Unity creates them (for example, after adding new scripts/assets).

### 2. Input System Standard (Mandatory)
- Always use the `Input System Package (New)`.
- Do not introduce or rely on the legacy Input Manager (`UnityEngine.Input` old system) for new work.
- If touching input-related code, align it with the New Input System patterns already used in the project.

### 3. Self-Improvement Loop
- After ANY correction from the user: update `tasks/lessons.md` with the pattern
- Write rules for yourself that prevent the same mistake
- Ruthlessly iterate on these lessons until mistake rate drops
- Review lessons at session start for relevant project

### 4. Verification Before Done
- Never mark a task complete without proving it works
- Diff behavior between main and your changes when relevant
- Ask yourself: "Would a staff engineer approve this?"
- Run tests, check logs, demonstrate correctness
- Use unity mcp if needed to verify changes

### 5. Demand Elegance (Balanced)
- For non-trivial changes: pause and ask "is there a more elegant way?"
- If a fix feels hacky: "Knowing everything I know now, implement the elegant solution"
- Skip this for simple, obvious fixes - don't over-engineer
- Challenge your own work before presenting it

### 6. Autonomous Bug Fixing
- When given a bug report: just fix it. Don't ask for hand-holding
- Point at logs, errors, failing tests - then resolve them
- Zero context switching required from the user
- Go fix failing CI tests without being told how

### 7. Subagent Strategy
- Spawn subagents liberally to keep main context window clean
- Offload research, exploration, and parallel analysis to subagents
- For complex problems, throw more compute at it via subagents
- One task per subagent for focused execution

### 8. Code Style & Conventions
- Follow standard **C# conventions**: PascalCase for public members/methods, camelCase for private fields.
- Prefix private fields with an underscore (e.g., `_playerHealth`).
- Use `[SerializeField]` on private fields instead of making them public.
- Add XML doc comments (`/// <summary>`) to all public classes and methods.
- Keep MonoBehaviour scripts focused — one responsibility per script.

### 9. Architecture
- Place gameplay scripts under `Assets/Scripts/`.
- Use **ScriptableObjects** for shared configuration and data.
- Prefer **composition over inheritance** for game components.
- Avoid `Find()`, `FindObjectOfType()`, and `GetComponent()` in `Update()` — cache references in `Awake()` or `Start()`.

### 10. File & Folder Safety
- **Never modify** files under `Library/`, `Temp/`, `Logs/`, or `Packages/` directly.
- **Never modify** `ProjectSettings/` files without explicit user permission.
- When creating new scripts or assets, let Unity auto-generate `.meta` files (see Rule 1).

### 11. Scene & Asset Workflow
- Use the Unity MCP to create, move, or modify GameObjects in the scene.
- When adding new assets, place them in appropriate subfolders under `Assets/` (e.g., `Assets/Sprites/`, `Assets/Prefabs/`, `Assets/Materials/`, `Assets/Scripts/`).
- Organize assets logically by type or feature.

### 12. Multiplayer-First Architecture (Mandatory)
- Every gameplay feature, weapon, ability, grenade, objective, and UI system **must be designed and implemented for multiplayer from the start** using **Unity Netcode for GameObjects (`Unity.Netcode`)**.
- Use `NetworkBehaviour` and `NetworkObject` for networked entities, and register any dynamically spawned prefabs with `NetworkManager`.
- Strictly separate **Client/Owner Authority** from **Server Authority**:
  - **Owner Authority (`IsOwner` / `HasInputAuthority`)**: Player input, local camera tracking (`TopDownCamera`), local screen shake, flashbang screen whiteout, and local HUD/vision cone must only affect the owning client.
  - **Server Authority (`IsServer` / `HasServerAuthority`)**: Damage calculation, health (`NetworkVariable`), grenade physics/detonation, foam containment, objective state, and AI dummy logic must be authoritative on the Server/Host.
  - **Replication (`NetworkVariable` & `Rpc`)**: Sync persistent state via `NetworkVariable<T>` and broadcast transient events (shots, tracers, muzzle flashes, explosions, radio callouts) via `[Rpc]`.
- **Dynamic N-Team Support**: Never hardcode a fixed two-team enum. Always use `TeamData` ScriptableObjects and `TeamMember.CanDamage(attacker, target)` so any number of teams works out of the box (*cannot shoot/damage own team; can shoot/damage all other teams*).
- **Zero-Friction Solo Playtesting**: Ensure `NetworkBootstrap` auto-starts as Host in the Editor and keep safe `!IsSpawned` fallbacks so pressing Play immediately works for solo testing against dummies.