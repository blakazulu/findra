namespace Findra;

/// <summary>One installer attached to a release, as GitHub's release record lists it: its file
/// name, where it downloads from, its size in bytes, and the SHA-256 GitHub computed when it was
/// uploaded, as lower-case hex.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string Sha256);

/// <summary>What a check learned: the newest version's tag, and the installer for this machine
/// when the source lists files at all. The winget catalogue lists versions only, so a winget copy
/// never has one; neither does a release with no installer for this architecture, or one GitHub
/// gives no digest for.</summary>
public sealed record LatestRelease(string Tag, ReleaseAsset? Installer);
