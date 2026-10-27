# Development Plan

## Goal
A cross-platform (Windows + Android), single-player terrain-discovery game built on .NET MAUI + Silk.NET.Vulkan. The player flies freely over streamed, procedurally generated 3D terrain and discovers it. Terrain is rendered off-screen, copied to the CPU and shown through MAUI. No game engine, no platform-specific code.

## Status
| Area | State |
|------|-------|
| MAUI shell, window, life cycle | Done |
| Off-screen Vulkan renderer (image → CPU pixels → `SKCanvasView`) | Done |
| HLSL shaders compiled to SPIR-V, embedded in the assembly | Done |
| Custom 3D noise + Marching Cubes terrain | Done |
| 3D pipeline: vertex buffers, depth buffer, uniform buffer, lighting, fog | Done |
| Terrain materials (sand / dirt / stone) per vertex | Done |
| Hold-to-dig with volumetric caves (persistent voxel edits) | Done |
| Streaming chunks around the player on background workers | Done |
| Fly camera + pointer-look + on-screen movement buttons | Done |
| SkiaSharp presentation (SKBitmap over the readback buffer, zero-copy) | Done |
| Windows build & run | Verified |
| Android build | Compiles; not yet run on a device |
| UX polish (HUD, saving, scoring) | Not started |

## Architecture
| Project path | Responsibility |
|--------------|----------------|
| `TerrainGame/MainPage.xaml(.cs)` | Game loop (30 fps dispatcher timer), input, HUD, dig raycast, wrapping the readback buffer in an `SKBitmap` and invalidating the `SKCanvasView` |
| `TerrainGame/Graphics/VulkanRenderer.cs` | Headless Vulkan: instance/device, color + depth targets, render pass, pipeline, chunk vertex buffers, uniform buffer, readback, row flip |
| `TerrainGame/Graphics/CameraController.cs` | Free-fly camera (no gravity), yaw/pitch, view matrix |
| `TerrainGame/Terrain/Noise3D.cs` | Seeded 3D Perlin noise |
| `TerrainGame/Terrain/TerrainGenerator.cs` | 3D fBm density field (no heightmap) → voxel grid → Marching Cubes, plus material selection |
| `TerrainGame/Terrain/VoxelEdits.cs` | Sparse persistent voxel density deltas; digging subtracts a brush volume |
| `TerrainGame/Terrain/TerrainMaterial.cs` | Sand / Dirt / Stone IDs shared with the shader |
| `TerrainGame/Terrain/MarchingCubes.cs` | Table-free Marching Cubes: per-face intersections stitched into loops, loop fan triangulation, orientation from the density gradient |
| `TerrainGame/Terrain/ChunkStreamer.cs` | Desired-chunk set around the camera, background generation via `Channel<long>`, rebuild requests after digging, ready/removal queues |
| `TerrainGame/Shaders/terrain.hlsl` | Vertex + pixel shaders (HLSL 6.0 → SPIR-V), material palette, lighting, fog |

### Frame flow
1. Update camera from input.
2. Ask `ChunkStreamer` for chunks around the camera; upload finished meshes, destroy out-of-range ones.
3. Build `view * projection`, write the scene uniform buffer.
4. Record: render pass (clear + depth), bind pipeline, draw each chunk, barrier, copy color image to a host-visible buffer.
5. Submit, wait idle, map the buffer, copy pixels, flip rows.
6. `NotifyPixelsChanged()` on the `SKBitmap`, then `SKCanvasView.InvalidateSurface()`. The canvas draws the bitmap with aspect fill.

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
SPIR-V binaries are committed so the project builds without extra tooling. To regenerate after editing `Shaders/terrain.hlsl` you need `dxc` **with SPIR-V support** — the Windows SDK copy does not have it. Either download the official DirectXShaderCompiler release, or take `build/native/bin/x64/dxc.exe` from the `Microsoft.Direct3D.DXC` NuGet package (this is what was used):

```bash
dxc -spirv -T vs_6_0 -E VSMain -Zpr -Fo terrain.vert.spv terrain.hlsl
dxc -spirv -T ps_6_0 -E PSMain -Zpr -Fo terrain.frag.spv terrain.hlsl
```
The `.spv` files are embedded as `TerrainGame.Shaders.<name>.spv`.

## Controls
- Drag on the image: look around (yaw/pitch).
- `Fwd` / `Back`: move along the view direction; `Left` / `Right`: strafe; `Up` / `Down`: rise/sink. Gravity is not simulated.
- **Dig**: press and hold the left mouse button / touch for more than one second. The crosshair turns orange while held and a brush is carved at the crosshair every 350 ms. Works on the surface and underground (caves).
- Start position: `(0, 16, 0)` looking slightly down at the terrain.

## Terrain model
- Density field: `BaseDensity(x,y,z) = -y*0.55 + fBm3D(x,y,z)*7`, plus a closure bias that forces solid rock below y = -8 and air above y = 4 (positive = solid). No heightmap; overhangs, arches and caves come straight out of the noise.
- Chunks are 32 × 48 × 32 cells at 0.5 units (16 × 24 × 16 world units), sampled into a 33 × 49 × 33 grid and meshed with Marching Cubes.
- Digging subtracts a smooth spherical brush from world-space voxel samples at 0.5-unit spacing. The sparse deltas live in `VoxelEdits` and are applied on every chunk build, so edits survive chunk unload/reload and can carve enclosed caves.
- Material IDs are written per vertex from slope and altitude: steep or high → Stone, low → Sand, flat → Grass, everything else → Dirt. The shader maps IDs to flat colours.

## Known Issues / Limits
- **Readback is the bottleneck**: every frame is rendered off-screen, copied to host memory and flipped on the CPU (~1000×600 at ~30 fps target). The next step would be uploading the pixels as a GPU texture instead of reading them back.
- **Edits are memory-only**: digging is persisted for the session but not saved to disk; restarting regenerates the untouched world.
- **No collision**: the camera can fly through terrain.
- **Bounded world**: each chunk spans y ∈ [-16, 8]; the field is forced solid below -8 and air above 4, so digging outside that band has no visible effect.
- **No MSAA**: distant terrain shows some shimmering.
- **Windows high-DPI quirk**: on the test machine (200 % scaling) MAUI reports a page size in physical pixels while WinUI scales the content, so the UI is drawn enlarged and clipped. Layout is anchored top-left so all controls remain reachable; the same layout is correct on other devices. Not addressed in game code (would require platform-specific fixes).
- Android is built but not yet exercised on hardware; Vulkan availability and driver behaviour still need testing.

### Frame presentation
Earlier builds assigned `Image.Source` (a new stream-backed source) every frame, which flashes on Android because the native drawable is torn down before the next bitmap is decoded. Frames now go to an `SKBitmap` that wraps the renderer's pinned BGRA buffer (installed once, zero-copy) and are painted by an `SKCanvasView`; no PNG encoding, no source churn.

## Next Steps
1. Upload the readback pixels as a GPU texture (or render into a surface) to remove the CPU round-trip.
2. Add vertical chunk layers and larger view distance.
3. Add discovery mechanics (visited-chunk tracking, map/Fog of war, points of interest).
4. LOD for distant chunks, MSAA, and a proper sky/horizon.
5. Save/load discovered terrain state.
