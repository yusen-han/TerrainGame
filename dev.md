# Development Plan

## Goal
A cross-platform (Windows + Android), single-player terrain-discovery game built on .NET MAUI + Silk.NET.Vulkan. The player flies freely over streamed, procedurally generated 3D terrain and discovers it. Terrain is rendered off-screen, copied to the CPU and shown through MAUI. No game engine, no platform-specific code.

## Status
| Area | State |
|------|-------|
| MAUI shell, window, life cycle | Done |
| Off-screen Vulkan renderer (image → CPU pixels → MAUI `Image`) | Done |
| HLSL shaders compiled to SPIR-V, embedded in the assembly | Done |
| Custom noise + Marching Cubes terrain | Done |
| 3D pipeline: vertex buffers, depth buffer, uniform buffer, lighting, fog | Done |
| Streaming chunks around the player on background workers | Done |
| Fly camera + pointer-look + on-screen movement buttons | Done |
| Windows build & run | Verified |
| Android build | Compiles; not yet run on a device |
| UX polish (HUD, saving, scoring) | Not started |

## Architecture
| Project path | Responsibility |
|--------------|----------------|
| `TerrainGame/MainPage.xaml(.cs)` | Game loop (20 fps dispatcher timer), input, HUD, handing PNG frames to the drawable |
| `TerrainGame/Graphics/VulkanRenderer.cs` | Headless Vulkan: instance/device, color + depth targets, render pass, pipeline, chunk vertex buffers, uniform buffer, readback, row flip |
| `TerrainGame/Graphics/FrameDrawable.cs` | `IDrawable` that caches the decoded frame and draws it into a `GraphicsView` (double-buffered by the view, no `Image.Source` churn) |
| `TerrainGame/Graphics/CameraController.cs` | Free-fly camera (no gravity), yaw/pitch, view matrix |
| `TerrainGame/Graphics/PngEncoder.cs` | Minimal BGRA → PNG encoder (zlib/Adler-32/CRC-32) used to display frames |
| `TerrainGame/Terrain/TerrainGenerator.cs` | Seeded fBm Perlin heightfield → 17³ density grid with a soft vertical clamp |
| `TerrainGame/Terrain/MarchingCubes.cs` | Table-free Marching Cubes: per-face intersections stitched into loops, loop fan triangulation, orientation from the density gradient |
| `TerrainGame/Terrain/ChunkStreamer.cs` | Desired-chunk set around the camera, background generation via `Channel<long>`, ready/removal queues |
| `TerrainGame/Shaders/terrain.hlsl` | Vertex + pixel shaders (HLSL 6.0 → SPIR-V) |

### Frame flow
1. Update camera from input.
2. Ask `ChunkStreamer` for chunks around the camera; upload finished meshes, destroy out-of-range ones.
3. Build `view * projection`, write the scene uniform buffer.
4. Record: render pass (clear + depth), bind pipeline, draw each chunk, barrier, copy color image to a host-visible buffer.
5. Submit, wait idle, map the buffer, copy pixels, flip rows.
6. Encode PNG, hand it to `FrameDrawable`, and call `GraphicsView.Invalidate()`.

## Build & Run
```bash
cd TerrainGame
dotnet build -f net10.0-windows10.0.19041.0
dotnet build -f net10.0-android
```
Run on Windows:
```bash
cd TerrainGame/bin/Debug/net10.0-windows10.0.19041.0/win-x64
./TerrainGame.exe
```

### Compiling the shaders
SPIR-V binaries are committed so the project builds without extra tooling. To regenerate after editing `Shaders/terrain.hlsl` you need `dxc` **with SPIR-V support** (the Windows SDK copy does not have it; download the official DirectXShaderCompiler release):

```bash
dxc -spirv -T vs_6_0 -E VSMain -Zpr -Fo terrain.vert.spv terrain.hlsl
dxc -spirv -T ps_6_0 -E PSMain -Zpr -Fo terrain.frag.spv terrain.hlsl
```
The `.spv` files are embedded as `TerrainGame.Shaders.<name>.spv`.

## Controls
- Drag on the image: look around (yaw/pitch).
- `Fwd` / `Back`: move along the view direction; `Left` / `Right`: strafe; `Up` / `Down`: rise/sink. Gravity is not simulated.
- Start position: `(0, 16, 0)` looking slightly down at the terrain.

## Known Issues / Limits
- **Display path is CPU-bound**: every frame is read back, PNG-encoded (1000×600) and PNG-decoded inside the draw pass at 20 fps. Fine for a placeholder, but 3D at high resolution needs a GPU texture upload path instead.
- **No collision**: the camera can fly through terrain.
- **Chunks are one voxel layer tall** (y ∈ [-8, 8]); the height function is soft-clamped so the surface never leaves the grid. Taller worlds need vertical chunk layers.
- **No MSAA**: distant terrain shows some shimmering.
- **Windows high-DPI quirk**: on the test machine (200 % scaling) MAUI reports a page size in physical pixels while WinUI scales the content, so the UI is drawn enlarged and clipped. Layout is anchored top-left so all controls remain reachable; the same layout is correct on other devices. Not addressed in game code (would require platform-specific fixes).
- Android is built but not yet exercised on hardware; Vulkan availability and driver behaviour still need testing.

### Frame presentation
Earlier builds assigned `Image.Source` (a new stream-backed source) every frame, which flashes on Android because the native drawable is torn down before the next bitmap is decoded. Frames now go through `FrameDrawable` + `GraphicsView`: the decoded `IImage` is swapped inside a single draw pass, so there is no blank interval.

## Next Steps
1. Replace PNG-per-frame with a GPU texture upload or a platform-agnostic surface path.
2. Add vertical chunk layers and larger view distance.
3. Add discovery mechanics (visited-chunk tracking, map/Fog of war, points of interest).
4. LOD for distant chunks, MSAA, and a proper sky/horizon.
5. Save/load discovered terrain state.
