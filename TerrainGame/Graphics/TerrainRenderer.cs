using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using SkiaSharp;
using TerrainGame.Terrain;
using VulkanView;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkDevice = Silk.NET.Vulkan.Device;
using VkImage = Silk.NET.Vulkan.Image;
using VkFormat = Silk.NET.Vulkan.Format;

namespace TerrainGame.Graphics;

/// <summary>
/// Terrain scene rendered into the VulkanView target. Owns pipelines, textures,
/// chunk buffers and the depth buffer; the control owns the device and presents.
/// </summary>
public sealed unsafe class TerrainRenderer : IVulkanScene, IDisposable
{
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
    }

    private const uint TextureSize = 256;
    private const uint TextureLayerCount = 4;
    private const ulong UniformSize = 80;

    private readonly VulkanContext _context;
    private readonly Dictionary<long, ChunkGpu> _chunks = new();
    private readonly List<(long Key, TerrainVertex[] Vertices)> _pendingUploads = new();
    private readonly List<long> _pendingRemovals = new();

    private RenderPass _renderPass;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private VkImage _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;
    private Format _depthFormat;
    private bool _hasTarget;

    private VkBuffer _uniformBuffer;
    private DeviceMemory _uniformMemory;
    private void* _uniformMapped;
    private SceneUniform _scene;

    private VkImage _textureImage;
    private DeviceMemory _textureMemory;
    private ImageView _textureView;
    private Sampler _textureSampler;

    private bool _disposed;

    public TerrainRenderer(VulkanContext context)
    {
        _context = context;
        CreateUniformBuffer();
        CreateTextures();
        CreateDescriptorResources();
    }

    public int ChunkCount => _chunks.Count;
    public int RenderCalls { get; private set; }
    public bool HasTarget => _hasTarget;

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

    // ---- IVulkanScene -------------------------------------------------

    public void OnTargetCreated(VulkanTargetInfo target) => CreateTargetResources(target);

    public void OnTargetResized(VulkanTargetInfo target)
    {
        DestroyTargetResources();
        CreateTargetResources(target);
    }

    public void OnTargetDestroyed()
    {
        DestroyTargetResources();
        _hasTarget = false;
    }

    public void Render(VulkanFrameContext frame)
    {
        RenderCalls++;
        if (!_hasTarget)
            return;

        ProcessPendingChunkWork();
        Marshal.StructureToPtr(_scene, (IntPtr)_uniformMapped, false);

        var vk = _context.Vk;
        var command = frame.CommandBuffer;
        var clearValues = stackalloc ClearValue[2];
        clearValues[0] = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0.52f, Float32_1 = 0.68f, Float32_2 = 0.86f, Float32_3 = 1f },
        };
        clearValues[1] = new ClearValue
        {
            DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 },
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[frame.ImageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), frame.Extent),
            ClearValueCount = 2,
            PClearValues = clearValues,
        };

        vk.CmdBeginRenderPass(command, in renderPassInfo, SubpassContents.Inline);
        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        var descriptorSet = _descriptorSet;
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);

        foreach (var chunk in _chunks.Values)
        {
            var vertexBuffer = chunk.Buffer;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(command, 0, 1, &vertexBuffer, &offset);
            vk.CmdDraw(command, chunk.VertexCount, 1, 0, 0);
        }

        vk.CmdEndRenderPass(command);
    }

    // ---- target resources ---------------------------------------------

    private void CreateTargetResources(VulkanTargetInfo target)
    {
        if (_hasTarget)
            DestroyTargetResources();

        _depthFormat = PickDepthFormat();
        CreateDepthImage(target.Extent);
        CreateRenderPass(target);
        CreateFramebuffers(target);
        CreatePipeline(target);

        _hasTarget = true;
    }

    private Format PickDepthFormat()
    {
        ReadOnlySpan<Format> candidates = [Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm];
        foreach (var candidate in candidates)
        {
            _context.Vk.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, candidate, out var properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return candidate;
        }

        throw new InvalidOperationException("No usable depth format found.");
    }

    private void CreateDepthImage(Extent2D extent)
    {
        var vk = _context.Vk;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _depthFormat,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var result = vk.CreateImage(_context.Device, in imageInfo, null, out _depthImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage (depth) failed: {result}");

        vk.GetImageMemoryRequirements(_context.Device, _depthImage, out var requirements);
        _depthMemory = _context.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        vk.BindImageMemory(_context.Device, _depthImage, _depthMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = _depthFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
        };

        result = vk.CreateImageView(_context.Device, in viewInfo, null, out _depthView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView (depth) failed: {result}");
    }

    private void CreateRenderPass(VulkanTargetInfo target)
    {
        var vk = _context.Vk;
        var attachments = stackalloc AttachmentDescription[2];

        attachments[0] = new AttachmentDescription
        {
            Format = target.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = target.FinalLayout,
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

        var colorReference = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var depthReference = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };

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

        var result = vk.CreateRenderPass(_context.Device, in renderPassInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass failed: {result}");
    }

    private void CreateFramebuffers(VulkanTargetInfo target)
    {
        var vk = _context.Vk;
        _framebuffers = new Framebuffer[target.ImageViews.Count];

        for (var i = 0; i < target.ImageViews.Count; i++)
        {
            var views = stackalloc ImageView[2];
            views[0] = target.ImageViews[i];
            views[1] = _depthView;

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 2,
                PAttachments = views,
                Width = target.Extent.Width,
                Height = target.Extent.Height,
                Layers = 1,
            };

            var result = vk.CreateFramebuffer(_context.Device, in framebufferInfo, null, out _framebuffers[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateFramebuffer failed: {result}");
        }
    }

    private void CreatePipeline(VulkanTargetInfo target)
    {
        var vk = _context.Vk;
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
                    Width = target.Extent.Width,
                    Height = target.Extent.Height,
                    MinDepth = 0,
                    MaxDepth = 1,
                };

                var scissor = new Rect2D(new Offset2D(0, 0), target.Extent);

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

                var result = vk.CreatePipelineLayout(_context.Device, in pipelineLayoutInfo, null, out _pipelineLayout);
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

                result = vk.CreateGraphicsPipelines(_context.Device, default, 1, in pipelineInfo, null, out _pipeline);
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
            vk.DestroyShaderModule(_context.Device, vertexModule, null);
            vk.DestroyShaderModule(_context.Device, fragmentModule, null);
        }
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* pointer = code)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)pointer,
            };

            var result = _context.Vk.CreateShaderModule(_context.Device, in moduleInfo, null, out var module);
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

    // ---- uniform / textures / descriptors -----------------------------

    private void CreateUniformBuffer()
    {
        _uniformBuffer = _context.CreateBuffer(UniformSize, BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _uniformMemory);

        void* mapped = null;
        var result = _context.Vk.MapMemory(_context.Device, _uniformMemory, 0, UniformSize, 0, &mapped);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (uniform) failed: {result}");
        _uniformMapped = mapped;
    }

    private void CreateTextures()
    {
        var layers = new[]
        {
            LoadTextureLayer("TerrainGame.Textures.grass.png"),
            LoadTextureLayer("TerrainGame.Textures.sand.png"),
            LoadTextureLayer("TerrainGame.Textures.dirt.png"),
            LoadTextureLayer("TerrainGame.Textures.stone.png"),
        };

        var vk = _context.Vk;
        var layerBytes = (int)(TextureSize * TextureSize * 4);
        var bufferSize = (ulong)(layerBytes * (int)TextureLayerCount);

        var staging = _context.CreateBuffer(bufferSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var stagingMemory);

        void* mapped = null;
        var result = vk.MapMemory(_context.Device, stagingMemory, 0, bufferSize, 0, &mapped);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (textures) failed: {result}");

        try
        {
            for (var i = 0; i < layers.Length; i++)
                Marshal.Copy(layers[i], 0, (IntPtr)((byte*)mapped + i * layerBytes), layerBytes);
        }
        finally
        {
            vk.UnmapMemory(_context.Device, stagingMemory);
        }

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = VkFormat.R8G8B8A8Unorm,
            Extent = new Extent3D(TextureSize, TextureSize, 1),
            MipLevels = 1,
            ArrayLayers = TextureLayerCount,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        result = vk.CreateImage(_context.Device, in imageInfo, null, out _textureImage);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage (textures) failed: {result}");

        vk.GetImageMemoryRequirements(_context.Device, _textureImage, out var requirements);
        _textureMemory = _context.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        vk.BindImageMemory(_context.Device, _textureImage, _textureMemory, 0);

        var command = _context.CommandBuffer;
        vk.ResetCommandBuffer(command, 0);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        vk.BeginCommandBuffer(command, in beginInfo);

        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _textureImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, TextureLayerCount),
            DstAccessMask = AccessFlags.TransferWriteBit,
        };

        vk.CmdPipelineBarrier(command,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0,
            0, null, 0, null, 1, &toTransfer);

        var regions = stackalloc BufferImageCopy[(int)TextureLayerCount];
        for (var i = 0; i < TextureLayerCount; i++)
        {
            regions[i] = new BufferImageCopy
            {
                BufferOffset = (ulong)(i * layerBytes),
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)i, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(TextureSize, TextureSize, 1),
            };
        }

        vk.CmdCopyBufferToImage(command, staging, _textureImage,
            ImageLayout.TransferDstOptimal, TextureLayerCount, regions);

        var toShaderRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _textureImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, TextureLayerCount),
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };

        vk.CmdPipelineBarrier(command,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0,
            0, null, 0, null, 1, &toShaderRead);

        vk.EndCommandBuffer(command);
        _context.SubmitAndWait();

        vk.DestroyBuffer(_context.Device, staging, null);
        vk.FreeMemory(_context.Device, stagingMemory, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _textureImage,
            ViewType = ImageViewType.Type2DArray,
            Format = VkFormat.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, TextureLayerCount),
        };

        result = vk.CreateImageView(_context.Device, in viewInfo, null, out _textureView);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView (textures) failed: {result}");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = false,
            MaxAnisotropy = 1f,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatTransparentBlack,
            UnnormalizedCoordinates = false,
        };

        result = vk.CreateSampler(_context.Device, in samplerInfo, null, out _textureSampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSampler failed: {result}");
    }

    private static byte[] LoadTextureLayer(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded texture '{resourceName}' not found.");
        using var source = SKBitmap.Decode(stream)
                          ?? throw new InvalidOperationException($"Failed to decode '{resourceName}'.");

        var info = new SKImageInfo((int)TextureSize, (int)TextureSize, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var resized = new SKBitmap(info);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            canvas.DrawBitmap(source, new SKRect(0, 0, TextureSize, TextureSize), sampling);
        }

        var bytes = new byte[(int)(TextureSize * TextureSize * 4)];
        Marshal.Copy(resized.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    private void CreateDescriptorResources()
    {
        var vk = _context.Vk;
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        bindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.Sampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };

        var result = vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _descriptorSetLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorSetLayout failed: {result}");

        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = 1 };
        poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = 1 };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
            MaxSets = 1,
        };

        result = vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _descriptorPool);
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

        result = vk.AllocateDescriptorSets(_context.Device, in allocateInfo, out _descriptorSet);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateDescriptorSets failed: {result}");

        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = _uniformBuffer,
            Offset = 0,
            Range = UniformSize,
        };

        var imageInfo = new DescriptorImageInfo
        {
            Sampler = default,
            ImageView = _textureView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var samplerInfo = new DescriptorImageInfo
        {
            Sampler = _textureSampler,
            ImageView = default,
            ImageLayout = ImageLayout.Undefined,
        };

        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer,
            PBufferInfo = &bufferInfo,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = &imageInfo,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = &samplerInfo,
        };

        vk.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    // ---- chunk buffers -------------------------------------------------

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

            if (vertices.Length > 0)
                _chunks[key] = CreateChunk(vertices);
        }

        _pendingUploads.Clear();
    }

    private ChunkGpu CreateChunk(TerrainVertex[] vertices)
    {
        var vk = _context.Vk;
        var size = (ulong)vertices.Length * TerrainVertex.SizeInBytes;
        var buffer = _context.CreateBuffer(size, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out var memory);

        void* mapped = null;
        var result = vk.MapMemory(_context.Device, memory, 0, size, 0, &mapped);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkMapMemory (chunk) failed: {result}");

        try
        {
            fixed (TerrainVertex* source = vertices)
                System.Buffer.MemoryCopy(source, mapped, (long)size, (long)size);
        }
        finally
        {
            vk.UnmapMemory(_context.Device, memory);
        }

        return new ChunkGpu { Buffer = buffer, Memory = memory, VertexCount = (uint)vertices.Length };
    }

    private void DestroyChunk(ChunkGpu chunk)
    {
        _context.Vk.DestroyBuffer(_context.Device, chunk.Buffer, null);
        _context.Vk.FreeMemory(_context.Device, chunk.Memory, null);
    }

    // ---- teardown ------------------------------------------------------

    private void DestroyTargetResources()
    {
        var vk = _context.Vk;

        if (_pipeline.Handle != 0)
        {
            vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
        }

        if (_pipelineLayout.Handle != 0)
        {
            vk.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            _pipelineLayout = default;
        }

        foreach (var framebuffer in _framebuffers)
        {
            if (framebuffer.Handle != 0)
                vk.DestroyFramebuffer(_context.Device, framebuffer, null);
        }

        _framebuffers = Array.Empty<Framebuffer>();

        if (_renderPass.Handle != 0)
        {
            vk.DestroyRenderPass(_context.Device, _renderPass, null);
            _renderPass = default;
        }

        if (_depthView.Handle != 0)
        {
            vk.DestroyImageView(_context.Device, _depthView, null);
            _depthView = default;
        }

        if (_depthImage.Handle != 0)
        {
            vk.DestroyImage(_context.Device, _depthImage, null);
            _depthImage = default;
        }

        if (_depthMemory.Handle != 0)
        {
            vk.FreeMemory(_context.Device, _depthMemory, null);
            _depthMemory = default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var vk = _context.Vk;
        vk.DeviceWaitIdle(_context.Device);

        foreach (var chunk in _chunks.Values)
            DestroyChunk(chunk);
        _chunks.Clear();

        DestroyTargetResources();

        if (_descriptorPool.Handle != 0)
            vk.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_descriptorSetLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(_context.Device, _descriptorSetLayout, null);

        if (_textureSampler.Handle != 0)
            vk.DestroySampler(_context.Device, _textureSampler, null);
        if (_textureView.Handle != 0)
            vk.DestroyImageView(_context.Device, _textureView, null);
        if (_textureImage.Handle != 0)
            vk.DestroyImage(_context.Device, _textureImage, null);
        if (_textureMemory.Handle != 0)
            vk.FreeMemory(_context.Device, _textureMemory, null);

        if (_uniformMapped is not null)
            vk.UnmapMemory(_context.Device, _uniformMemory);
        if (_uniformBuffer.Handle != 0)
            vk.DestroyBuffer(_context.Device, _uniformBuffer, null);
        if (_uniformMemory.Handle != 0)
            vk.FreeMemory(_context.Device, _uniformMemory, null);
    }
}
