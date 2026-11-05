using Android.Views;
using Microsoft.Maui.Handlers;

namespace VulkanView;

public class VulkanViewHandler : ViewHandler<global::VulkanView.VulkanView, SurfaceView>
{
    private AndroidVulkanHost? _host;

    public static readonly IPropertyMapper<global::VulkanView.VulkanView, VulkanViewHandler> Mapper =
        new PropertyMapper<global::VulkanView.VulkanView, VulkanViewHandler>(ViewMapper)
        {
            [nameof(global::VulkanView.VulkanView.Scene)] = MapScene,
        };

    public VulkanViewHandler() : base(Mapper)
    {
    }

    protected override SurfaceView CreatePlatformView() => new(MauiContext!.Context);

    protected override void ConnectHandler(SurfaceView platformView)
    {
        base.ConnectHandler(platformView);
        _host = new AndroidVulkanHost(platformView, this);
        platformView.Holder?.AddCallback(_host);
        _host.SetScene(VirtualView?.Scene);
    }

    protected override void DisconnectHandler(SurfaceView platformView)
    {
        platformView.Holder?.RemoveCallback(_host);
        _host?.Shutdown();
        _host = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapScene(VulkanViewHandler handler, global::VulkanView.VulkanView view)
        => handler._host?.SetScene(view.Scene);

    public void RequestFrame() => _host?.RequestFrame();

    internal void ReportError(string message) => VirtualView?.ReportError(message);
}
