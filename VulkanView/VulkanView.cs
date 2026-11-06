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

    public event EventHandler? DiagnosticsChanged;

    private string? _diagnostics;

    /// <summary>Last platform/host state, surfaced in the app HUD for debugging.</summary>
    public string? Diagnostics
    {
        get => _diagnostics;
        internal set
        {
            _diagnostics = value;
            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Renders and presents one frame. Call from the game loop.</summary>
    public void RequestFrame()
    {
        if (Handler is VulkanViewHandler handler)
            handler.RequestFrame();
    }

    /// <summary>Re-delivers OnTargetCreated if a target exists but the scene lost it.</summary>
    public void EnsureTarget()
    {
        if (Handler is VulkanViewHandler handler)
            handler.EnsureTarget();
    }

    internal void ReportError(string message) => RenderError?.Invoke(this, message);
}
