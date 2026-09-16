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

    /// <summary>
    /// Start this process again with the speech runtime pointed at the card, and hand back the exit
    /// code it finished with. Null means nothing was done and the caller carries on.
    ///
    /// <para><b>It has to be a new process.</b> The runtime reads
    /// <see cref="VisibleDevices"/> through the C runtime's copy of the environment, which
    /// <c>Environment.SetEnvironmentVariable</c> does not reach - so a version of this that set the
    /// variable in place would read its own value back, log success, and change nothing. The
    /// indexer child gets the variable because its parent sets it before the child exists; a
    /// diagnostic somebody typed has no such parent, and this is how it gets one.</para>
    ///
    /// <para>Nothing happens when the variable is already set - which is what stops this restarting
    /// itself for ever - or when there is no card to choose, which is an ordinary machine rather
    /// than a fault.</para>
    /// </summary>
    public static int? ReExecWithDiscrete(IReadOnlyList<string> args)
    {
        // Checked here rather than inside Run: Run's return type is int, with no way to say "I
        // could not restart", so folding this into it would hand the caller a real exit code for
        // a diagnostic that never ran - reporting success for nothing done. An absent process path
        // is not itself a fault - a host that did not launch this as its own executable - so the
        // diagnostic proceeds on whatever device the runtime picks by default, same as a machine
        // with nothing discrete to choose.
        if (string.IsNullOrEmpty(Environment.ProcessPath))
        {
            Log.Once("models|no-process-path", "WARN", "models",
                     "cannot restart to choose a Vulkan device: no process path");
            return null;
        }
        return ReExecWithDiscrete(args, Environment.GetEnvironmentVariable, Visible, Run);
    }

    /// <summary>The effects as delegates, so the decision is testable without starting a process.
    /// </summary>
    public static int? ReExecWithDiscrete(IReadOnlyList<string> args, Func<string, string?> readEnv,
                                          Func<string?> visible, Func<IReadOnlyList<string>, string, int> run)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readEnv);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(run);

        // An empty value counts as unset, the same as a null one: GGML_VK_VISIBLE_DEVICES set to
        // "" by something outside Findra hides every device from the runtime rather than choosing
        // none of them, and treating that as "already chosen" would skip the restart and leave the
        // runtime on its default device - the exact failure this method exists to prevent.
        if (!string.IsNullOrEmpty(readEnv(VisibleDevices))) return null;
        if (visible() is not { } device) return null;
        return run(args, device);
    }

    private static int Run(IReadOnlyList<string> args, string device)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            // Inherited handles, so the restarted process writes to the console the caller is
            // already attached to. A diagnostic whose output went nowhere would be worse than a
            // slow one.
            UseShellExecute = false,
        };
        foreach (string a in args) start.ArgumentList.Add(a);
        start.Environment[VisibleDevices] = device;

        using var proc = System.Diagnostics.Process.Start(start);
        if (proc is null) return 0;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
