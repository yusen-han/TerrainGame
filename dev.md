# Development Plan

## Goal
A cross-platform (Windows + Android), single-player terrain-discovery game built on .NET MAUI + Silk.NET.Vulkan. The player flies freely over streamed, procedurally generated 3D terrain and discovers it. Terrain is rendered directly into a platform swapchain hosted by a reusable MAUI control (`VulkanView`); platform code lives inside that control. No game engine.

## Status
| Area | State |
|------|-------|
| MAUI shell, window, life cycle | Done |
| `VulkanView` MAUI control owning instance/device/surface/swapchain | Done |
| HLSL shaders compiled to SPIR-V, embedded in the assembly | Done |
| Custom 3D noise + Marching Cubes terrain | Done |
| 3D pipeline: vertex buffers, depth buffer, uniform buffer, lighting, fog | Done |
| Terrain materials (sand / dirt / stone) per vertex | Done |
| Hold-to-dig with volumetric caves (persistent voxel edits) | Done |
| Streaming chunks around the player on background workers | Done |
| Fly camera + pointer-look + on-screen movement buttons | Done |
| Swapchain presentation: DXGI SwapChainPanel (Windows) / VkSwapchainKHR (Android) | Done |
| Windows build & run | Verified |
| Android build | Compiles; not yet run on a device |
| UX polish (HUD, saving, scoring) | Not started |

## Architecture
| Project path | Responsibility |
|--------------|----------------|
| `TerrainGame/MainPage.xaml(.cs)` | Game loop (30 fps dispatcher timer), input, HUD, dig raycast; implements `IVulkanScene` and forwards frames to `TerrainRenderer` |
| `TerrainGame/Graphics/TerrainRenderer.cs` | Scene renderer: render pass, depth buffer, pipeline, chunk vertex buffers, uniform buffer, material texture array + sampler; records into the control's command buffer |
| `TerrainGame/Graphics/CameraController.cs` | Free-fly camera (no gravity), yaw/pitch, view matrix |
| `TerrainGame/Terrain/Noise2D.cs` | Seeded 2D Perlin noise (base height) |
| `TerrainGame/Terrain/Noise3D.cs` | Seeded 3D Perlin noise (sparse cave carving) |
| `TerrainGame/Terrain/TerrainHeightField.cs` | 2D fBm height with rain/droplet erosion; world-aligned cached tiles with border blending |
| `TerrainGame/Terrain/TerrainGenerator.cs` | Fills `ChunkData` from the height field, carves sparse caves, writes per-voxel materials, meshes chunks |
| `TerrainGame/Terrain/ChunkData.cs` | Dense 3D density grid + per-voxel material grid for one chunk |
| `TerrainGame/Terrain/TerrainWorld.cs` | Owns the chunk grids; digging writes directly into them, modified chunks survive unload, trilinear density sampling |
| `TerrainGame/Terrain/TerrainMaterial.cs` | Grass / Sand / Dirt / Stone IDs shared with the shader |
| `TerrainGame/Terrain/MarchingCubes.cs` | Table-free Marching Cubes: per-face intersections stitched into loops, loop fan triangulation, orientation from the density gradient; emits the stored per-voxel material |
| `TerrainGame/Terrain/ChunkStreamer.cs` | Desired-chunk set around the camera, background generation via `Channel<long>`, rebuild requests after digging, releases chunks through `TerrainWorld` |
| `TerrainGame/Shaders/terrain.hlsl` | Vertex + pixel shaders (HLSL 6.0 → SPIR-V): triplanar sampling from the material texture array, lighting, fog |
| `TerrainGame/Resources/Textures/*.png` | Grass / sand / dirt / stone textures, embedded in the assembly and packed into a 2D array image (256×256 per layer) |
| `VulkanView/VulkanView.cs` | MAUI control: `Scene` property, `RequestFrame()`, error event |
| `VulkanView/VulkanContext.cs` | Instance/device/queue/command pool + memory helpers |
| `VulkanView/Platforms/Windows/*` | `SwapChainPanel` + D3D11 shared-texture interop (imported into Vulkan, one GPU blit, DXGI present) |
| `VulkanView/Platforms/Android/*` | `SurfaceView` + `VK_KHR_android_surface`, real `VkSwapchainKHR`, zero-copy present |

### Frame flow
1. Update camera from input.
2. Ask `ChunkStreamer` for chunks around the camera; upload finished meshes, destroy out-of-range ones.
3. Build `view * projection`, write the scene uniform buffer.
4. The control has already acquired a target image and begun the command buffer. `TerrainRenderer.Render` records the render pass (clear + depth), binds the pipeline and draws each chunk, ending in the platform's final layout (`PresentSrcKHR` on Android, `ColorAttachmentOptimal` on Windows).
5. The control ends the command buffer and submits; on Android it presents the swapchain image; on Windows it waits for the queue, copies the shared texture into the DXGI backbuffer and presents.

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
- Drag on the image with mouse or touch: look around (yaw/pitch). Pointer deltas are used where available; a pan gesture covers Android touch.
- `Fwd` / `Back`: move along the view direction; `Left` / `Right`: strafe; `Up` / `Down`: rise/sink. Gravity is not simulated.
- **Dig**: press and hold the left mouse button / touch for more than one second. The crosshair turns orange while held and a brush is carved at the crosshair every 350 ms. Works on the surface and underground (caves).
- Start position: `(0, 16, 0)` looking slightly down at the terrain.

## Terrain model
- **Generation**: a 2D fBm height field (`8 * tanh(fbm * 1.2)`) is refined with rain/droplet erosion over world-aligned 64-unit tiles (16-unit padding, cached). Sampling blends neighbouring tiles along an 8-unit border band, so chunk meshes never crack.
- **Storage**: each chunk keeps a dense 33 × 49 × 33 grid (0.5-unit spacing, 16 × 24 × 16 world units) of `float Density` plus a `byte Material` grid (`ChunkData`). Positive density is solid.
- **Fill**: `density = HeightAt(x,z) - y`, with a sparse cave term (single-octave 3D noise thresholded at 0.55, scaled by 30 and gated to depths of 3–7 units) that leaves occasional pockets and tunnels, never a sponge.
- **Digging**: the brush subtracts directly from `ChunkData.Density` in every affected chunk and marks it modified. Modified chunks are kept when they stream out; clean chunks regenerate identically.
- **Materials** are stored per voxel at generation time: steep or deep → Stone, low near-surface → Sand, near-surface flat → Grass, otherwise Dirt. Marching Cubes reads the material of the solid cell and writes it into the vertex.
- **Texturing**: the four PNGs are decoded with SkiaSharp, resized to 256×256 and uploaded as one `R8G8B8A8_UNORM` 2D array image (layer order = Grass, Sand, Dirt, Stone). Descriptor set: binding 0 uniform buffer, binding 1 sampled image array, binding 2 repeating linear sampler. The pixel shader samples the layer selected by the vertex material with triplanar projection (`0.12` tiles per world unit), so cliffs and cave walls are textured without UVs.

## Known Issues / Limits
- **Windows has one GPU blit**: Vulkan renders into a shared D3D11 texture and `CopyResource` pushes it into the SwapChainPanel's backbuffer. There is no CPU readback, but it is not strictly zero-copy. Android presents directly to swapchain images.
- **Dependency on matching GPUs**: the D3D11 device and the Vulkan physical device must be on the same adapter for the shared-texture import. Multi-GPU laptops can pick different adapters.
- **Modified chunks are memory-only**: digging survives streaming but not an app restart; there is no disk save yet. Modified chunk grids also stay in memory while exploring.
- **No collision**: the camera can fly through terrain.
- **Bounded world**: each chunk spans y ∈ [-16, 8]; digging outside that band has no visible effect.
- **Textures are basic**: 256×256, no mipmaps, no normal/roughness maps, and the source images are not seamless so tiling is visible up close. Triplanar projection can look slightly soft on 45° slopes.
- **No MSAA**: distant terrain shows some shimmering.
- **Windows high-DPI quirk**: on the test machine (200 % scaling) MAUI reports layout in physical pixels. The Windows target therefore sizes the swapchain from `ActualWidth/Height` directly instead of multiplying by `CompositionScale` (which double-scaled it). On a correctly-scaled display the swapchain is rendered at logical resolution and stretched slightly.
- Android is built but not yet exercised on hardware; Vulkan availability and driver behaviour still need testing.

### Rendering backends
Frames are no longer read back to the CPU. `VulkanView` owns the Vulkan instance/device and the presentation target:
- **Windows**: `SwapChainPanel` + DXGI `CreateSwapChainForComposition`; Vulkan renders into a D3D11 texture shared through an NT handle (`VK_KHR_external_memory_win32`), then one `CopyResource` + `Present`. The targets report `FlipY` (D3D11 stores the image top-down, and Android's swapchain presents Vulkan's Y-down NDC directly), so the app mirrors the projection's Y axis on both platforms.
- **Android**: `SurfaceView` + `VK_KHR_android_surface` with a real `VkSwapchainKHR`; rendering goes straight into acquired swapchain images and `vkQueuePresentKHR` presents them.
- **Apple/MoltenVK**: deferred. The control is structured so an `VK_EXT_metal_surface` target can be added with a bundled MoltenVK loader.

## Next Steps
1. Upload the readback pixels as a GPU texture (or render into a surface) to remove the CPU round-trip.
2. Add vertical chunk layers and larger view distance.
3. Add discovery mechanics (visited-chunk tracking, map/Fog of war, points of interest).
4. LOD for distant chunks, MSAA, and a proper sky/horizon.
5. Save/load discovered terrain state.
