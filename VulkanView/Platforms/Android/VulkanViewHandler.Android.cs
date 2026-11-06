using Android.Views;
using Microsoft.Maui.ApplicationModel;
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

    protected override SurfaceView CreatePlatformView()
        => new VulkanSurfaceView(Platform.CurrentActivity ?? MauiContext!.Context);

    protected override void ConnectHandler(SurfaceView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.Visibility = ViewStates.Visible;
        _host = new AndroidVulkanHost(platformView, this);
        if (platformView is VulkanSurfaceView surfaceView)
            surfaceView.SetCallback(_host);
        else
            platformView.Holder?.AddCallback(_host);

        platformView.Holder?.SetFormat(Android.Graphics.Format.Rgba8888);
        ReportDiagnostics("handler connected");
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

    internal IVulkanScene? CurrentScene => VirtualView?.Scene;

    internal void ReportError(string message) => VirtualView?.ReportError(message);

    internal void ReportDiagnostics(string message)
    {
        if (VirtualView is not null)
            VirtualView.Diagnostics = message;
    }

    /// <summary>SurfaceView that reliably registers the holder callback.</summary>
    private sealed class VulkanSurfaceView : SurfaceView
    {
        private ISurfaceHolderCallback? _callback;

        public VulkanSurfaceView(Android.Content.Context context) : base(context)
        {
        }

        public void SetCallback(ISurfaceHolderCallback callback)
        {
            _callback = callback;
            Holder?.AddCallback(callback);
            Holder?.SetFormat(Android.Graphics.Format.Rgba8888);
        }

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            if (_callback is not null)
                Holder?.AddCallback(_callback);
        }
    }
}
