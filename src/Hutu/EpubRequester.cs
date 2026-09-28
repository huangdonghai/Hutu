using System.IO.Compression;
using System.Net;
using System.Text;
using AngleSharp;
using AngleSharp.Io;

public sealed class EpubRequester : BaseRequester, IDisposable
{
    private readonly ZipArchive _archive;
    private readonly string _rootPath;

    public EpubRequester(string epubPath, string rootPath = "")
    {
        _archive = ZipFile.OpenRead(epubPath);
        _rootPath = NormalizePath(rootPath);
    }

    public EpubRequester(Stream epubStream, string rootPath = "")
    {
        _archive = new ZipArchive(
            epubStream,
            ZipArchiveMode.Read,
            leaveOpen: true);

        _rootPath = NormalizePath(rootPath);
    }

    public override bool SupportsProtocol(string protocol)
    {
        return string.Equals(
            protocol,
            "epub",
            StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<IResponse?> PerformRequestAsync(
        Request request,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var url = request.Address;

        if (!string.Equals(
                url.Protocol,
                "epub",
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IResponse?>(null);
        }

        var path = Uri.UnescapeDataString(url.Path);

        // epub:///OEBPS/Styles/style.css
        path = path.TrimStart('/');

        if (_rootPath.Length > 0)
        {
            path = CombinePath(_rootPath, path);
        }

        var entry = _archive.GetEntry(path);

        if (entry == null)
        {
            return Task.FromResult<IResponse?>(null);
        }

        var memory = new MemoryStream();

        using (var source = entry.Open())
        {
            source.CopyTo(memory);
        }

        memory.Position = 0;

        var contentType = GetContentType(path);

        IResponse response = VirtualResponse.Create(response => response
            .Address(url)
            .Status(HttpStatusCode.OK)
            .Header("Content-Type", contentType)
            .Content(memory, shouldDispose: true));

        return Task.FromResult<IResponse?>(response);
    }

    private static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/');

        while (path.StartsWith('/'))
            path = path[1..];

        return path.TrimEnd('/');
    }

    private static string CombinePath(string root, string path)
    {
        if (string.IsNullOrEmpty(root))
            return path;

        return $"{root}/{path}";
    }

    private static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path);

        return extension.ToLowerInvariant() switch
        {
            ".xhtml" => "application/xhtml+xml",
            ".html" => "text/html",
            ".htm" => "text/html",

            ".css" => "text/css",

            ".xml" => "application/xml",
            ".opf" => "application/oebps-package+xml",
            ".ncx" => "application/x-dtbncx+xml",

            ".svg" => "image/svg+xml",

            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",

            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",

            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        _archive.Dispose();
    }
}
