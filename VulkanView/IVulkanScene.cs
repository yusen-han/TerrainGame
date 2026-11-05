using Silk.NET.Vulkan;

namespace VulkanView;

/// <summary>
/// Application-supplied renderer. The control owns the device and presentation;
/// the scene records its own render pass into the supplied command buffer.
/// </summary>
public interface IVulkanScene
{
    void OnTargetCreated(VulkanTargetInfo target);

    void OnTargetResized(VulkanTargetInfo target);

    void Render(VulkanFrameContext frame);

    void OnTargetDestroyed();
}
