using Microsoft.Maui.Controls;

namespace VulkanView;

/// <summary>
/// MAUI control that owns a Vulkan device and presents a scene through a
/// platform surface (SwapChainPanel on Windows, SurfaceView on Android).
/// </summary>
public class VulkanView : View
{
    public static readonly BindableProperty SceneProperty = BindableProperty.Create(
        nameof(Scene),
        typeof(IVulkanScene),
        typeof(VulkanView));

    public IVulkanScene? Scene
    {
        get => (IVulkanScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public event EventHandler<string>? RenderError;

    /// <summary>Renders and presents one frame. Call from the game loop.</summary>
    public void RequestFrame()
    {
        if (Handler is VulkanViewHandler handler)
            handler.RequestFrame();
    }

    internal void ReportError(string message) => RenderError?.Invoke(this, message);
}
