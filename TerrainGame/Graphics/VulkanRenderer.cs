using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using TerrainGame.Terrain;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkDevice = Silk.NET.Vulkan.Device;
using VkImage = Silk.NET.Vulkan.Image;

namespace TerrainGame.Graphics;

/// <summary>
/// Headless Vulkan renderer for 3D terrain. Draws into an off-screen color
/// image with a depth buffer, copies the result into host-visible memory and
/// exposes the raw pixels to managed code. No window system is used, so the
/// same code runs on Windows and Android.
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private const Format ColorFormat = Format.B8G8R8A8Unorm;

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneUniform
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
    }

    private sealed class ChunkGpu
    {
        public VkBuffer Buffer;
        public DeviceMemory Memory;
        public uint VertexCount;
        public ulong Size;
    }

    private readonly Vk _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private VkDevice _device;
    private Queue _queue;
    private uint _queueFamily;

    private VkImage _colorImage;
    private DeviceMemory _colorMemory;
    private ImageView _colorView;
    private VkImage _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;
    private Format _depthFormat;

    private RenderPass _renderPass;
    private Framebuffer _framebuffer;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;

    private VkBuffer _readbackBuffer;
    private DeviceMemory _readbackMemory;
    private ulong _readbackSize;

    private VkBuffer _uniformBuffer;
    private DeviceMemory _uniformMemory;
    private void* _uniformMapped;
    private const ulong UniformSize = 80;
    private SceneUniform _scene;

    private readonly Dictionary<long, ChunkGpu> _chunks = new();
    private readonly List<(long Key, TerrainVertex[] Vertices)> _pendingUploads = new();
    private readonly List<long> _pendingRemovals = new();

    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Raw BGRA8 pixels, tightly packed, top row first.</summary>
    public byte[] Pixels { get; }

    public int ChunkCount => _chunks.Count;

    public VulkanRenderer(int width = 640, int height = 360)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];

        _vk = Vk.GetApi();
        CreateInstance();
        PickPhysicalDevice();
        CreateDevice();
        CreateColorTarget();
        CreateDepthTarget();
        CreateReadbackBuffer();
        CreateUniformBuffer();
        CreateRenderPass();
        CreateFramebuffer();
        CreateDescriptorResources();
        CreatePipeline();
        CreateCommandResources();
    }

    private void CreateInstance()
    {
        var appName = Marshal.StringToHGlobalAnsi("TerrainGame");
        var engineName = Marshal.StringToHGlobalAnsi("TerrainEngine");
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version10,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };

            var result = _vk.CreateInstance(in createInfo, null, out _instance);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateInstance failed: {result}");
        }
        finally
        {
            Marshal.FreeHGlobal(appName);
            Marshal.FreeHGlobal(engineName);
        }
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0)
            throw new InvalidOperationException("No Vulkan physical devices found.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            _vk.EnumeratePhysicalDevices(_instance, ref count, p);

        foreach (var device in devices)
        {
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* p = families)
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, p);

            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    _physicalDevice = device;
                    _queueFamily = i;
                    break;
                }
            }

            if (_physicalDevice.Handle != 0)
                break;
        }

        if (_physicalDevice.Handle == 0)
            throw new InvalidOperationException("No Vulkan device with a graphics queue was found.");
    }

    private void CreateDevice()
    {
        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
        };

        var result = _vk.CreateDevice(_physicalDevice, in deviceInfo, null, out _device);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDevice failed: {result}");

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
    }

    private void CreateColorTarget()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = ColorFormat,
            Extent = new Extent3D((uint)Width, (uint)Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var result = _vk.CreateImage(_device, in imageInfo, null, out _colorImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage (color) failed: {result}");

        _vk.GetImageMemoryRequirements(_device, _colorImage, out var requirements);
        _colorMemory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        _vk.BindImageMemory(_device, _colorImage, _colorMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _colorImage,
            ViewType = ImageViewType.Type2D,
            Format = ColorFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        result = _vk.CreateImageView(_device, in viewInfo, null, out _colorView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView (color) failed: {result}");
    }

    private void CreateDepthTarget()
    {
        _depthFormat = PickDepthFormat();

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _depthFormat,
            Extent = new Extent3D((uint)Width, (uint)Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var result = _vk.CreateImage(_device, in imageInfo, null, out _depthImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage (depth) failed: {result}");

        _vk.GetImageMemoryRequirements(_device, _depthImage, out var requirements);
        _depthMemory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        _vk.BindImageMemory(_device, _depthImage, _depthMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = _depthFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
        };

        result = _vk.CreateImageView(_device, in viewInfo, null, out _depthView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView (depth) failed: {result}");
    }

    private Format PickDepthFormat()
    {
        ReadOnlySpan<Format> candidates = [Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm];
        foreach (var candidate in candidates)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, candidate, out var properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return candidate;
        }

        throw new InvalidOperationException("No usable depth format found.");
    }

    private void CreateReadbackBuffer()
    {
        _readbackSize = (ulong)Width * (ulong)Height * 4;
        _readbackBuffer = CreateBuffer(_readbackSize, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _readbackMemory);
    }

    private void CreateUniformBuffer()
    {
        _uniformBuffer = CreateBuffer(UniformSize, BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _uniformMemory);

        void* mapped = null;
        var result = _vk.MapMemory(_device, _uniformMemory, 0, UniformSize, 0, &mapped);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (uniform) failed: {result}");

        _uniformMapped = mapped;
    }

    private VkBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        var result = _vk.CreateBuffer(_device, in bufferInfo, null, out var buffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer failed: {result}");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        memory = Allocate(requirements, properties);
        _vk.BindBufferMemory(_device, buffer, memory, 0);
        return buffer;
    }

    private DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties),
        };

        var result = _vk.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory failed: {result}");

        return memory;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memoryProperties);
        for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memoryProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No suitable Vulkan memory type found.");
    }

    private void CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];

        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };

        attachments[1] = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var colorReference = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        var depthReference = new AttachmentReference
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference,
            PDepthStencilAttachment = &depthReference,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        var result = _vk.CreateRenderPass(_device, in renderPassInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass failed: {result}");
    }

    private void CreateFramebuffer()
    {
        var views = stackalloc ImageView[2];
        views[0] = _colorView;
        views[1] = _depthView;

        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = (uint)Width,
            Height = (uint)Height,
            Layers = 1,
        };

        var result = _vk.CreateFramebuffer(_device, in framebufferInfo, null, out _framebuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateFramebuffer failed: {result}");
    }

    private void CreateDescriptorResources()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };

        var result = _vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _descriptorSetLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorSetLayout failed: {result}");

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1,
        };

        result = _vk.CreateDescriptorPool(_device, in poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorPool failed: {result}");

        var setLayout = _descriptorSetLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        result = _vk.AllocateDescriptorSets(_device, in allocateInfo, out _descriptorSet);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateDescriptorSets failed: {result}");

        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = _uniformBuffer,
            Offset = 0,
            Range = UniformSize,
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &bufferInfo,
        };

        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    private void CreatePipeline()
    {
        var vertexCode = ReadShader("TerrainGame.Shaders.terrain.vert.spv");
        var fragmentCode = ReadShader("TerrainGame.Shaders.terrain.frag.spv");

        var vertexModule = CreateShaderModule(vertexCode);
        var fragmentModule = CreateShaderModule(fragmentCode);

        try
        {
            var vertexEntry = Marshal.StringToHGlobalAnsi("VSMain");
            var fragmentEntry = Marshal.StringToHGlobalAnsi("PSMain");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = (byte*)vertexEntry,
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = (byte*)fragmentEntry,
                };

                var binding = new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = TerrainVertex.SizeInBytes,
                    InputRate = VertexInputRate.Vertex,
                };

                var attributes = stackalloc VertexInputAttributeDescription[3];
                attributes[0] = new VertexInputAttributeDescription
                {
                    Location = 0,
                    Binding = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = 0,
                };
                attributes[1] = new VertexInputAttributeDescription
                {
                    Location = 1,
                    Binding = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = 12,
                };
                attributes[2] = new VertexInputAttributeDescription
                {
                    Location = 2,
                    Binding = 0,
                    Format = Format.R32Sfloat,
                    Offset = 24,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 3,
                    PVertexAttributeDescriptions = attributes,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };

                var viewport = new Viewport
                {
                    X = 0,
                    Y = 0,
                    Width = Width,
                    Height = Height,
                    MinDepth = 0,
                    MaxDepth = 1,
                };

                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Width, (uint)Height));

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1.0f,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };

                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = false,
                };

                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = false,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };

                var setLayout = _descriptorSetLayout;
                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = &setLayout,
                };

                var result = _vk.CreatePipelineLayout(_device, in pipelineLayoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreatePipelineLayout failed: {result}");

                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };

                result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _pipeline);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkCreateGraphicsPipelines failed: {result}");
            }
            finally
            {
                Marshal.FreeHGlobal(vertexEntry);
                Marshal.FreeHGlobal(fragmentEntry);
            }
        }
        finally
        {
            _vk.DestroyShaderModule(_device, vertexModule, null);
            _vk.DestroyShaderModule(_device, fragmentModule, null);
        }
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* p = code)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };

            var result = _vk.CreateShaderModule(_device, in moduleInfo, null, out var module);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateShaderModule failed: {result}");

            return module;
        }
    }

    private static byte[] ReadShader(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded shader '{name}' not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void CreateCommandResources()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        var result = _vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool failed: {result}");

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        result = _vk.AllocateCommandBuffers(_device, in allocateInfo, out _commandBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers failed: {result}");
    }

    public void SetChunk(long key, TerrainVertex[] vertices)
    {
        _pendingUploads.Add((key, vertices));
    }

    public void RemoveChunk(long key)
    {
        _pendingRemovals.Add(key);
    }

    public void SetCamera(Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        _scene.ViewProjection = viewProjection;
        _scene.CameraPosition = new Vector4(cameraPosition, 1f);
    }

    private void ProcessPendingChunkWork()
    {
        foreach (var key in _pendingRemovals)
        {
            if (_chunks.Remove(key, out var chunk))
                DestroyChunk(chunk);
        }

        _pendingRemovals.Clear();

        foreach (var (key, vertices) in _pendingUploads)
        {
            if (_chunks.Remove(key, out var existing))
                DestroyChunk(existing);

            if (vertices.Length == 0)
                continue;

            _chunks[key] = CreateChunk(vertices);
        }

        _pendingUploads.Clear();
    }

    private ChunkGpu CreateChunk(TerrainVertex[] vertices)
    {
        var size = (ulong)vertices.Length * TerrainVertex.SizeInBytes;
        var buffer = CreateBuffer(size, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var memory);

        void* mapped = null;
        var result = _vk.MapMemory(_device, memory, 0, size, 0, &mapped);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (chunk) failed: {result}");

        try
        {
            fixed (TerrainVertex* source = vertices)
                System.Buffer.MemoryCopy(source, mapped, (long)size, (long)size);
        }
        finally
        {
            _vk.UnmapMemory(_device, memory);
        }

        return new ChunkGpu
        {
            Buffer = buffer,
            Memory = memory,
            VertexCount = (uint)vertices.Length,
            Size = size,
        };
    }

    private void DestroyChunk(ChunkGpu chunk)
    {
        _vk.DestroyBuffer(_device, chunk.Buffer, null);
        _vk.FreeMemory(_device, chunk.Memory, null);
    }

    /// <summary>
    /// Renders one frame off-screen, copies it to host memory and returns the
    /// pixel array (top row first, BGRA8).
    /// </summary>
    public byte[] Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ProcessPendingChunkWork();

        Marshal.StructureToPtr(_scene, (IntPtr)_uniformMapped, false);

        _vk.ResetCommandBuffer(_commandBuffer, 0);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
        };
        _vk.BeginCommandBuffer(_commandBuffer, in beginInfo);

        var clearValues = stackalloc ClearValue[2];
        clearValues[0] = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0.52f, Float32_1 = 0.68f, Float32_2 = 0.86f, Float32_3 = 1.0f },
        };
        clearValues[1] = new ClearValue
        {
            DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 },
        };

        var renderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Width, (uint)Height));
        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = renderArea,
            ClearValueCount = 2,
            PClearValues = clearValues,
        };

        _vk.CmdBeginRenderPass(_commandBuffer, in renderPassInfo, SubpassContents.Inline);
        _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics, _pipeline);

        var descriptorSet = _descriptorSet;
        _vk.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);

        foreach (var chunk in _chunks.Values)
        {
            var vertexBuffer = chunk.Buffer;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, &vertexBuffer, &offset);
            _vk.CmdDraw(_commandBuffer, chunk.VertexCount, 1, 0, 0);
        }

        _vk.CmdEndRenderPass(_commandBuffer);

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _colorImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        _vk.CmdPipelineBarrier(
            _commandBuffer,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.TransferBit,
            0,
            0, null,
            0, null,
            1, &barrier);

        var copyRegion = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)Width, (uint)Height, 1),
        };

        _vk.CmdCopyImageToBuffer(
            _commandBuffer,
            _colorImage,
            ImageLayout.TransferSrcOptimal,
            _readbackBuffer,
            1,
            &copyRegion);

        _vk.EndCommandBuffer(_commandBuffer);

        var command = _commandBuffer;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command,
        };

        var result = _vk.QueueSubmit(_queue, 1, in submitInfo, default);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit failed: {result}");

        _vk.QueueWaitIdle(_queue);

        void* data = null;
        result = _vk.MapMemory(_device, _readbackMemory, 0, _readbackSize, 0, &data);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (readback) failed: {result}");

        try
        {
            Marshal.Copy((IntPtr)data, Pixels, 0, Pixels.Length);
        }
        finally
        {
            _vk.UnmapMemory(_device, _readbackMemory);
        }

        FlipRows(Pixels, Width, Height);
        return Pixels;
    }

    /// <summary>Vulkan's framebuffer origin is bottom-left; PNG/MAUI expect top-left.</summary>
    private static void FlipRows(byte[] pixels, int width, int height)
    {
        var stride = width * 4;
        var temp = new byte[stride];
        for (var y = 0; y < height / 2; y++)
        {
            var top = y * stride;
            var bottom = (height - 1 - y) * stride;
            System.Buffer.BlockCopy(pixels, top, temp, 0, stride);
            System.Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            System.Buffer.BlockCopy(temp, 0, pixels, bottom, stride);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _vk.DeviceWaitIdle(_device);

        foreach (var chunk in _chunks.Values)
            DestroyChunk(chunk);
        _chunks.Clear();

        _vk.DestroyPipeline(_device, _pipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);

        _vk.UnmapMemory(_device, _uniformMemory);
        _vk.DestroyBuffer(_device, _uniformBuffer, null);
        _vk.FreeMemory(_device, _uniformMemory, null);

        _vk.DestroyBuffer(_device, _readbackBuffer, null);
        _vk.FreeMemory(_device, _readbackMemory, null);

        _vk.DestroyFramebuffer(_device, _framebuffer, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        _vk.DestroyImageView(_device, _depthView, null);
        _vk.DestroyImage(_device, _depthImage, null);
        _vk.FreeMemory(_device, _depthMemory, null);
        _vk.DestroyImageView(_device, _colorView, null);
        _vk.DestroyImage(_device, _colorImage, null);
        _vk.FreeMemory(_device, _colorMemory, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();

        _disposed = true;
    }
}
