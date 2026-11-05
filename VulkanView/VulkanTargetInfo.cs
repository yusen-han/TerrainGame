using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace VulkanView;

/// <summary>Formatting and size of the current presentation target.</summary>
public sealed class VulkanTargetInfo
{
    public required VulkanContext Context { get; init; }

    public required Format Format { get; init; }

    public required Extent2D Extent { get; init; }

    /// <summary>Layout the scene's final render pass must transition the image to.</summary>
    public required ImageLayout FinalLayout { get; init; }

    public required IReadOnlyList<ImageView> ImageViews { get; init; }

    /// <summary>True when the colour image is stored top-down (D3D11 interop).</summary>
    public bool FlipY { get; init; }

    public float AspectRatio => Extent.Height == 0 ? 1f : Extent.Width / (float)Extent.Height;
}
