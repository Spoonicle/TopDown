---
name: gdd
description: >-
  Game Design Document for the TopDown game. Reference this for all gameplay
  mechanics, aesthetics, visual style, game feel, progression, and design
  decisions. Consult this before implementing any gameplay feature, visual
  element, or design choice.
---

# Game Design Document — TopDown

## 1. Overview

### Elevator Pitch
A gritty, top-down 3v3 tactical extraction shooter. Two special forces squads from rival nations are deployed to an abandoned factory in the dead of night — neither knows what the objective is, only that they must "get the package" and extract it before the other side does. Dark, fast, lethal.

### Genre
Top-Down Tactical Shooter / Extraction PvP

### Target Audience
- Hardcore and competitive players who enjoy tactical shooters (Rainbow Six Siege, Escape from Tarkov, Zero Hour)
- Ages 16+ due to gritty combat themes (blood, violence)
- Players who enjoy high-tension, low-information PvP scenarios
- Fans of minimalist, atmospheric indie games

### Platform
PC (Windows) — Unity

### Inspiration / Reference Games
- **Rainbow Six Siege** — Tactical team-based combat, breaching, room clearing, tight CQB engagements
- **Hotline Miami** — Top-down perspective, fast lethal combat, gritty atmosphere, screen shake and impact
- **Escape from Tarkov** — Extraction-based gameplay loop, high stakes, realistic combat feel, "you know nothing" mentality
- **Papers Please** — Voice line style (garbled/unintelligible speech with subtitle text)
- **Hong Kong Massacre** — Top-down gunplay feel, muzzle flash lighting
- **Door Kickers** — Top-down tactical entry, squad-based room clearing

---

## 2. Core Gameplay Loop

### Moment-to-Moment
Players navigate dark hallways and rooms in a top-down view. Visibility is severely limited — you see by hallway lights and the flash of gunfire. You move tactically with your squad, clearing rooms, listening for enemy footsteps and radio chatter, and engaging in fast, lethal firefights. You communicate position via radio callouts. Every corner could be a threat.

### Session Loop
A single match (approx. 5–10 minutes):
1. **Deploy** — Both 3-player squads spawn in courtyards on opposite sides of the factory
2. **Breach & Search** — Enter the procedurally generated factory, sweep floors (1–3 stories), locate the objective
3. **Secure** — Interact with the objective (extract data from a computer, pick up the biohazard container, etc.)
4. **Extract** — Carry/transport the objective back to your starting courtyard while the enemy team tries to stop you or steal it. **The carrier is restricted to sidearm (pistol) only and moves slower**, creating a high-value escort dynamic. The carrier can drop the objective to re-equip their primary weapon.
5. **Victory** — First team to successfully extract the objective wins. If all members of a team are eliminated, the surviving team wins.

### Long-Term Loop
- Unlockable operator cosmetics and squad customization (future)
- Ranked/competitive matchmaking (future)
- New objective types, factory tile sets, and equipment added over time
- Mastery of map knowledge, team coordination, and grenade mechanics

---

## 3. Player

### Movement
- **WASD** directional movement relative to screen (top-down)
- **Base speed**: Moderate tactical pace — not sprinting, not crawling. Soldiers move with purpose.
- **Sprint**: Hold Shift to sprint — faster movement but louder footsteps and unable to aim
- **Wounded movement**: Players who have taken damage move progressively slower based on health lost. At critical health, movement is significantly reduced.
- **Blood trails**: Wounded players leave blood decals on the ground as they move, allowing tracking
- **No dodge/dash**: Movement is grounded and realistic — no arcade mechanics

### Health / Lives
- **Health pool**: Finite HP (tunable, e.g., 100 HP)
- **No regeneration**: Health does not regenerate. What you lose, you keep.
- **No respawns**: Once dead, you spectate teammates for the rest of the round
- **Downed state** (future consideration): Potentially allow downed players to be revived by teammates
- **Death is fast**: Engagements should resolve in 2–4 shots depending on weapon. Combat is lethal.

### Abilities
- **Aim**: Mouse cursor controls aim direction. Character faces the cursor.
- **Shoot**: Left click to fire equipped weapon
- **Frag Grenade**: Throwable explosive. Press grenade key, click to throw at cursor position. Lethal in blast radius, damage falloff at range.
- **Flash Grenade**: Throwable. Blinds (whites out screen) players in line of sight of the detonation. Affected players also hear a ringing sound effect.
- **Grenade Foam**: Throwable canister aimed at cursor. Creates a foam patch at the target location. If a grenade (frag or flash) lands in or is covered by foam, its explosion is fully contained/neutralized. Foam persists for a duration then dissipates.
- **Grenade inventory**: Counts per grenade type are **configurable via ScriptableObject** for easy playtesting and balancing. Suggested starting defaults: 1 frag, 1 flash, 1 foam. Adjust freely during testing.
- **Radio Callout**: Press Left Alt to have your character broadcast their current position. Teammates see it as text on screen and hear the garbled voice. Enemies within audio range also hear the garbled voice (but do NOT see the text), giving away your position.

---

## 4. Combat / Interactions

### Weapons
- **Primary weapon**: Each player spawns with a standard-issue assault rifle or SMG (exact loadout TBD; initially one default weapon for whitebox)
- **Sidearm**: Pistol as secondary (future)
- **Weapon characteristics** (all tunable per weapon):
  - Fire rate
  - Damage per bullet
  - Spread / accuracy
  - Magazine size / reload time
  - Muzzle flash intensity (affects visibility for both shooter and targets)
  - Equipped indicator visual (e.g., long black line for rifle, shorter black line for pistol — configurable per `WeaponData` asset)
- **Fire modes**: Both the default Rifle and Service Pistol are semi-automatic (one shot per click), configurable per `WeaponData` asset.
- **Walls are impenetrable**: Bullets do not pass through walls. No wallbanging.
- **Gunfire sound propagation**: The sound of gunfire carries through the entire building. All players can hear shots fired anywhere in the factory, though volume attenuates with distance and floors.

### Teams & Opponents
- PvP-only game (default 3v3 squads, with solo-test AI dummies for whitebox development).
- **Dynamic N-Team Architecture**: Teams are defined via `TeamData` ScriptableObjects (not hardcoded binary enums), allowing any number of teams to be added in the future without code changes.
- **Friendly-Fire Rule**: Characters **cannot** shoot or damage members of their own team (`TeamMember.CanDamage` returns `false` for teammates, and hitscan raycasts pass through same-team colliders), and **can** shoot and damage all other teams.

### Boss Encounters
- N/A — PvP only. No boss encounters.

### Damage System
- **Hitscan** (server-authoritative raycast with client visual replication)
- **Flat damage per bullet** modified by:
  - Distance falloff (tunable curve)
  - Headshot multiplier (future consideration — may be too complex for top-down)
- **Grenade damage**: Frag grenades deal high damage in a radius with falloff
- **Flash grenades**: No damage, but apply a "flashed" debuff (screen whiteout, muffled audio) for a duration
- **Grenade foam**: No damage. Utility only — contains grenade explosions
- **Wounded effects**: As health decreases, player movement speed decreases proportionally (`Health.WoundedSpeedMultiplier` evaluated from `PlayerConfig.WoundedSpeedCurve`). Blood trail intensity increases.

---

## 5. World & Level Design

### Setting / Theme
Modern-day, abandoned industrial factory. Cold, utilitarian architecture — concrete walls, metal catwalks, rusted pipes, broken windows. Set during a moonless night. The factory has been abandoned but still has emergency lighting in some hallways. The atmosphere is oppressive and claustrophobic.

### Level Structure
- **Procedurally generated cement factory**: Synced across multiplayer via a single server-authoritative `mapSeed` (`NetworkVariable<int>`). Each match generates a new interior layout of concrete hallways, 15 dark rooms per floor, interior cover (pillars/crates), and **1 Objective Room** containing the `ComputerTerminal`.
- **Room Size Archetypes & 1–4 Entrances/Exits**: Rooms vary vastly in size and doorway/window counts scale by archetype:
  - **Closets (`5×5` to `8×8`)**: 1 single doorway, no windows, 0–1 small pillar.
  - **Standard Rooms (`11×11` to `17×17`)**: 1–3 doorways, 1–2 window spans, 2–3 pillars.
  - **Large Workshops (`18×18` to `24×24`)**: 2–4 doorways, 2–3 window spans, 3–5 pillars.
  - **Auditoriums (`26×20` to `32×26`)**: 3–4 doorways, 2–4 window spans, 5–8 pillars.
- **1 to 3 Floors (`Basement`, `1st Floor`, `2nd Floor`, `3rd Floor`)**:
  - Valid combinations: `[1st]`, `[Basement, 1st]`, `[1st, 2nd]`, `[Basement, 1st, 2nd]`, or `[1st, 2nd, 3rd]`.
  - **Strict Mutual Exclusion**: If there is a **3rd Floor**, there is **no Basement**, and vice-versa. The **1st Floor** is always present and holds the 4 exterior perimeter entrances and courtyard extraction zones.
  - **Walk-On Stairwells (`StairwellZone`)**: Stepping onto an `UP` or `DOWN` stairwell pad transitions the player and camera seamlessly to the matching stairwell on the adjacent floor.
  - **Multi-Floor Objective**: The 1 Objective Room can spawn on any active floor; `ObjectiveCarrier` routes the extraction guide arrow to the nearest stairwell toward the 1st Floor before pointing to the courtyard `ExtractionZone`.
- **4 Perimeter Entrances**: The 1st Floor exterior features 4 entrances (North, East, South, West), randomly shifted along each wall per seed while maintaining minimum spacing so teams never start fighting immediately at spawn.
- **Random Entrance Spawns & Extraction**: At round start, each team's spawn and `ExtractionZone` are placed outside a randomly selected entrance (never the same entrance).
- **Physics-Pushed Swinging Doors**: Doorways contain physical doors (`SwingDoor`) that swing open when players push against them with either their player body or their equipped weapon barrel (`WeaponVisual`).
- **Breakable Glass Windows (`BreakableWindow`)**: Room walls bordering hallways or adjacent rooms feature glass windows that allow sight through, shatter when shot so bullets pass through freely, and always block player movement.
- **Dark Rooms & Flickering Hallway Lights**: Interior rooms are dark; visibility comes from hallway emergency lights (`HallwayLight` — some steady on, some flickering, some off), stairwell beacons, objective terminal glow, and dynamic 2D muzzle flashes.

### Environmental Hazards
- **Darkness itself**: Limited visibility is the primary hazard. Players can only see clearly in lit areas or during muzzle flashes.
- **Destructible lights** (future): Shooting out hallway lights to create dark zones
- **Physics doors**: Doors physically swing and can reveal movement when pushed open by a player or gun barrel
- **Breakable glass windows**: Shatter on the first gunshot with glass shard particles, opening lines of fire while continuing to block player movement through the window frame

### Camera
- **Fixed top-down camera** (orthographic or near-orthographic)
- Camera is centered on the player
- Visibility is driven by the environment's 2D lighting (dark rooms, steady/flickering hallway lights, objective monitor glow, and dynamic weapon muzzle flashes) without a synthetic Fog of War mesh overlay.
- Slight camera smoothing/lerp on player movement

---

## 6. Aesthetic & Visual Style

### Art Direction
- **Whitebox first**: Initial build uses simple geometric shapes, flat colors, and placeholder art. Gameplay-first development.
- **Target style**: Dark, minimal, almost greyscale 2D top-down art. Utilitarian and industrial. Evokes the feeling of looking at a building schematic or security camera feed.
- Sprites should be simple, readable silhouettes — players need to distinguish friend from foe instantly in dark conditions.
- Team differentiation via subtle color coding (e.g., slight blue tint vs. slight red tint on player sprites, or armband indicators).

### Color Palette
- **Dominant**: Near-greyscale — dark greys (#1a1a1a to #3a3a3a), concrete tones (#4a4a4a to #6a6a6a)
- **Accent (environment)**: Dim warm yellow/orange for hallway lights (#c4a35a, low opacity)
- **Accent (combat)**: Bright white-yellow muzzle flash (#fff4cc), orange-red explosion (#ff6633)
- **Blood**: Dark crimson red (#8b0000) for trails and splatter — subdued, not cartoonish
- **Team A**: Subtle cool blue tint (#4a6a8a)
- **Team B**: Subtle warm tan/khaki tint (#8a7a5a)
- **UI text**: White (#ffffff) with slight transparency, clean sans-serif font

### Lighting & Atmosphere
- **Extreme darkness**: The default state is near-black. The factory is barely visible.
- **Hallway lights**: Sparse, fixed light sources casting warm pools of light in corridors. Not all hallways are lit.
- **Muzzle flash lighting**: When a weapon fires, a bright flash momentarily illuminates the surrounding area, briefly revealing the environment and players. This is a CORE visual mechanic.
- **Grenade explosions**: Frag grenades produce a large, lingering light burst. Flash grenades produce blinding white light.
- **Dynamic 2D lighting**: Unity 2D lights (URP 2D Renderer) for real-time point lights, muzzle flash lights, and environmental lights.

### Animation Style
- **Minimal and functional** for whitebox phase — rotation toward aim, basic walk cycle
- **Target feel**: Snappy, responsive. No wind-up animations. Actions happen immediately when input is pressed.
- Recoil shake on weapons when firing
- Ragdoll-style death (or simple collapse animation)

### Reference Images
<!-- Visual references to be added as development progresses -->

---

## 7. Game Feel & Juice

### Screen Shake
- **On firing**: Subtle per-shot shake (intensity tunable per weapon)
- **On taking damage**: Medium directional shake toward damage source
- **On grenade explosion**: Large shake, intensity based on distance from blast
- **On death**: Brief intense shake then fade

### Hit Feedback
- **Hit marker**: Brief visual/audio confirmation when your bullet hits an enemy
- **Blood splatter**: Small particle burst at point of impact on enemy
- **Freeze frame**: Micro-pause (1–3 frames) on killing blow (tunable)
- **Damage flash**: Brief red tint on the damaged player's sprite

### Particles & VFX
- **Muzzle flash**: Bright sprite flash + point light at barrel, randomized rotation
- **Bullet impact on walls**: Small spark/debris particles
- **Blood**: Splatter particles on hit, persistent blood trail decals from wounded players
- **Grenade explosion**: Expanding circle of force + debris particles + lingering smoke
- **Flash grenade**: Expanding white sphere + screen whiteout for affected players
- **Grenade foam**: Expanding foam blob sprite/particle at target location, bubbly texture
- **Shell casings**: Ejected casings bouncing on floor (visual only, future)

### Sound Feedback
- **Gunfire**: Punchy, sharp, realistic gunshot sounds. Echo/reverb appropriate to indoor factory environment. Audible across the entire map (attenuated by distance).
- **Footsteps**: Distinct footstep sounds on concrete/metal. Louder when sprinting. Audible to nearby enemies.
- **Radio callout**: Garbled, Papers Please-style voice (low-pitch, distorted, syllabic gibberish) played from the player character's position. Accompanied by a radio click/static sound.
- **Grenade bounce**: Metallic clinking sound when grenades hit ground/walls
- **Explosion**: Deep, concussive boom for frag grenades
- **Flash bang**: High-pitched crack + ringing for affected players
- **Ambient**: Distant industrial hum, wind through broken windows, creaking metal, dripping water

### Camera Effects
- **Zoom punch**: Brief zoom-in on explosive events, then snap back
- **Smooth follow**: Camera lerps to follow player with slight lag for natural feel
- **Flash whiteout**: Camera post-processing whiteout effect when flash-banged

---

## 8. Progression

### Difficulty Curve
- N/A for traditional difficulty — this is PvP. Difficulty is determined by opponent skill.
- Matchmaking (future) will pair players of similar skill levels.

### Unlockables
- Future: Operator skins, weapon skins, team emblems, voice line packs
- Initially: None. Focus on core gameplay.

### Currency / Economy
- Future consideration. No economy in whitebox/MVP.

### Save System
- No save system needed — matches are self-contained sessions.
- Player accounts and stats tracking (future — tied to networking/backend).

---

## 9. UI / UX

### HUD
- **Health bar**: Minimal, positioned bottom-left. Desaturates and pulses red at low health.
- **Ammo counter**: Bottom-right, current magazine / reserve ammo
- **Grenade inventory**: Small icons near ammo counter showing available grenades (frag, flash, foam) with counts
- **Teammate indicators**: Small arrows or dots at screen edges pointing to off-screen teammates
- **Radio callout text**: When a teammate (or nearby enemy) uses radio, text appears briefly near the top of the screen (e.g., "Third Floor, Left") in a military-style font. Teammate callouts in team color, enemy callouts in neutral white (if within earshot).
- **Objective indicator**: Once the objective is found/picked up, a directional indicator shows the way to extraction
- **Minimap**: None. No minimap. Information is earned through exploration and communication.

### Menus
- **Main menu**: Dark, minimal aesthetic matching the game. Play, Settings, Quit.
- **Lobby**: Team assembly screen — shows your 3-player squad, ready-up system
- **Pause menu**: Settings, Leave Match (no pausing in multiplayer)
- **End-of-round screen**: Victory/Defeat, basic stats (kills, damage dealt, objective carrier)

### Controls
- **WASD**: Movement
- **Mouse**: Aim direction (character faces cursor)
- **Left Click**: Fire weapon
- **R**: Reload
- **G** (or number keys): Cycle/select grenade type
- **Right Click** (or dedicated key): Throw selected grenade toward cursor
- **Left Alt**: Radio callout (broadcasts current position)
- **Shift**: Sprint
- **E**: Interact (pick up objective, extract data from computer)
- **Tab**: Scoreboard (future)
- **Esc**: Pause/menu
- **NOTE**: Must use Unity's New Input System (see AGENTS.md Rule 2). All input bindings defined via Input Action Assets, not hardcoded legacy Input calls.

### Accessibility
- Remappable controls (via New Input System)
- Colorblind-friendly team indicators (shapes + colors, not color alone)
- Subtitle text size options for radio callouts
- Screen shake intensity slider
- Audio cue redundancy for major events (visual + audio indicators)

---

## 10. Audio

### Music Direction
- **Minimal to none during gameplay**. The absence of music heightens tension. Players should rely on environmental audio.
- **Menu music**: Low, droning ambient synth. Tense, cold, industrial.
- **End-of-round sting**: Brief musical cue on victory/defeat.
- Future: Dynamic tension music that subtly builds as teams get closer to each other or the objective.

### Sound Effects Style
- **Realistic and grounded**: Gunshots should sound like actual firearms (punchy, with indoor reverb). Not stylized or retro.
- **Environmental audio is critical**: Footsteps, reloads, grenade pins, door handles — all provide tactical information.
- **Radio voice lines**: Papers Please style — garbled, low-pitch, syllabic gibberish. Not real words. Accompanied by radio static/click.

### Ambient Audio
- Factory hum (electrical, low-frequency drone)
- Wind through broken windows and gaps
- Distant metal creaking and settling
- Water dripping
- Flickering light buzzing
- Occasional distant mechanical sounds (conveyor belts, fans)
- All ambient sounds should be spatialized (2D positional audio)

---

## 11. Technical Notes

### Target Frame Rate
- 60fps minimum, targeting 120fps on capable hardware
- Gameplay logic should be frame-rate independent (use Time.deltaTime / Time.fixedDeltaTime properly)

### Resolution
- Default: 1920x1080
- Support common resolutions (1280x720 through 3840x2160)
- UI should scale properly across resolutions

### Performance Targets
- Lightweight 2D rendering — performance should not be an issue for top-down 2D
- Procedural generation should complete during loading screen, not at runtime
- Network tick rate: 20–30 ticks/sec minimum for responsive multiplayer (future, when networking is implemented)
- Modular script architecture: all gameplay systems built as independent, configurable components for easy iteration and expansion

### Networking (Active — Multiplayer-First)
- **Framework**: **Unity Netcode for GameObjects (`com.unity.netcode.gameobjects`)** with `UnityTransport` (Client-Host architecture, relay-ready).
- **Mandatory Multiplayer-First Design**: Every new feature, weapon, grenade, foam patch, objective, and audio/visual callout must be built as a `NetworkBehaviour` (or synced via RPCs/`NetworkVariable`s) from day one.
- **Authority Model**:
  - **Owner Authority (`IsOwner` / `HasInputAuthority`)**: Local player input, `TopDownCamera` target tracking, local screen shake, flashbang whiteout, and local HUD/vision cone.
  - **Server Authority (`IsServer` / `HasServerAuthority`)**: Health & damage (`NetworkVariable<float>`), friendly-fire validation (`TeamMember.CanDamage`), grenade trajectories & detonation, foam neutralization, objective state, and AI dummy patrol/combat.
- **Solo Playtesting Workflow**: `NetworkBootstrap` automatically starts as Host when entering Play Mode in the Editor (`_autoStartHost = true`) and spawns `Player.prefab` on opposing teams for any additional connecting clients.
- All gameplay values (damage, speed, health, grenade properties, team definitions) exposed as ScriptableObject configs for easy balancing.

---

## 12. Scope & Priorities

### MVP (Minimum Viable Product) — "White Box"
- [x] Top-down player controller (WASD + mouse aim) with New Input System & `TopDownCamera` smooth follow
- [x] Basic shooting mechanic (hitscan, muzzle flash, tracer lines, weapon indicator lines, Rifle + Pistol swapping)
- [x] Health system with wounded slowdown and blood trails (`Health` + `PlayerConfig.WoundedSpeedCurve`)
- [x] Death (no respawn within round, body darkening + collider/weapon disable)
- [x] Dynamic N-Team system (`TeamData` + `TeamMember` — cannot shoot own team, can shoot all other teams)
- [x] Multiplayer foundation with Unity Netcode for GameObjects (`NetworkBootstrap`, `NetworkBehaviour` sync)
- [ ] Frag grenade (throw at cursor, explosion with damage radius)
- [ ] Flash grenade (throw at cursor, whiteout effect on players in LOS)
- [ ] Grenade foam (throw at cursor, neutralizes grenades in area)
- [ ] One hardcoded test map (simple factory layout, single floor, two courtyards)
- [ ] One objective type (e.g., carry the biohazard container to extraction)
- [ ] Fog of war / limited visibility (vision cone + darkness)
- [ ] Basic 2D lighting (hallway lights + muzzle flash dynamic lights)
- [ ] Radio callout system (Left Alt, Papers Please voice, position text)
- [ ] Gunfire sound propagation (audible across map, distance attenuation)
- [x] AI dummy targets for solo testing (stationary + simple patrol) — test shooting, grenades, and game feel without needing a second player
- [x] Basic whitebox art (rectangles, simple shapes, flat colors)
- [x] Screen shake and basic hit feedback

### Nice-to-Have
- Procedural map generation (tile-based factory interiors, 1–3 floors)
- Online relay / lobby matchmaking integration (Steam Relay / Unity Relay)
- Computer objective (data extraction with progress bar)
- Additional objective types
- Loadout selection (weapon choices)
- Destructible lights
- Breakable doors / breaching mechanics
- Shared team vision
- Downed/revive state
- Ranked matchmaking
- Spectator mode
- Replay system
- Operator cosmetics and unlockables

### Out of Scope
- Single-player campaign or story mode
- AI enemies / PvE content
- Open world or large-scale maps
- Vehicles
- Crafting systems
- Microtransactions or real-money economy (at this stage)
- Mobile or console ports (PC only for now)
- Voice chat (replaced by the radio callout system by design)
- Destructible walls / terrain deformation
