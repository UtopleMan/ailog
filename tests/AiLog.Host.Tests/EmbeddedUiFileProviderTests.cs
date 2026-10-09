using Microsoft.Extensions.FileProviders;

namespace AiLog.Host.Tests;

public sealed class EmbeddedUiFileProviderTests
{
    private static readonly EmbeddedUiFileProvider Provider =
        EmbeddedUiFileProvider.Load(typeof(EmbeddedUiFileProviderTests).Assembly)!;

    [Fact]
    public void An_embedded_file_is_found_by_its_web_path()
    {
        IFileInfo file = Provider.GetFileInfo("/_ailog/index.html");

        Assert.True(file.Exists);
        Assert.Equal("index.html", file.Name);
        using StreamReader reader = new(file.CreateReadStream());
        Assert.Contains("<title>embedded</title>", reader.ReadToEnd());
    }

    [Fact]
    public void Resource_names_with_windows_separators_are_found_by_their_web_path()
    {
        Assert.True(Provider.GetFileInfo("/_ailog/css/app.css").Exists);
    }

    [Fact]
    public void A_path_that_was_not_embedded_is_not_found()
    {
        Assert.False(Provider.GetFileInfo("/_ailog/missing.js").Exists);
    }

    [Fact]
    public void An_assembly_without_an_embedded_ui_has_no_provider()
    {
        Assert.Null(EmbeddedUiFileProvider.Load(typeof(EmbeddedUiFileProvider).Assembly));
    }
}
