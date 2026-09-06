using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Findra;

/// <summary>One graphics adapter as DXGI reports it, at the index DirectML will accept.</summary>
/// <param name="Index">Its position in DXGI's own enumeration, which is exactly what
/// <c>AppendExecutionProvider_DML</c> takes. Not an index into any list of ours.</param>
/// <param name="Name">The card's own description, so a log line and <c>--searchmodels</c> can name
/// the silicon rather than saying "DirectML" and leaving the reader to guess which chip.</param>
/// <param name="DedicatedBytes">Memory belonging to the card. An integrated GPU has little or none
/// of this - it borrows system memory - which is what makes it the discriminator.</param>
/// <param name="Software">DXGI's own flag for a software rasteriser (WARP). Never chosen: it is
/// slower than the CPU provider and would be picked over it silently.</param>
public sealed record GpuChoice(int Index, string Name, long DedicatedBytes, bool Software);

/// <summary>
/// Which graphics adapter the accelerated work goes to.
///
/// <para><b>A device index is not a choice.</b> <c>AppendExecutionProvider_DML(0)</c> takes
/// whichever adapter DXGI happens to list first, and that order is decided by the machine: which
/// chip drives the display, what the power settings say, and what virtual display drivers somebody
/// has installed. On a laptop, and on any desktop whose monitors hang off the motherboard, first is
/// the integrated GPU - and an integrated GPU running a vision tower is not a little slower than a
/// discrete one, it is close to two orders of magnitude slower.</para>
///
/// <para>The failure is silent in the worst way: the provider really is DirectML, the log says
/// DirectML, every model loads, every answer is correct. Only the clock disagrees, and there was
/// nothing to compare it against.</para>
///
/// <para><b>Most dedicated video memory wins.</b> It is the one property that separates a discrete
/// card from an integrated one without knowing any vendor's name - integrated graphics carve their
/// working memory out of system RAM and report little or no dedicated memory of their own. That
/// keeps the rule vendor-neutral, which spec §7 requires: no capability may prefer a vendor, and a
/// list of known-good device names would be exactly that.</para>
///
/// <para>Everything here fails soft. A machine with no DXGI, one adapter, or an enumeration that
/// throws gets <c>null</c>, and the caller passes device 0 - which is what the code did before this
/// existed. Nothing may fail to start because the adapter could not be named.</para>
/// </summary>
public static class GpuAdapter
{
    private const int SoftwareFlag = 2;   // DXGI_ADAPTER_FLAG_SOFTWARE

    // IDXGIFactory1. The GUID is the interface's, not a product's.
    private static readonly Guid IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    // Vtable slots, counted from the interface chain rather than guessed: IUnknown takes 0-2,
    // IDXGIObject 3-6, IDXGIFactory 7-11, so IDXGIFactory1::EnumAdapters1 is 12. On the adapter,
    // IDXGIObject ends at 6, IDXGIAdapter takes 7-9, so IDXGIAdapter1::GetDesc1 is 10.
    private const int Release = 2, EnumAdapters1 = 12, GetDesc1 = 10;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersFn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescFn(IntPtr self, out AdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private static T Call<T>(IntPtr obj, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    /// <summary>Every adapter DXGI reports, in DXGI's order. Empty when DXGI is unavailable or
    /// throws - this is a diagnostic and a preference, never a precondition.</summary>
    public static IReadOnlyList<GpuChoice> All()
    {
        var found = new List<GpuChoice>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(in IDXGIFactory1, out factory) != 0 || factory == IntPtr.Zero) return found;
            EnumAdaptersFn next = Call<EnumAdaptersFn>(factory, EnumAdapters1);
            for (uint i = 0; ; i++)
            {
                if (next(factory, i, out IntPtr adapter) != 0 || adapter == IntPtr.Zero) break;
                try
                {
                    if (Call<GetDescFn>(adapter, GetDesc1)(adapter, out AdapterDesc1 d) == 0)
                        found.Add(new GpuChoice((int)i, (d.Description ?? "").Trim(), d.DedicatedVideoMemory,
                                                (d.Flags & SoftwareFlag) != 0));
                }
                finally { Call<ReleaseFn>(adapter, Release)(adapter); }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (Exception ex) { Log.Warn("models", "could not enumerate graphics adapters: " + ex.Message); }
        finally { if (factory != IntPtr.Zero) Call<ReleaseFn>(factory, Release)(factory); }
        return found;
    }

    /// <summary>The adapter the accelerated work should go to, or null to leave the decision where
    /// it was. Null on one adapter as well as on none: there is nothing to choose, and saying so is
    /// how the log distinguishes "picked the good one" from "there was only one".</summary>
    public static GpuChoice? Best()
    {
        IReadOnlyList<GpuChoice> all = All();
        GpuChoice? best = null;
        foreach (GpuChoice a in all)
        {
            if (a.Software) continue;
            if (best is null || a.DedicatedBytes > best.DedicatedBytes) best = a;
        }
        return all.Count > 1 ? best : null;
    }

    /// <summary>One line for a log or a diagnostic. Names every adapter and marks the chosen one,
    /// because "DirectML" on its own has never answered the question anybody was asking.</summary>
    public static string Describe()
    {
        IReadOnlyList<GpuChoice> all = All();
        if (all.Count == 0) return "no DXGI adapters";
        GpuChoice? best = Best();
        var parts = new List<string>(all.Count);
        foreach (GpuChoice a in all)
            parts.Add($"[{a.Index}] {a.Name} {Sizes.Human(a.DedicatedBytes)}{(a.Software ? " (software)" : "")}"
                      + (best is not null && best.Index == a.Index ? " <- chosen" : ""));
        return string.Join(", ", parts);
    }
}
