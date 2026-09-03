using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Findra.Pipe;
using Xunit;

/// <summary>
/// The helper's own pipe instances, created against the real kernel object rather than read out
/// of the source. Both of these run at whatever integrity the test host has: the rights on the
/// descriptor are what decide the outcome, and elevation is not part of it.
/// </summary>
public class PipeInstanceTests
{
    /// <summary>A name nothing else on the machine holds, so a stale instance from an earlier run
    /// or from a running Findra cannot decide the answer.</summary>
    private static string Fresh() => "findra-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ASecondInstanceIsCreatedWhileTheFirstOneIsStillHeld()
    {
        // The listener creates one instance, hands it to a session the moment a client connects,
        // and immediately creates the next one - so every instance after the first is created
        // while the pipe already exists. That creation is access-checked against the descriptor
        // the first instance carries, and a descriptor granting only read and write refuses it:
        // the helper reads the disk perfectly, serves exactly one client and can then never
        // listen again, which is name search dead on a machine where the interface holds its
        // connection open.
        string name = Fresh();
        using NamedPipeServerStream first = NameServer.CreateInstance(name, first: true);
        using NamedPipeServerStream second = NameServer.CreateInstance(name, first: false);

        Assert.False(second.IsConnected);
    }

    [Fact]
    public void NobodyButTheAccountTheHelperRunsAsIsNamedOnThePipe()
    {
        // The other half of the line above. The pipe is the boundary between an elevated process
        // and a normal one, so what may never happen is a rule for Everyone, for Users, or for
        // Administrators. One rule, for one account, is the whole of the descriptor.
        string name = Fresh();
        using NamedPipeServerStream server = NameServer.CreateInstance(name, first: true);

        PipeSecurity acl = server.GetAccessControl();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;

        Assert.Equal(me, acl.GetOwner(typeof(SecurityIdentifier)));

        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false,
                                       typeof(SecurityIdentifier))
                       .Cast<PipeAccessRule>().ToList();
        PipeAccessRule only = Assert.Single(rules);
        Assert.Equal(me, only.IdentityReference);
        Assert.Equal(AccessControlType.Allow, only.AccessControlType);
    }
}
