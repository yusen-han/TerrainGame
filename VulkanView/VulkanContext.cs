using System;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Device = Silk.NET.Vulkan.Device;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkDevice = Silk.NET.Vulkan.Device;
using VkInstance = Silk.NET.Vulkan.Instance;
using VkPhysicalDevice = Silk.NET.Vulkan.PhysicalDevice;
using VkQueue = Silk.NET.Vulkan.Queue;

namespace VulkanView;

/// <summary>
/// Vulkan instance, device, queue and command pool shared by the control and the
/// hosted scene. Creating it is platform-independent; surfaces/swapchains are
/// created by the platform target classes.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    public Vk Vk { get; }
    public Instance Instance { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public Queue Queue { get; }
    public uint QueueFamily { get; }
    public CommandPool CommandPool { get; }
    public CommandBuffer CommandBuffer { get; }

    private bool _disposed;

    public VulkanContext(
        string[] instanceExtensions,
        string[] deviceExtensions,
        Action<Instance>? onInstanceCreated = null,
        Func<PhysicalDevice, uint, bool>? queueFilter = null)
    {
        Vk = Vk.GetApi();
        Instance = CreateInstance(instanceExtensions);
        onInstanceCreated?.Invoke(Instance);
        (PhysicalDevice, QueueFamily) = PickPhysicalDevice(queueFilter);
        Device = CreateDevice(deviceExtensions);

        Vk.GetDeviceQueue(Device, QueueFamily, 0, out var queue);
        Queue = queue;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        var result = Vk.CreateCommandPool(Device, in poolInfo, null, out var commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool failed: {result}");
        CommandPool = commandPool;

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        result = Vk.AllocateCommandBuffers(Device, in allocateInfo, out var commandBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateCommandBuffers failed: {result}");
        CommandBuffer = commandBuffer;
    }

    private Instance CreateInstance(string[] extensions)
    {
        var appName = Marshal.StringToHGlobalAnsi("VulkanView");
        var engineName = Marshal.StringToHGlobalAnsi("VulkanView");
        byte** extensionNames = null;

        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version11,
            };

            extensionNames = ToStringArray(extensions, out _);

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = extensionNames,
            };

            var result = Vk.CreateInstance(in createInfo, null, out var instance);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateInstance failed: {result}");

            return instance;
        }
        finally
        {
            if (extensionNames is not null)
                FreeStringArray(extensionNames, extensions.Length);
            Marshal.FreeHGlobal(appName);
            Marshal.FreeHGlobal(engineName);
        }
    }

    private (PhysicalDevice Device, uint Family) PickPhysicalDevice(Func<PhysicalDevice, uint, bool>? queueFilter)
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
            throw new InvalidOperationException("No Vulkan physical devices found.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
            Vk.EnumeratePhysicalDevices(Instance, ref count, pointer);

        foreach (var device in devices)
        {
            uint familyCount = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pointer = families)
                Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref familyCount, pointer);

            for (uint i = 0; i < familyCount; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
                    continue;

                if (queueFilter is not null && !queueFilter(device, i))
                    continue;

                return (device, i);
            }
        }

        throw new InvalidOperationException("No Vulkan device with a suitable graphics queue was found.");
    }

    private Device CreateDevice(string[] extensions)
    {
        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var extensionNames = ToStringArray(extensions, out _);
        try
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = extensionNames,
            };

            var result = Vk.CreateDevice(PhysicalDevice, in createInfo, null, out var device);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateDevice failed: {result}");

            return device;
        }
        finally
        {
            FreeStringArray(extensionNames, extensions.Length);
        }
    }

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memoryProperties);
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

    public DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties),
        };

        var result = Vk.AllocateMemory(Device, in allocateInfo, null, out var memory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory failed: {result}");

        return memory;
    }

    public VkBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        var result = Vk.CreateBuffer(Device, in bufferInfo, null, out var buffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer failed: {result}");

        Vk.GetBufferMemoryRequirements(Device, buffer, out var requirements);
        memory = Allocate(requirements, properties);
        Vk.BindBufferMemory(Device, buffer, memory, 0);
        return buffer;
    }

    public void SubmitAndWait()
    {
        var command = CommandBuffer;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command,
        };

        var result = Vk.QueueSubmit(Queue, 1, in submitInfo, default);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit failed: {result}");

        Vk.QueueWaitIdle(Queue);
    }

    private static byte** ToStringArray(string[] values, out IntPtr block)
    {
        block = Marshal.AllocHGlobal(IntPtr.Size * values.Length);
        var pointers = (IntPtr*)block;
        for (var i = 0; i < values.Length; i++)
            pointers[i] = Marshal.StringToHGlobalAnsi(values[i]);

        return (byte**)block;
    }

    private static void FreeStringArray(byte** pointer, int count)
    {
        var pointers = (IntPtr*)pointer;
        for (var i = 0; i < count; i++)
            Marshal.FreeHGlobal(pointers[i]);
        Marshal.FreeHGlobal((IntPtr)pointer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Vk.DeviceWaitIdle(Device);
        Vk.DestroyCommandPool(Device, CommandPool, null);
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();

        _disposed = true;
    }
}
