using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace VulkanView;

public class VulkanViewHandler : ViewHandler<global::VulkanView.VulkanView, SwapChainPanel>
{
    private WindowsVulkanHost? _host;

    public static readonly IPropertyMapper<global::VulkanView.VulkanView, VulkanViewHandler> Mapper =
        new PropertyMapper<global::VulkanView.VulkanView, VulkanViewHandler>(ViewMapper)
        {
            [nameof(global::VulkanView.VulkanView.Scene)] = MapScene,
        };

    static VulkanViewHandler()
    {
        // SwapChainPanel does not support the FrameworkElement.Background property.
        Mapper.ModifyMapping(nameof(IView.Background), (_, _, _) => { });
    }

    public VulkanViewHandler() : base(Mapper)
    {
    }

    protected override SwapChainPanel CreatePlatformView() => new();

    protected override void ConnectHandler(SwapChainPanel platformView)
    {
        base.ConnectHandler(platformView);
        platformView.PointerPressed += OnPointerPressed;
        platformView.PointerReleased += OnPointerReleased;
        platformView.PointerCanceled += OnPointerReleased;
        platformView.PointerCaptureLost += OnPointerReleased;
        _host = new WindowsVulkanHost(platformView, this);
        _host.SetScene(VirtualView?.Scene);
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => VirtualView?.RaisePressBegan();

    private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => VirtualView?.RaisePressEnded();

    protected override void DisconnectHandler(SwapChainPanel platformView)
    {
        platformView.PointerPressed -= OnPointerPressed;
        platformView.PointerReleased -= OnPointerReleased;
        platformView.PointerCanceled -= OnPointerReleased;
        platformView.PointerCaptureLost -= OnPointerReleased;
        _host?.Dispose();
        _host = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapScene(VulkanViewHandler handler, global::VulkanView.VulkanView view)
        => handler._host?.SetScene(view.Scene);

    public void RequestFrame() => _host?.RequestFrame();

    internal void EnsureTarget() => _host?.EnsureTarget();

    internal IVulkanScene? CurrentScene => VirtualView?.Scene;

    internal void ReportError(string message) => VirtualView?.ReportError(message);

    internal void ReportDiagnostics(string message)
    {
        if (VirtualView is not null)
            VirtualView.Diagnostics = message;
    }
}
