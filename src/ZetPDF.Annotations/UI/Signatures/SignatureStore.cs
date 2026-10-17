namespace ZetPDF.Annotations.UI.Signatures;

/// <summary>Zapisane podpisy odręczne (PNG z przezroczystością) w %APPDATA%\ZetPDF\signatures – tylko lokalnie.</summary>
internal sealed class SignatureStore
{
    private readonly string _dir;

    public SignatureStore(string directory) => _dir = directory;

    public string Directory => _dir;

    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(_dir)) return Array.Empty<string>();
        return new DirectoryInfo(_dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList();
    }

    public string Save(byte[] png)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"podpis-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.png");
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, png);
        File.Move(tmp, path);
        return path;
    }

    public void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        if (File.Exists(full)) File.Delete(full);
    }
}
