using System.Reflection;

namespace Msgtrap.Ui;

/// <summary>
/// Serves the single-page UI from embedded resources so the packaged tool needs
/// no loose files next to the binary.
/// </summary>
public static class EmbeddedUi
{
    private static readonly Assembly Assembly = typeof(EmbeddedUi).Assembly;

    /// <summary>
    /// Every shipped file: request path, manifest suffix and content type.
    /// The vendored framework files keep their upstream names on disk (so an update is
    /// obvious) but are served from neutral paths, which keeps the binary from
    /// advertising its stack to tools that fingerprint pages by asset URL.
    /// </summary>
    private static readonly (string Path, string FileName, string ContentType)[] Assets =
    [
        ("/", "index.html", "text/html; charset=utf-8"),
        ("/app.js", "app.js", "text/javascript; charset=utf-8"),
        ("/styles.css", "styles.css", "text/css; charset=utf-8"),
        ("/favicon.svg", "favicon.svg", "image/svg+xml"),
        ("/vendor/base.css", "bootstrap.min.css", "text/css; charset=utf-8"),
        ("/vendor/base.js", "bootstrap.bundle.min.js", "text/javascript; charset=utf-8"),
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        foreach (var (path, fileName, contentType) in Assets)
            app.MapGet(path, () => Serve(fileName, contentType));
    }

    private static IResult Serve(string fileName, string contentType)
    {
        var resource = Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

        if (resource is null) return Results.NotFound($"{fileName} is missing from the build.");

        var stream = Assembly.GetManifestResourceStream(resource);
        return stream is null ? Results.NotFound() : Results.Stream(stream, contentType);
    }
}
