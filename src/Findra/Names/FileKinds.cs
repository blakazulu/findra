using System;
using System.Collections.Generic;

namespace Findra;

public enum ResultKind { File, Folder, Photo, Video, Document, Audio }

// What a file IS, from its extension, and whether its contents are worth indexing. One table so the
// name search, the card's result rows and their chips, and the indexer's queue all agree on it.
public static class FileKinds
{
    // Every image this build can decode, not every image somebody would call a photograph. An
    // application icon is an image of something and is searched the same way; `ico` and `avif` are
    // here because Skia reads them, and `svg` is not because Skia does not - a vector file has no
    // pixels to embed and would be queued only to be recorded unreadable.
    private static readonly HashSet<string> Photo = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "heic", "heif", "webp", "bmp", "gif", "tif", "tiff", "jfif",
          "ico", "avif",
          "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf" };
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mov", "mkv", "avi", "webm", "m4v", "wmv", "mts", "m2ts" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus", "wma", "aiff" };
    // Documents are things people WRITE and READ. Code, JSON, YAML and logs are not - the first
    // queue on this machine was 38,000 "documents" of which 18,000 were JSON and 7,000 were code,
    // and a semantic search over package-lock.json helps nobody. They stay searchable by name.
    private static readonly HashSet<string> Document = new(StringComparer.OrdinalIgnoreCase)
        { "pdf", "docx", "pptx", "xlsx", "epub", "txt", "md", "rtf", "csv", "doc", "xls", "ppt",
          "odt", "odp", "ods", "html", "htm" };

    public static ResultKind Classify(string name, bool isDirectory)
    {
        if (isDirectory) return ResultKind.Folder;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return ResultKind.File;
        string ext = name[(dot + 1)..];
        if (Photo.Contains(ext)) return ResultKind.Photo;
        if (Video.Contains(ext)) return ResultKind.Video;
        if (Audio.Contains(ext)) return ResultKind.Audio;
        if (Document.Contains(ext)) return ResultKind.Document;
        return ResultKind.File;
    }

    /// <summary>Has content worth sending to the indexer.</summary>
    public static bool HasContent(ResultKind k) => k is ResultKind.Photo or ResultKind.Video or ResultKind.Audio or ResultKind.Document;

    /// <summary>
    /// Every extension that classifies to a kind with content, without the dot.
    ///
    /// Read from the same four sets <see cref="Classify"/> reads, and for one reason: the first
    /// pass asks the helper for a suffix list, and a second table written out by hand would drift
    /// from this one the first time somebody adds an extension. The drift is silent - files with
    /// the new extension are simply never enumerated - and it only shows up on machines that had
    /// already finished indexing, which is nobody's test machine.
    /// </summary>
    public static IEnumerable<string> ContentExtensions()
    {
        foreach (string e in Photo) yield return e;
        foreach (string e in Video) yield return e;
        foreach (string e in Audio) yield return e;
        foreach (string e in Document) yield return e;
    }

    public static string Label(ResultKind k) => k switch
    {
        ResultKind.Photo => "Photo", ResultKind.Video => "Video", ResultKind.Audio => "Audio",
        ResultKind.Document => "Doc", ResultKind.Folder => "Folder", _ => "File"
    };

    // Content exclusions: path FRAGMENTS matched case-insensitively against the full path with a
    // separator on each side, so "bin" excludes "\bin\" and not "\binaries\". Names/paths are still
    // indexed for everything - this only decides what the indexer opens.
    public static readonly string[] DefaultExclusions =
    {
        @"\Windows\", @"\Program Files\", @"\Program Files (x86)\", @"\ProgramData\", @"\AppData\",
        @"\$Recycle.Bin\", @"\System Volume Information\", @"\node_modules\", @"\.git\", @"\bin\",
        @"\obj\", @"\.venv\", @"\venv\", @"\__pycache__\", @"\.cache\", @"\.nuget\", @"\.gradle\",
        @"\.m2\", @"\steamapps\common\", @"\XboxGames\", @"\.vs\", @"\packages\", @"\site-packages\",
        @"\Temp\", @"\tmp\", @"\$WinREAgent\", @"\Recovery\",
        // the AI tools' own state: transcripts, caches and checkpoints, tens of thousands of files
        @"\.claude\", @"\.codex\", @"\.cursor\", @"\.gemini\", @"\.antigravity-ide\", @"\.vscode\",
        @"\.idea\", @"\.ollama\", @"\.docker\", @"\Lib\test\", @"\.pytest_cache\", @"\.mypy_cache\",
    };

    public static bool Excluded(string path, IReadOnlyList<string> exclusions)
    {
        // normalise so a fragment can be written either way and still hit
        string p = "\\" + path.Replace('/', '\\').TrimEnd('\\') + "\\";
        foreach (var raw in exclusions)
        {
            string x = raw.Trim();
            if (x.Length == 0) continue;
            x = x.Replace('/', '\\');
            if (!x.StartsWith('\\')) x = "\\" + x;
            if (!x.EndsWith('\\')) x += "\\";
            if (p.Contains(x, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
