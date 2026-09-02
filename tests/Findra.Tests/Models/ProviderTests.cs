using System.Runtime.InteropServices;

using Findra;

using Xunit;

/// <summary>
/// Which execution provider answered, and what refused. Spec §6: "it's slow on my laptop" is
/// unanswerable, "DirectML failed to initialise, fell back to CPU" is a bug report - so the record
/// of what was rejected is half the point of this type, and the tests below are written so that an
/// implementation which only ever uses the processor, or which records nothing, fails rather than
/// passing on the strength of a fallback assertion.
/// </summary>
public class ProviderTests
{
    private sealed record Session(string From);

    [Fact]
    public void TheFirstProviderThatInitialisesIsTheOneUsed()
    {
        // The assertion that "always fall back to the CPU" fails. If the chain is walked to the
        // end regardless, or the last entry is simply returned, this reports "CPU".
        int cpuBuilt = 0;
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => new Session("DirectML")),
            ("CPU", () => { cpuBuilt++; return new Session("CPU"); }),
        ]);

        Assert.Equal("DirectML", c.Provider);
        Assert.Equal("DirectML", c.Value.From);
        Assert.Equal(0, cpuBuilt);            // the CPU session was never even constructed
    }

    [Fact]
    public void AProviderThatCannotInitialiseHandsOverToTheNextOne()
    {
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no DirectX 12 device")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Equal("CPU", c.Provider);
    }

    [Fact]
    public void EveryProviderItRejectedIsNamedWithTheReasonItWasRejectedFor()
    {
        // Spec §6, in as many words: "it's slow on my laptop" is unanswerable, "DirectML failed
        // to initialise, fell back to CPU" is a bug report. Recording only the winner loses
        // exactly the half that answers the question.
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no DirectX 12 device")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Equal(2, c.Tried.Count);
        ProviderTry rejected = c.Tried.Single(t => !t.Chosen);
        Assert.Equal("DirectML", rejected.Name);
        Assert.Contains("no DirectX 12 device", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatWasNeverTriedIsNotClaimedAsRejected()
    {
        // The control that stops a report inventing rows for the whole declared chain.
        Chosen<Session> c = Providers.First<Session>(
        [
            ("DirectML", () => new Session("DirectML")),
            ("CPU", () => new Session("CPU")),
        ]);

        Assert.Single(c.Tried);
        Assert.True(c.Tried[0].Chosen);
        Assert.Equal("", c.Tried[0].Reason);
    }

    [Fact]
    public void AChainWhereNothingInitialisesSaysSoWithEveryReasonInIt()
    {
        NoProviderException ex = Assert.Throws<NoProviderException>(() => Providers.First<Session>(
        [
            ("DirectML", () => throw new InvalidOperationException("no device")),
            ("CPU", () => throw new DllNotFoundException("onnxruntime.dll")),
        ]));

        Assert.Contains("DirectML", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no device", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CPU", ex.Message, StringComparison.Ordinal);
        Assert.Contains("onnxruntime.dll", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Tried.Count);
    }

    [Fact]
    public void TheDeclaredChainsPutTheAcceleratorFirstAndTheCpuLast()
    {
        // A chain with CPU first still "works" everywhere and silently costs every user their
        // GPU. It is exactly the change somebody makes to close a support ticket.
        Assert.Equal(["DirectML", "CPU"], Providers.OnnxOrder);
        Assert.Equal(["Vulkan", "CPU"], Providers.WhisperOrder);
    }

    [Fact]
    public void EveryChainEndsAtTheCpuBecauseTheCpuIsASupportedConfiguration()
    {
        Assert.Equal("CPU", Providers.OnnxOrder[^1]);
        Assert.Equal("CPU", Providers.WhisperOrder[^1]);
    }

    [Fact]
    public void NoChainNamesAVendorLockedProvider()
    {
        foreach (string name in Providers.OnnxOrder.Concat(Providers.WhisperOrder))
            Assert.DoesNotContain(name, Providers.Banned, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CUDA", Providers.Banned, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ROCm", Providers.Banned, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARungWhoseBinaryWasNeverShippedSaysThatRatherThanReportingAFailureToInitialise()
    {
        // The accelerated speech runtime is published for x64 only, so on Windows on ARM the
        // Vulkan rung has no binary to load. That is a fact about the package, not a defect on
        // the machine, and a reason reading "DllNotFoundException: ggml-vulkan-whisper.dll"
        // would send whoever reads --searchmodels looking for a driver problem that is not
        // there. So the record carries the sentence and NOT the exception's type name.
        Chosen<Session> c = Providers.First<Session>(
        [
            ("Vulkan", () => throw new ProviderNotShippedException("no binary is published for arm64")),
            ("CPU", () => new Session("CPU")),
        ]);

        ProviderTry rejected = c.Tried.Single(t => !t.Chosen);
        Assert.Equal("no binary is published for arm64", rejected.Reason);
        Assert.DoesNotContain("Exception", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAcceleratedSpeechRuntimeIsOfferedExactlyWhereItWasPublished()
    {
        // x64 is the only architecture Whisper.net.Runtime.Vulkan 1.9.1 carries a Windows binary
        // for. Anywhere else the rung must refuse before it touches the loader, with a reason
        // that names the package rather than the machine.
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            Providers.RequireAcceleratedSpeechRuntime();   // does not throw here
            return;
        }

        ProviderNotShippedException ex =
            Assert.Throws<ProviderNotShippedException>(Providers.RequireAcceleratedSpeechRuntime);
        Assert.Contains("published", ex.Message, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), ex.Message,
                        StringComparison.OrdinalIgnoreCase);
    }
}
