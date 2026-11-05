using Silk.NET.Vulkan;

namespace VulkanView;

/// <summary>One frame handed to the scene by the control.</summary>
public readonly struct VulkanFrameContext
{
    public required CommandBuffer CommandBuffer { get; init; }

    public required int ImageIndex { get; init; }

    public required Extent2D Extent { get; init; }

    public float AspectRatio => Extent.Height == 0 ? 1f : Extent.Width / (float)Extent.Height;
}
