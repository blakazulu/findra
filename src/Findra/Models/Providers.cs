using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Findra;

/// <summary>One provider that was tried, and what came of it. <see cref="Reason"/> is empty for
/// the one that worked and carries the exception's type and message for one that did not.</summary>
public readonly record struct ProviderTry(string Name, bool Chosen, string Reason);

/// <summary>What was built, by which provider, and everything that was tried on the way.</summary>
public sealed record Chosen<T>(T Value, string Provider, IReadOnlyList<ProviderTry> Tried);

public sealed class NoProviderException(string message, IReadOnlyList<ProviderTry> tried) : Exception(message)
{
    public IReadOnlyList<ProviderTry> Tried { get; } = tried;
}

/// <summary>
/// Thrown by a rung whose binary was never published for this architecture, as opposed to one
/// that was published and would not start.
///
/// <para>The difference is the whole reason this type exists. A missing accelerator on a machine
/// that has one is a driver question worth chasing; a missing accelerator on an architecture the
/// runtime was never built for is a fact about the package, and printing an initialisation error
/// for it sends whoever reads <c>--searchmodels</c> hunting a defect that is not there. So
/// <see cref="Providers.First"/> records this one's message alone, with no exception type in
/// front of it.</para>
/// </summary>
public sealed class ProviderNotShippedException(string message) : Exception(message);

/// <summary>
/// Which execution provider to run on, decided by trying them rather than by asking what the
/// machine is.
///
/// <para>Findra ships on winget and lands on machines nobody chose for it - AMD and Intel CPUs,
/// NVIDIA / AMD / Intel GPUs, integrated or discrete, and machines with no usable accelerator at
/// all. No capability may require a particular vendor, so the chains are DirectML (DirectX 12,
/// which covers all three) and Vulkan (the same breadth for the ggml runtime), each falling back
/// to the CPU. CUDA would mean NVIDIA only plus a large separate runtime, and ROCm is not a
/// Windows story: a portable path everywhere beats a fast path for a third of users.</para>
///
/// <para>The CPU is a supported configuration and not a failure state. Nothing here logs an error
/// or warns because no accelerator was found - the only honest difference is how long the first
/// content index takes.</para>
///
/// <para>Everything that was tried is recorded, including what was rejected and why, because
/// <c>--searchmodels</c> prints it (spec §6). That record is the difference between a solvable
/// support question and an unsolvable one.</para>
/// </summary>
public static class Providers
{
    public static readonly string[] OnnxOrder = ["DirectML", "CPU"];
    public static readonly string[] WhisperOrder = ["Vulkan", "CPU"];

    /// <summary>Named so the ban is a value a test can read, rather than a paragraph somebody
    /// has to remember. Anything here ties Findra to one vendor's silicon.</summary>
    public static readonly string[] Banned = ["CUDA", "TensorRT", "ROCm", "OpenVINO", "CoreML"];

    /// <summary>Build with the first candidate that initialises. Later candidates are not
    /// constructed at all once one succeeds - a provider is an expensive thing to make and a
    /// discarded one holds a device.</summary>
    public static Chosen<T> First<T>(IReadOnlyList<(string Name, Func<T> Init)> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var tried = new List<ProviderTry>(chain.Count);
        foreach ((string name, Func<T> init) in chain)
        {
            try
            {
                T made = init();
                tried.Add(new ProviderTry(name, true, ""));
                return new Chosen<T>(made, name, tried);
            }
            catch (Exception ex)
            {
                // Not a warning. A machine with no DirectX 12 device is an ordinary machine, and
                // the next rung of the chain is the answer rather than a degradation to report.
                //
                // A rung that was never built for this architecture states that in its own words:
                // an exception type in front of it would read as a failure rather than as an
                // absence, and the two want different answers from whoever reads the report.
                string why = ex is ProviderNotShippedException ? ex.Message
                                                               : $"{ex.GetType().Name}: {ex.Message}";
                tried.Add(new ProviderTry(name, false, why));
                Log.Once($"models|provider|{name}", "INFO", "models",
                         $"{name} did not initialise, trying the next one :: {why}");
            }
        }
        throw new NoProviderException(
            "no execution provider would initialise: " +
            string.Join("; ", tried.Select(t => $"{t.Name} - {t.Reason}")), tried);
    }

    /// <summary>
    /// Refuse the accelerated speech rung where its binary was never published, before the ggml
    /// loader is touched.
    ///
    /// <para>Whisper.net.Runtime.Vulkan 1.9.1 carries <c>build/win-x64</c> and <c>build/linux-x64</c>
    /// and nothing else - there is no Windows-on-ARM asset in the package at all - while the CPU
    /// runtime beside it does ship for every architecture. So on arm64 the Vulkan rung is not a
    /// device that would not start, it is a file that is not there, and the only honest thing the
    /// record can say is which package is short. Speech still works; it works on the processor,
    /// which is a supported configuration.</para>
    ///
    /// <para>Called by the Vulkan rung of the whisper chain as its first statement, so that the
    /// architecture check and the reason live together in one place instead of being a condition
    /// at one call site and a sentence at another.</para>
    /// </summary>
    public static void RequireAcceleratedSpeechRuntime()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;
        if (arch == Architecture.X64) return;
        throw new ProviderNotShippedException(
            $"the accelerated speech runtime is published for x64 only and there is no {arch} " +
            "build of it in the package, so the processor runtime is the whole answer here");
    }
}
