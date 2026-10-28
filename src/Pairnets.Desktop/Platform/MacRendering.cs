using Avalonia;

namespace Pairnets.Desktop.Platform;

/// <summary>
/// How the Mac app draws. Avalonia's default on a Mac is OpenGL, which Apple only keeps alive by translating it to Metal
/// on the fly: a macOS report on a MacBook showed the render thread busy in that translation (new surfaces and textures
/// every frame) for minutes. Metal first, drawing on the processor as the fallback. <c>PAIRNETS_RENDERING</c>
/// (metal, opengl or software) picks one by hand, for finding out.
/// </summary>
internal static class MacRendering
{
    public const string Variable = "PAIRNETS_RENDERING";

    public static AvaloniaNativeRenderingMode[] Modes(string? choice) => choice?.Trim().ToLowerInvariant() switch
    {
        "opengl" => [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
        "software" => [AvaloniaNativeRenderingMode.Software],
        _ => [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software],
    };

    public static AppBuilder Apply(AppBuilder builder) => OperatingSystem.IsMacOS()
        ? builder.With(new AvaloniaNativePlatformOptions { RenderingMode = Modes(Environment.GetEnvironmentVariable(Variable)) })
        : builder;
}
