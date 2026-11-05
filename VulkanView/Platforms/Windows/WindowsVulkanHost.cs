using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using WinRT;
using D3D11Api = Silk.NET.Direct3D11;
using VkFormat = Silk.NET.Vulkan.Format;
using VkImage = Silk.NET.Vulkan.Image;
using DxgiFormat = Silk.NET.DXGI.Format;
using D3D11Device = Silk.NET.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Silk.NET.Direct3D11.ID3D11DeviceContext;
using D3D11Texture2D = Silk.NET.Direct3D11.ID3D11Texture2D;

namespace VulkanView;

/// <summary>
/// Windows host: Vulkan renders into a D3D11 texture shared through an NT handle,
/// then a single GPU CopyResource pushes it into the SwapChainPanel's DXGI
/// swapchain. No CPU copies.
/// </summary>
internal sealed unsafe class WindowsVulkanHost : IDisposable
{
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint SharedResourceReadWrite = 0x80000001;

    private readonly SwapChainPanel _panel;
    private readonly VulkanViewHandler _handler;
    private readonly D3D11Api.D3D11 _d3d11;

    private D3D11Device* _d3dDevice;
    private D3D11DeviceContext* _d3dContext;
    private IDXGIFactory2* _factory;
    private IDXGISwapChain1* _swapchain;
    private ISwapChainPanelNative? _panelNative;

    private VulkanContext? _context;
    private IVulkanScene? _scene;
    private VkImage _image;
    private DeviceMemory _imageMemory;
    private ImageView _imageView;
    private D3D11Texture2D* _sharedTexture;
    private IntPtr _sharedHandle;

    private int _width;
    private int _height;
    private bool _disposed;
    private bool _sizeDirty;

    public WindowsVulkanHost(SwapChainPanel panel, VulkanViewHandler handler)
    {
        _panel = panel;
        _handler = handler;
        _d3d11 = D3D11Api.D3D11.GetApi();
        _panel.SizeChanged += OnPanelSizeChanged;
    }

    public void SetScene(IVulkanScene? scene)
    {
        if (ReferenceEquals(_scene, scene))
            return;

        if (_context is not null)
        {
            try { _scene?.OnTargetDestroyed(); } catch { /* ignore */ }
            DestroyTarget();
            DestroyContext();
        }

        _scene = scene;
        _sizeDirty = true;
    }

    private void OnPanelSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        => _sizeDirty = true;

    public void RequestFrame()
    {
        if (_disposed || _scene is null)
            return;

        try
        {
            EnsureInitialized();
            if (_context is null)
                return;

            var vk = _context.Vk;
            var command = _context.CommandBuffer;

            vk.ResetCommandBuffer(command, 0);
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
            vk.BeginCommandBuffer(command, in begin);

            _scene.Render(new VulkanFrameContext
            {
                CommandBuffer = command,
                ImageIndex = 0,
                Extent = new Extent2D((uint)_width, (uint)_height),
            });

            vk.EndCommandBuffer(command);
            _context.SubmitAndWait();

            Present();
        }
        catch (Exception ex)
        {
            _handler.ReportError(ex.Message);
        }
    }

    private void EnsureInitialized()
    {
        // MAUI reports layout sizes in device pixels here, so use them directly; multiplying by
        // CompositionScale again double-scales the swapchain on high-DPI displays.
        var width = (int)MathF.Max(1f, (float)_panel.ActualWidth);
        var height = (int)MathF.Max(1f, (float)_panel.ActualHeight);

        if (_context is null)
        {
            Initialize(width, height);
            return;
        }

        if (_sizeDirty || width != _width || height != _height)
            Recreate(width, height);
    }

    private void Initialize(int width, int height)
    {
        CreateD3DDevice();
        CreateVulkanContext();
        _width = width;
        _height = height;
        CreateSwapchain(width, height);
        AttachSwapchain();
        CreateSharedResources(width, height);
        _sizeDirty = false;

        _scene?.OnTargetCreated(CreateTargetInfo());
    }

    private void Recreate(int width, int height)
    {
        _scene?.OnTargetDestroyed();
        DestroySharedResources();
        DestroySwapchain();

        _width = width;
        _height = height;
        CreateSwapchain(width, height);
        AttachSwapchain();
        CreateSharedResources(width, height);
        _sizeDirty = false;

        _scene?.OnTargetResized(CreateTargetInfo());
    }

    private VulkanTargetInfo CreateTargetInfo() => new()
    {
        Context = _context!,
        Format = VkFormat.B8G8R8A8Unorm,
        Extent = new Extent2D((uint)_width, (uint)_height),
        FinalLayout = ImageLayout.ColorAttachmentOptimal,
        ImageViews = new[] { _imageView },
        FlipY = true,
    };

    private void CreateD3DDevice()
    {
        D3D11Device* device;
        D3D11DeviceContext* context;
        D3DFeatureLevel level;

        var result = _d3d11.CreateDevice(
            null,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            (uint)D3D11Api.CreateDeviceFlag.BgraSupport,
            null,
            0,
            D3D11Api.D3D11.SdkVersion,
            &device,
            &level,
            &context);

        if (result < 0)
            throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{result:X8}");

        _d3dDevice = device;
        _d3dContext = context;

        var dxgiIid = IDXGIDevice.Guid;
        IDXGIDevice* dxgiDevice;
        if (_d3dDevice->QueryInterface(&dxgiIid, (void**)&dxgiDevice) < 0)
            throw new InvalidOperationException("IDXGIDevice is not available.");

        IDXGIAdapter* adapter;
        if (dxgiDevice->GetAdapter(&adapter) < 0)
            throw new InvalidOperationException("Could not get the DXGI adapter.");

        var factoryIid = IDXGIFactory2.Guid;
        void* factory;
        if (adapter->GetParent(&factoryIid, &factory) < 0)
            throw new InvalidOperationException("IDXGIFactory2 is not available.");

        _factory = (IDXGIFactory2*)factory;
        adapter->Release();
        dxgiDevice->Release();
    }

    private void CreateVulkanContext()
    {
        _context = new VulkanContext(
            instanceExtensions: new[] { "VK_KHR_external_memory_capabilities" },
            deviceExtensions: new[] { "VK_KHR_external_memory_win32" });
    }

    private void CreateSwapchain(int width, int height)
    {
        var desc = new SwapChainDesc1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = DxgiFormat.FormatB8G8R8A8Unorm,
            Stereo = false,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DxgiUsageRenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = 0,
        };

        IDXGISwapChain1* swapchain;
        var result = _factory->CreateSwapChainForComposition(
            (IUnknown*)(void*)_d3dDevice,
            &desc,
            null,
            &swapchain);

        if (result < 0)
            throw new InvalidOperationException($"CreateSwapChainForComposition failed: 0x{result:X8}");

        _swapchain = swapchain;
    }

    private void AttachSwapchain()
    {
        _panelNative ??= _panel.As<ISwapChainPanelNative>();
        var hr = _panelNative.SetSwapChain((IntPtr)_swapchain);
        if (hr < 0)
            throw new InvalidOperationException($"ISwapChainPanelNative.SetSwapChain failed: 0x{hr:X8}");
    }

    private void CreateSharedResources(int width, int height)
    {
        var textureDesc = new D3D11Api.Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.FormatB8G8R8A8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11Api.Usage.UsageDefault,
            BindFlags = (uint)D3D11Api.BindFlag.BindRenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = (uint)D3D11Api.ResourceMiscFlag.ResourceMiscShared |
                        (uint)D3D11Api.ResourceMiscFlag.ResourceMiscSharedNthandle,
        };

        D3D11Texture2D* texture;
        var result = _d3dDevice->CreateTexture2D(&textureDesc, null, &texture);
        if (result < 0)
            throw new InvalidOperationException($"CreateTexture2D failed: 0x{result:X8} size={width}x{height}");
        _sharedTexture = texture;

        var resourceIid = IDXGIResource1.Guid;
        IDXGIResource1* resource;
        if (texture->QueryInterface(&resourceIid, (void**)&resource) < 0)
            throw new InvalidOperationException("IDXGIResource1 is not available.");

        void* handle;
        result = resource->CreateSharedHandle(null, SharedResourceReadWrite, (char*)null, &handle);
        resource->Release();
        if (result < 0)
            throw new InvalidOperationException($"CreateSharedHandle failed: 0x{result:X8}");

        _sharedHandle = (IntPtr)handle;
        CreateVulkanImage(width, height);
    }

    private void CreateVulkanImage(int width, int height)
    {
        var context = _context!;
        var vk = context.Vk;

        if (!vk.TryGetDeviceExtension(context.Instance, context.Device, out KhrExternalMemoryWin32 win32))
            throw new InvalidOperationException("VK_KHR_external_memory_win32 is not available.");

        var externalInfo = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
        };

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &externalInfo,
            ImageType = ImageType.Type2D,
            Format = VkFormat.B8G8R8A8Unorm,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var result = vk.CreateImage(context.Device, in imageInfo, null, out _image);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage (imported) failed: {result}");

        vk.GetImageMemoryRequirements(context.Device, _image, out var requirements);

        var handleProperties = new MemoryWin32HandlePropertiesKHR
        {
            SType = StructureType.MemoryWin32HandlePropertiesKhr,
        };

        result = win32.GetMemoryWin32HandleProperties(
            context.Device,
            ExternalMemoryHandleTypeFlags.D3D11TextureBit,
            _sharedHandle,
            &handleProperties);

        if (result != Result.Success)
            throw new InvalidOperationException($"vkGetMemoryWin32HandlePropertiesKHR failed: {result}");

        var importInfo = new ImportMemoryWin32HandleInfoKHR
        {
            SType = StructureType.ImportMemoryWin32HandleInfoKhr,
            HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
            Handle = _sharedHandle,
        };

        var dedicated = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            PNext = &importInfo,
            Image = _image,
        };

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &dedicated,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = context.FindMemoryType(handleProperties.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        result = vk.AllocateMemory(context.Device, in allocateInfo, null, out _imageMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory (imported) failed: {result}");

        vk.BindImageMemory(context.Device, _image, _imageMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = VkFormat.B8G8R8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        result = vk.CreateImageView(context.Device, in viewInfo, null, out _imageView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView (imported) failed: {result}");
    }

    private void Present()
    {
        var iid = D3D11Texture2D.Guid;
        D3D11Texture2D* backbuffer;
        if (_swapchain->GetBuffer(0, &iid, (void**)&backbuffer) < 0)
            throw new InvalidOperationException("Could not get the swapchain backbuffer.");

        _d3dContext->CopyResource(
            (D3D11Api.ID3D11Resource*)backbuffer,
            (D3D11Api.ID3D11Resource*)_sharedTexture);

        backbuffer->Release();

        _swapchain->Present(1, 0);
    }

    private void DestroySharedResources()
    {
        var vk = _context?.Vk;
        if (vk is not null)
        {
            if (_imageView.Handle != 0)
                vk.DestroyImageView(_context!.Device, _imageView, null);
            if (_image.Handle != 0)
                vk.DestroyImage(_context!.Device, _image, null);
            if (_imageMemory.Handle != 0)
                vk.FreeMemory(_context!.Device, _imageMemory, null);
        }

        _imageView = default;
        _image = default;
        _imageMemory = default;

        if (_sharedTexture is not null)
        {
            _sharedTexture->Release();
            _sharedTexture = null;
        }

        if (_sharedHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_sharedHandle);
            _sharedHandle = IntPtr.Zero;
        }
    }

    private void DestroySwapchain()
    {
        if (_swapchain is not null)
        {
            _swapchain->Release();
            _swapchain = null;
        }
    }

    private void DestroyTarget()
    {
        DestroySharedResources();
        DestroySwapchain();
    }

    private void DestroyContext()
    {
        _context?.Dispose();
        _context = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _panel.SizeChanged -= OnPanelSizeChanged;

        try { _scene?.OnTargetDestroyed(); } catch { /* ignore */ }

        DestroyTarget();
        DestroyContext();

        if (_factory is not null)
        {
            _factory->Release();
            _factory = null;
        }

        if (_d3dContext is not null)
        {
            _d3dContext->Release();
            _d3dContext = null;
        }

        if (_d3dDevice is not null)
        {
            _d3dDevice->Release();
            _d3dDevice = null;
        }
    }

    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }
}
