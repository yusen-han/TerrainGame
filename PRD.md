# Terrain Discovery Game

## Overview
A cross‑platform, single‑player game that lets players fly freely and discover procedurally generated 3‑D terrain.

- **Platforms:** Windows & Android
- **Engine stack:** .NET MAUI for the runtime shell, Silk.NET.Vulkan for graphics, HLSL for shaders, and a custom Marching Cubes implementation for geometry.
- **Controls:** First‑person fly‑mode (gravity disabled).
- **Networking:** None (single‑player only).
- **Assets:** Placeholder pure‑color textures.
- **Feature status:** Only gameplay loop and terrain discovery exist; future work will add UI, scoring, and optional save data.

## Core Gameplay
- Player controls a flying camera that can move freely in all three axes.
- As the player moves, terrain is generated around them using a custom, streaming terrain generator.
- Terrain is represented as a 3‑D mesh produced with the Marching Cubes algorithm.
- Off‑screen rendering of the terrain is performed with Silk.NET.Vulkan; pixel data is copied to the CPU and displayed through MAUI’s native view.

## Architecture
| Layer | Responsibility | Key Libraries |
|-------|----------------|----------------|
| Runtime | Cross‑platform window and input handling | .NET MAUI |
| Graphics Pipeline | Render 3‑D terrain via Vulkan | Silk.NET.Vulkan, HLSL shaders |
| Terrain Generation | Procedural terrain, streaming & Marching Cubes | Custom C# code |
| Input | WASD/keyboard + mouse for flight | MAUI’s input events |

## Development Constraints
- No external game engines (Unity, Unreal, Godot, etc.).
- No platform‑specific code; rely exclusively on MAUI and Silk.NET for cross‑platform abstraction.
- Shader code written in HLSL; no GLSL.
- The terrain generator is an in‑house implementation, not a NuGet package.
- All assets will use simple placeholder textures; no external graphics libraries.
- Only single‑player mode – no networking or multiplayer subsystems.

## Milestones
| Milestone | Description | Target Date |
|-----------|-------------|-------------|
| 1.0 – Core engine | MAUI window, Vulkan context, basic flight controls, streaming terrain generator, Marching Cubes mesh, off‑screen render copy | 2026‑11‑30 |
| 1.1 – Performance tuning | Optimize streaming, reduce CPU usage, enable multi‑threaded terrain build | 2026‑12‑15 |
| 1.2 – UI & UX | On‑screen HUD, pause menu, settings | 2027‑01‑10 |
| 1.3 – Polish & Release | Bug fixes, packaging for Windows & Android | 2027‑02‑01 |

## Success Metrics
- Stable frame rate on a mid‑range Windows laptop and standard Android device.
- Smooth flight navigation without latency.
- Terrain must render continuously as the player moves.
- No crashes or memory leaks after prolonged play.

---

*Prepared: 2026‑10‑25*
