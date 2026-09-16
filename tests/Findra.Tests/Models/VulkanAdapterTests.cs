using Findra;

using Xunit;

/// <summary>
/// Which Vulkan device the speech runtime is shown.
///
/// <para>Like the DXGI tests beside them, these run on whatever machine runs them - including one
/// with no Vulkan loader at all, which is an ordinary machine and not a fault. What is asserted is
/// what would be wrong anywhere: a choice made when there is nothing to choose between, a choice
/// that is not actually a card, and an enumeration that throws instead of saying it found nothing.
/// </para>
/// </summary>
public class VulkanAdapterTests
{
    [Fact]
    public void EnumeratingNeverThrowsAndIndicesAreTheRuntimesOwn()
    {
        // The index is what GGML_VK_VISIBLE_DEVICES is set to, so it has to be the position in the
        // runtime's own enumeration rather than a position in a list we filtered.
        IReadOnlyList<VulkanDevice> all = VulkanAdapter.All();
        for (int i = 0; i < all.Count; i++) Assert.Equal(i, all[i].Index);
    }

    [Fact]
    public void NothingIsChosenWhenThereIsNothingToChooseBetween()
    {
        // One device is the common case and the runtime's own default is already right for it.
        if (VulkanAdapter.All().Count <= 1)
        {
            Assert.Null(VulkanAdapter.Best());
            Assert.Null(VulkanAdapter.Visible());
        }
    }

    [Fact]
    public void NothingIsChosenWhenNoDeviceIsACardOfItsOwn()
    {
        // Two integrated devices are two slices of the same processor. Picking between them on a
        // guess is not an improvement, and hiding one of them from the runtime might be a loss.
        IReadOnlyList<VulkanDevice> all = VulkanAdapter.All();
        bool anyDiscrete = false;
        foreach (VulkanDevice d in all) anyDiscrete |= d.Discrete;
        if (!anyDiscrete) Assert.Null(VulkanAdapter.Best());
    }

    [Fact]
    public void TheChoiceIsAlwaysADiscreteDevice()
    {
        VulkanDevice? best = VulkanAdapter.Best();
        if (best is not null) Assert.True(best.Discrete, $"[{best.Index}] {best.Name} is not a card of its own");
    }

    [Fact]
    public void TheVisibleValueIsTheChosenIndexAndNothingElse()
    {
        // It is read by native code as a list of device indices. Anything else in it - a name, a
        // comma, a stray space - is a device list the runtime cannot parse, and it would end up
        // seeing no devices at all.
        VulkanDevice? best = VulkanAdapter.Best();
        string? visible = VulkanAdapter.Visible();
        if (best is null) { Assert.Null(visible); return; }
        Assert.Equal(best.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), visible);
        Assert.All(visible!, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void TheVariableIsNamedOnceAndItIsTheOneTheRuntimeReads()
    {
        // Written into the child's environment in one place and asserted here. A second spelling
        // of it anywhere would be a device choice that silently does nothing.
        Assert.Equal("GGML_VK_VISIBLE_DEVICES", VulkanAdapter.VisibleDevices);
    }

    [Fact]
    public void DescribeAlwaysSaysSomething()
        => Assert.False(string.IsNullOrWhiteSpace(VulkanAdapter.Describe()));

    [Fact]
    public void AProcessThatAlreadyHasTheVariableIsNotRestarted()
    {
        bool ran = false;
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchmodels"], _ => "0", () => "1", (_, _) => { ran = true; return 0; });
        Assert.Null(code);
        Assert.False(ran);
    }

    [Fact]
    public void AMachineWithNothingDiscreteIsLeftExactlyAsItWas()
    {
        bool ran = false;
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchmodels"], _ => null, () => null, (_, _) => { ran = true; return 0; });
        Assert.Null(code);
        Assert.False(ran);
    }

    [Fact]
    public void OtherwiseTheProcessIsRestartedOnceWithTheCardMadeVisibleAndItsExitCodeCarried()
    {
        var seen = new List<(IReadOnlyList<string> Args, string Visible)>();
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchindex", @"D:\clip.mp4"], _ => null, () => "1",
            (args, visible) => { seen.Add((args, visible)); return 3; });

        Assert.Equal(3, code);
        (IReadOnlyList<string> args, string visible) = Assert.Single(seen);
        Assert.Equal("1", visible);
        Assert.Equal(["--searchindex", @"D:\clip.mp4"], args);
    }
}
