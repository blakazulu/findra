using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Findra;

/// <summary>One Vulkan physical device, at the index the ggml runtime numbers it by.</summary>
/// <param name="Index">Its position in <c>vkEnumeratePhysicalDevices</c>, which is the order the
/// speech runtime walks as well.</param>
/// <param name="Name">The device's own name, so a log line can say which chip transcribed.</param>
/// <param name="Discrete">A card of its own rather than a slice of the processor.</param>
public sealed record VulkanDevice(int Index, string Name, bool Discrete);

/// <summary>
/// Which Vulkan device the speech runtime should use.
///
/// <para>The same trap as <see cref="GpuAdapter"/>, one library along and worse. The speech
/// runtime takes the first Vulkan device it is shown, and on a machine with both an integrated and
/// a discrete GPU that is usually the integrated one - which for this workload is not merely
/// slower than the card, it is <b>slower than the processor</b>. So the chain picks its
/// accelerated rung, the rung initialises, the log says Vulkan, and the machine ends up doing the
/// work in the worst of the three available places while reporting the best.</para>
///
/// <para><b>The device is chosen by making only one visible</b>, through
/// <c>GGML_VK_VISIBLE_DEVICES</c> on the indexer child's environment. Not by any option the
/// managed wrapper offers: a device index handed to the factory is accepted and ignored, and
/// <c>Environment.SetEnvironmentVariable</c> reaches the runtime's own copy of the environment
/// rather than the block native code reads - so a version of this that sets it in-process reads
/// its own value back correctly, logs success, and changes nothing. It has to be on the child
/// before the child exists, which is why <see cref="Visible"/> returns a string for a
/// <c>ProcessStartInfo</c> rather than setting anything itself.</para>
///
/// <para>Everything fails soft. No Vulkan loader, one device, or an enumeration that throws
/// returns null and sets nothing, leaving the runtime exactly the choice it had before. Restricting
/// to a device the runtime would itself have rejected costs the accelerated rung and falls back to
/// the processor, which on this workload is the faster of the two wrong answers anyway.</para>
/// </summary>
public static class VulkanAdapter
{
    /// <summary>The variable the ggml Vulkan backend reads to decide which devices exist at all.
    /// Named here once because it is written in one place and asserted in another.</summary>
    public const string VisibleDevices = "GGML_VK_VISIBLE_DEVICES";

    private const int DiscreteGpu = 2;          // VkPhysicalDeviceType
    private const int TypeOffset = 16;          // into VkPhysicalDeviceProperties
    private const int NameOffset = 20, NameBytes = 256;
    private const int PropertiesBytes = 1024;   // the real struct is ~800; this is room to write in

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(in InstanceCreateInfo info, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[]? devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public uint SType;              // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;  // optional, and nothing here needs one
        public uint LayerCount;
        public IntPtr LayerNames;
        public uint ExtensionCount;
        public IntPtr ExtensionNames;
    }

    /// <summary>Every Vulkan device on the machine, in the runtime's own order. Empty when there is
    /// no loader, which is an ordinary machine rather than a fault.</summary>
    public static IReadOnlyList<VulkanDevice> All()
    {
        var found = new List<VulkanDevice>();
        IntPtr instance = IntPtr.Zero;
        IntPtr props = IntPtr.Zero;
        try
        {
            var info = new InstanceCreateInfo { SType = 1 };
            if (vkCreateInstance(in info, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero) return found;

            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return found;
            var devices = new IntPtr[count];
            if (vkEnumeratePhysicalDevices(instance, ref count, devices) != 0) return found;

            props = Marshal.AllocHGlobal(PropertiesBytes);
            for (int i = 0; i < count; i++)
            {
                vkGetPhysicalDeviceProperties(devices[i], props);
                int type = Marshal.ReadInt32(props, TypeOffset);
                string name = Marshal.PtrToStringUTF8(props + NameOffset, NameBytes) ?? "";
                int nul = name.IndexOf('\0', StringComparison.Ordinal);
                found.Add(new VulkanDevice(i, (nul >= 0 ? name[..nul] : name).Trim(), type == DiscreteGpu));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (Exception ex) { Log.Warn("models", "could not enumerate Vulkan devices: " + ex.Message); }
        finally
        {
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (instance != IntPtr.Zero) try { vkDestroyInstance(instance, IntPtr.Zero); } catch (DllNotFoundException) { }
        }
        return found;
    }

    /// <summary>The device the speech work should go to, or null to leave the choice alone.
    ///
    /// <para>Null on one device as well as none - there is nothing to choose - and null when
    /// nothing is discrete, because then every candidate is a slice of the processor and picking
    /// between them on a guess is not an improvement.</para></summary>
    public static VulkanDevice? Best()
    {
        IReadOnlyList<VulkanDevice> all = All();
        if (all.Count <= 1) return null;
        foreach (VulkanDevice d in all) if (d.Discrete) return d;
        return null;
    }

    /// <summary>What <see cref="VisibleDevices"/> should be set to on the indexer child, or null
    /// to set nothing at all. A value here hides every other device from the speech runtime, which
    /// is the only lever that actually moves it.</summary>
    public static string? Visible()
        => Best()?.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One line for a log or a diagnostic, naming every device and marking the chosen
    /// one. "Vulkan" on its own has never answered the question anybody was asking.</summary>
    public static string Describe()
    {
        IReadOnlyList<VulkanDevice> all = All();
        if (all.Count == 0) return "no Vulkan devices";
        VulkanDevice? best = Best();
        var parts = new List<string>(all.Count);
        foreach (VulkanDevice d in all)
            parts.Add($"[{d.Index}] {d.Name}{(d.Discrete ? " (discrete)" : "")}"
                      + (best is not null && best.Index == d.Index ? " <- chosen" : ""));
        return string.Join(", ", parts);
    }
}
