using System;
using System.Globalization;
using System.IO;

namespace Findra.Startup;

/// <summary>
/// One interface per index.
///
/// <para>Nothing stopped two from running, and they do not simply coexist. They share one models
/// folder and one index, and the damage is specific rather than theoretical: each builds an
/// <c>IndexerHost</c> and starts a <c>--index</c> child, the second child's vector store opens for
/// writing over a file the first holds with <c>FileShare.Read</c> and throws a sharing violation,
/// its parent restarts it, and it throws again - for ever, at a five-minute backoff, logging a
/// death each time. Both interfaces meanwhile run queue transactions against one database, the
/// second global hotkey lands on a fallback chord because the first took the real one, and a
/// first-run download in either writes into the same part files the other is writing.</para>
///
/// <para><b>An exclusive file handle in the index folder, not a named mutex.</b> Three reasons,
/// and each is a bug avoided rather than a preference:</para>
/// <list type="bullet">
/// <item>A mutex belongs to the THREAD that took it. Releasing it from another throws, and taking
/// it twice on one thread SUCCEEDS - so a guard written on one is reentrant exactly where a test
/// would try to prove it works, which is how this was found.</item>
/// <item>Windows closes a dead process's handles unconditionally, so a hard kill frees the claim
/// with no abandoned-state reasoning and nobody is locked out of their own product until they
/// reboot. A named semaphore, the other obvious choice, does NOT come back from a crash.</item>
/// <item>It is keyed on the index directory by construction rather than by hashing a path into a
/// name, so two profiles are correctly two Findras, and one person signed in twice - console and
/// remote, one profile, one index - is correctly one.</item>
/// </list>
/// </summary>
public sealed class OnlyOne : IDisposable
{
    /// <summary>The claim, in the folder it protects. The file's CONTENT is a courtesy for whoever
    /// finds it - the handle is the claim, and a stale or empty file means nothing.</summary>
    public const string FileName = ".running";

    private readonly FileStream _held;

    private OnlyOne(FileStream held) => _held = held;

    public static string PathIn(string indexDir) => Path.Combine(indexDir, FileName);

    /// <summary>Take the claim, or say it is taken. True with a null claim means the guard could
    /// not be applied at all, which is deliberately not a refusal - see the catch below.</summary>
    public static bool Take(out OnlyOne? claim, string? indexDir = null)
    {
        claim = null;
        string dir = indexDir ?? Paths.Index;
        string path;
        try
        {
            Directory.CreateDirectory(dir);
            path = PathIn(dir);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "the index folder could not be prepared for the single-instance " +
                                "claim, so a second Findra is not being prevented :: " + ex.Message);
            return true;
        }

        try
        {
            // TWO things exclude here, and it is worth saying which, because a mutation test that
            // relaxed only the first still found the claim held:
            //
            //   FileShare.None refuses a second open that wants read or write access, and
            //   DeleteOnClose refuses ANY second open that does not itself pass FileShare.Delete.
            //
            // Either alone is enough for the case that matters. DeleteOnClose is also what tidies
            // the file away on an ordinary exit, and nothing depends on that half: a file left
            // behind by a kill is opened and locked again by the next launch as if it were new,
            // which TheClaimIsAHandleRatherThanTheFileBeingThere is the test for.
            var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                                    bufferSize: 1, FileOptions.DeleteOnClose);
            try
            {
                byte[] who = System.Text.Encoding.UTF8.GetBytes(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " " +
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
                fs.Write(who, 0, who.Length);
                fs.Flush();
            }
            catch (IOException) { /* the handle is the claim; what is in the file is a courtesy */ }
            claim = new OnlyOne(fs);
            return true;
        }
        catch (IOException)
        {
            // Somebody else holds it. This is the one refusal, and it is the ordinary one.
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // A read-only folder, a policy, an antivirus holding the file open. Refusing to START
            // on that would turn a guard against a rare collision into a product that will not
            // open, which is much the worse of the two failures. Let it through and say so.
            Log.Warn("startup", "the single-instance claim could not be taken, so a second Findra " +
                                "is not being prevented :: " + ex.Message);
            return true;
        }
    }

    /// <summary>What the second launch tells whoever typed it. It names the running process where
    /// it can, because "already running" with nothing on screen - a welcome screen behind another
    /// window, a capsule dragged onto a monitor that is now unplugged - is the moment this message
    /// is least helpful and most likely to be read.</summary>
    public static string AlreadyRunning(UiStatus.Status? other)
    {
        if (other is not { } s)
            return "findra: Findra is already running. Use its hotkey, or its icon in the notification area.";

        string how = string.IsNullOrWhiteSpace(s.Hotkey)
            ? "It registered no hotkey, so use its icon in the notification area"
            : "Press " + s.Hotkey + ", or use its icon in the notification area";
        return $"findra: Findra is already running (process {s.Pid.ToString(CultureInfo.InvariantCulture)}). {how}.";
    }

    public void Dispose()
    {
        try { _held.Dispose(); } catch (IOException) { }
    }
}
