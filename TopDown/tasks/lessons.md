# Lessons Learned

## Visibility & Atmosphere
- **Rely on Natural 2D Lighting Rather Than Mesh Fog of War Overlays**: The user prefers the game's atmosphere and visibility to come from the URP 2D lighting setup (dark factory rooms, steady/flickering hallway lights, and dynamic muzzle flashes) rather than a procedural Fog of War mesh overlay. Do not re-introduce a Fog of War mesh unless explicitly requested.

## Map Generation & Doorways
- **Doorway Swing Clearance & Obstruction Prevention**: Doors must never spawn against, swing into, or be obstructed by interior cover pillars, perpendicular wall jambs, or dead-end geometry. Always enforce generous clearance boxes (at least 4 tiles / full door length + margins) around all doorway openings and hinges to guarantee door swing arcs and player passage remain 100% unobstructed. Re-validate door clearances post-instantiation.
