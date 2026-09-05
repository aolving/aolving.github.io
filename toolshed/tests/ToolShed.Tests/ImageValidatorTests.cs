using ToolShed.Web.Services;
using Xunit;

namespace ToolShed.Tests;

public class ImageValidatorTests
{
    private static ImageInspection Inspect(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return ImageValidator.Inspect(stream, bytes.Length);
    }

    private static byte[] Padded(params byte[] header)
    {
        var buffer = new byte[64];
        header.CopyTo(buffer, 0);
        return buffer;
    }

    [Fact]
    public void Jpeg_magic_bytes_are_accepted()
    {
        var result = Inspect(Padded(0xFF, 0xD8, 0xFF, 0xE0));
        Assert.True(result.IsValid);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(".jpg", result.Extension);
    }

    [Fact]
    public void Png_magic_bytes_are_accepted()
    {
        var result = Inspect(Padded(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A));
        Assert.True(result.IsValid);
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public void Webp_riff_container_is_accepted()
    {
        var bytes = Padded(0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50);
        Assert.True(Inspect(bytes).IsValid);
    }

    [Fact]
    public void Svg_is_rejected_even_though_browsers_call_it_an_image()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.False(Inspect(bytes).IsValid);
    }

    [Fact]
    public void A_renamed_executable_is_rejected()
    {
        // "MZ" DOS header, i.e. photo.jpg that is really a PE binary.
        Assert.False(Inspect(Padded(0x4D, 0x5A, 0x90, 0x00)).IsValid);
    }

    [Fact]
    public void Empty_and_oversized_uploads_are_rejected()
    {
        Assert.False(Inspect([]).IsValid);

        using var stream = new MemoryStream(Padded(0xFF, 0xD8, 0xFF, 0xE0));
        Assert.False(ImageValidator.Inspect(stream, ImageValidator.MaxBytes + 1).IsValid);
    }
}
