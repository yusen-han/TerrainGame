using System;
using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Views;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using AndroidFormat = Android.Graphics.Format;
using VkFormat = Silk.NET.Vulkan.Format;

namespace VulkanView;

/// <summary>
/// Android host: a real VkSwapchainKHR on the SurfaceView's native window.
/// Rendering goes straight into swapchain images; presentation is zero-copy.
/// </summary>
internal sealed unsafe class AndroidVulkanHost : Java.Lang.Object, ISurfaceHolderCallback
{
    private readonly SurfaceView _view;
    private readonly VulkanViewHandler _handler;

    private VulkanContext? _context;
    private IVulkanScene? _scene;
    private KhrSurface? _surfaceApi;
    private KhrSwapchain? _swapchainApi;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private VkFormat _format = VkFormat.B8G8R8A8Unorm;
    private Extent2D _extent;
    private ImageView[] _imageViews = Array.Empty<ImageView>();
    private IntPtr _nativeWindow;
    private bool _disposed;

    private static void Log(string message)
    {
        try { Android.Util.Log.Info("TerrainGame.Vulkan", message); } catch { }
    }

    static AndroidVulkanHost()
    {
        try
        {
            Java.Lang.JavaSystem.LoadLibrary("android");
        }
        catch
        {
            // libandroid is usually resolvable by name on the NDK layout.
        }
    }

    public AndroidVulkanHost(SurfaceView view, VulkanViewHandler handler)
    {
        _view = view;
        _handler = handler;
    }

    public void SetScene(IVulkanScene? scene)
    {
        if (ReferenceEquals(_scene, scene))
            return;

        _scene?.OnTargetDestroyed();
        _scene = scene;

        if (_context is not null && _scene is not null)
            _scene.OnTargetCreated(CreateTargetInfo());
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        Log("SurfaceCreated");
        try
        {
            if (_nativeWindow == IntPtr.Zero)
                _nativeWindow = ANativeWindowFromSurface(JNIEnv.Handle, holder.Surface!.Handle);

            Log($"nativeWindow=0x{_nativeWindow.ToInt64():X}");
            if (_nativeWindow == IntPtr.Zero)
            {
                _handler.ReportError("ANativeWindow_fromSurface returned null.");
                return;
            }

            EnsureInitialized();
        }
        catch (Exception ex)
        {
            Log("RequestFrame failed: " + ex);
            _handler.ReportError(ex.Message);
        }
    }

    public void SurfaceChanged(ISurfaceHolder holder, AndroidFormat format, int width, int height)
    {
        try
        {
            if (_context is not null && (_view.Width != _extent.Width || _view.Height != _extent.Height))
                RecreateSwapchain();
        }
        catch (Exception ex)
        {
            Log("RequestFrame failed: " + ex);
            _handler.ReportError(ex.Message);
        }
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        DestroyTarget();
        DestroyContext();

        if (_nativeWindow != IntPtr.Zero)
        {
            ANativeWindowRelease(_nativeWindow);
            _nativeWindow = IntPtr.Zero;
        }
    }

    private void EnsureInitialized()
    {
        if (_context is not null)
            return;

        if (_nativeWindow == IntPtr.Zero)
        {
            Log("EnsureInitialized: native window not ready");
            return;
        }

        Log("EnsureInitialized: creating context");

        var vkApi = Silk.NET.Vulkan.Vk.GetApi();
        _context = new VulkanContext(
            instanceExtensions: new[] { "VK_KHR_surface", "VK_KHR_android_surface" },
            deviceExtensions: new[] { "VK_KHR_swapchain" },
            onInstanceCreated: instance =>
            {
                if (!vkApi.TryGetInstanceExtension(instance, out KhrSurface surfaceApi))
                    throw new InvalidOperationException("VK_KHR_surface is not available.");
                _surfaceApi = surfaceApi;
                if (!vkApi.TryGetInstanceExtension(instance, out KhrAndroidSurface androidSurface))
                    throw new InvalidOperationException("VK_KHR_android_surface is not available.");

                var createInfo = new AndroidSurfaceCreateInfoKHR
                {
                    SType = StructureType.AndroidSurfaceCreateInfoKhr,
                    Window = (nint*)_nativeWindow,
                };

                var result = androidSurface.CreateAndroidSurface(instance, in createInfo, null, out _surface);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateAndroidSurfaceKHR failed: {result}");
            },
            queueFilter: (physicalDevice, family) =>
            {
                _surfaceApi!.GetPhysicalDeviceSurfaceSupport(
                    physicalDevice, family, _surface, out var supported);
                return supported;
            });

        if (!_context.Vk.TryGetDeviceExtension(_context.Instance, _context.Device, out KhrSwapchain swapchainApi))
            throw new InvalidOperationException("VK_KHR_swapchain is not available.");
        _swapchainApi = swapchainApi;

        CreateSwapchain();
        Log($"swapchain {_extent.Width}x{_extent.Height} format={_format}");
        _scene?.OnTargetCreated(CreateTargetInfo());
    }

    private void CreateSwapchain()
    {
        var context = _context!;
        var vk = context.Vk;

        _surfaceApi!.GetPhysicalDeviceSurfaceCapabilities(
            context.PhysicalDevice, _surface, out var capabilities);

        uint formatCount = 0;
        _surfaceApi.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, _surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* pointer = formats)
            _surfaceApi.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, _surface, ref formatCount, pointer);

        var selected = formats.Length > 0 ? formats[0] : new SurfaceFormatKHR();
        foreach (var candidate in formats)
        {
            if (candidate.Format == VkFormat.B8G8R8A8Unorm || candidate.Format == VkFormat.R8G8B8A8Unorm)
            {
                selected = candidate;
                break;
            }
        }

        _format = selected.Format;

        var extent = capabilities.CurrentExtent;
        if (extent.Width == uint.MaxValue)
            extent = new Extent2D((uint)Math.Max(1, _view.Width), (uint)Math.Max(1, _view.Height));
        _extent = extent;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = capabilities.MinImageCount + 1,
            ImageFormat = _format,
            ImageColorSpace = selected.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };

        var result = _swapchainApi!.CreateSwapchain(context.Device, in createInfo, null, out _swapchain);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSwapchainKHR failed: {result}");

        uint imageCount = 0;
        _swapchainApi.GetSwapchainImages(context.Device, _swapchain, ref imageCount, null);
        var images = new Silk.NET.Vulkan.Image[imageCount];
        fixed (Silk.NET.Vulkan.Image* pointer = images)
            _swapchainApi.GetSwapchainImages(context.Device, _swapchain, ref imageCount, pointer);

        _imageViews = new ImageView[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[i],
                ViewType = ImageViewType.Type2D,
                Format = _format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            result = vk.CreateImageView(context.Device, in viewInfo, null, out _imageViews[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateImageView failed: {result}");
        }
    }

    private VulkanTargetInfo CreateTargetInfo() => new()
    {
        Context = _context!,
        Format = _format,
        Extent = _extent,
        FinalLayout = ImageLayout.PresentSrcKhr,
        ImageViews = _imageViews,
    };

    public void RequestFrame()
    {
        if (_disposed)
            return;

        try
        {
            if (_context is null)
                EnsureInitialized();

            if (_context is null || _scene is null || _swapchain.Handle == 0)
                return;

            var context = _context;
            var vk = context.Vk;

            uint imageIndex;
            var acquire = _swapchainApi!.AcquireNextImage(
                context.Device, _swapchain, ulong.MaxValue, default, default, &imageIndex);

            if (acquire is Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchain();
                return;
            }

            if (acquire is not (Result.Success or Result.SuboptimalKhr))
                return;

            var command = context.CommandBuffer;
            vk.ResetCommandBuffer(command, 0);
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
            vk.BeginCommandBuffer(command, in begin);

            _scene.Render(new VulkanFrameContext
            {
                CommandBuffer = command,
                ImageIndex = (int)imageIndex,
                Extent = _extent,
            });

            vk.EndCommandBuffer(command);
            context.SubmitAndWait();

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };

            var present = _swapchainApi.QueuePresent(context.Queue, in presentInfo);
            if (present is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
                RecreateSwapchain();

            vk.QueueWaitIdle(context.Queue);
        }
        catch (Exception ex)
        {
            Log("RequestFrame failed: " + ex);
            _handler.ReportError(ex.Message);
        }
    }

    private void RecreateSwapchain()
    {
        if (_context is null)
            return;

        _scene?.OnTargetDestroyed();
        DestroySwapchain();
        CreateSwapchain();
        _scene?.OnTargetResized(CreateTargetInfo());
    }

    private void DestroySwapchain()
    {
        var vk = _context?.Vk;
        if (vk is not null)
        {
            foreach (var view in _imageViews)
            {
                if (view.Handle != 0)
                    vk.DestroyImageView(_context!.Device, view, null);
            }
        }

        _imageViews = Array.Empty<ImageView>();

        if (_swapchain.Handle != 0)
        {
            _swapchainApi?.DestroySwapchain(_context!.Device, _swapchain, null);
            _swapchain = default;
        }
    }

    private void DestroyTarget()
    {
        DestroySwapchain();

        if (_surface.Handle != 0 && _context is not null)
        {
            _surfaceApi?.DestroySurface(_context.Instance, _surface, null);
            _surface = default;
        }
    }

    private void DestroyContext()
    {
        _context?.Dispose();
        _context = null;
        _surfaceApi = null;
        _swapchainApi = null;
        _imageViews = Array.Empty<ImageView>();
    }

    public void Shutdown()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _scene?.OnTargetDestroyed(); } catch { /* ignore */ }
        DestroyTarget();
        DestroyContext();

        if (_nativeWindow != IntPtr.Zero)
        {
            ANativeWindowRelease(_nativeWindow);
            _nativeWindow = IntPtr.Zero;
        }

        base.Dispose();
    }

    [DllImport("libandroid.so", EntryPoint = "ANativeWindow_fromSurface")]
    private static extern IntPtr ANativeWindowFromSurface(IntPtr env, IntPtr surface);

    [DllImport("libandroid.so", EntryPoint = "ANativeWindow_release")]
    private static extern void ANativeWindowRelease(IntPtr window);
}
