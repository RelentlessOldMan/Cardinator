using System;
using System.IO;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Guards the frame.png cache-invalidation logic — the fix for the "everything slammed to the
/// edges" bug where a frame baked from one spec was composited with text laid out from another.</summary>
public class TemplateServiceTests
{
    private static TemplateSpec Spec(double titleX) => new()
    {
        Name = "T", CanvasWidth = 750, CanvasHeight = 1050, BorderThickness = 28,
        TitleBar = new() { X = titleX, Y = 46, W = 640, H = 60 },
        ArtWindow = new() { X = 48, Y = 124, W = 654, H = 464 },
        TypeBar = new() { X = 48, Y = 598, W = 654, H = 56 },
        TextBox = new() { X = 48, Y = 662, W = 654, H = 296 },
    };

    [Fact]
    public void EnsureFrame_RegeneratesFrame_WhenSpecChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cardinator_tpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var framePath = Path.Combine(dir, "frame.png");
        var hashPath = framePath + ".hash";
        try
        {
            var a = Spec(48);
            TemplateService.EnsureFrame(a, framePath);
            Assert.True(File.Exists(framePath));
            Assert.True(File.Exists(hashPath));
            Assert.Equal(a.ContentHash(), File.ReadAllText(hashPath).Trim());
            var bytesA = File.ReadAllBytes(framePath);

            // Same spec again: hash matches, so the frame must NOT be rebuilt (identical bytes).
            TemplateService.EnsureFrame(a, framePath);
            Assert.Equal(bytesA, File.ReadAllBytes(framePath));

            // Changed spec: hash mismatch must regenerate the frame and update the sidecar.
            var b = Spec(120);
            TemplateService.EnsureFrame(b, framePath);
            Assert.Equal(b.ContentHash(), File.ReadAllText(hashPath).Trim());
            Assert.NotEqual(bytesA, File.ReadAllBytes(framePath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
