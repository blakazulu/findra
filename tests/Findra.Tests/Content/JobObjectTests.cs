using System.Diagnostics;
using Findra;
using Xunit;

public class JobObjectTests
{
    /// <summary>A child that will sit there for half a minute if nothing stops it, so "it exited"
    /// can only mean the job killed it. Started with no window and with its output swallowed, the
    /// same way the interface starts the indexer.</summary>
    private static Process StartALongLivedChild()
        => Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;

    [Fact]
    public void AChildInTheJobDiesWhenTheLastHandleToTheJobCloses()
    {
        // The specification's sentence is that indexing stops when the interface quits "by
        // construction - no lifetime code". A poll on the parent's process id is lifetime code,
        // and Windows reuses process ids: a reissued id keeps the child alive forever after its
        // parent has gone. A job object with kill-on-close makes the sentence true - the KERNEL
        // terminates the child when the last handle to the job closes, which includes every way
        // the interface can die, a force-kill and a crash among them.
        using Process child = StartALongLivedChild();
        try
        {
            using (JobObject? job = JobObject.CreateKillOnClose())
            {
                Assert.NotNull(job);
                Assert.True(job.Assign(child), "the child must actually be assigned to the job");
                Assert.False(child.HasExited, "assigning to a job does not by itself kill anything");
            }

            Assert.True(child.WaitForExit(10_000),
                        "closing the last handle to a kill-on-close job must terminate what is in it");
        }
        finally { try { if (!child.HasExited) child.Kill(true); } catch { } }
    }

    [Fact]
    public void AChildOutsideTheJobIsUntouchedByIt()
    {
        // The other half, and the one that stops the test above passing for the wrong reason: if
        // closing a job killed anything that happened to be running, the assertion above would
        // hold with no assignment at all.
        using Process outside = StartALongLivedChild();
        try
        {
            using (JobObject? job = JobObject.CreateKillOnClose()) Assert.NotNull(job);

            Assert.False(outside.WaitForExit(1_000), "a process that was never assigned must survive");
        }
        finally { try { if (!outside.HasExited) outside.Kill(true); } catch { } }
    }
}
