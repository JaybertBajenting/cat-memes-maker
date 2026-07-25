namespace CatMeme.Api.Services;

/// <summary>
/// Resolves every on-disk location the pipeline writes to, and creates the folders on startup.
/// </summary>
public class PipelinePaths
{
    public string Base { get; }
    public string Topics => Path.Combine(Base, "topics");
    public string Cats => Path.Combine(Base, "assets", "cats");
    public string Backgrounds => Path.Combine(Base, "assets", "backgrounds");
    public string Fonts => Path.Combine(Base, "assets", "fonts");
    public string Output => Path.Combine(Base, "output");
    public string Thumbnails => Path.Combine(Base, "output", "thumbs");
    public string Logs => Path.Combine(Base, "logs");
    public string Work => Path.Combine(Base, "work");

    public PipelinePaths(IConfiguration cfg)
    {
        var configured = cfg["PIPELINE_BASE_PATH"] ?? cfg["Pipeline:BasePath"];
        Base = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "pipeline")
            : Path.GetFullPath(configured);

        foreach (var d in new[] { Topics, Cats, Backgrounds, Fonts, Output, Thumbnails, Logs, Work })
            Directory.CreateDirectory(d);
    }

    public string CatClip(string filename) => Path.Combine(Cats, filename);
    public string OutputVideo(int videoId) => Path.Combine(Output, $"video_{videoId}.mp4");
    public string Thumbnail(int videoId) => Path.Combine(Thumbnails, $"video_{videoId}.jpg");
}
