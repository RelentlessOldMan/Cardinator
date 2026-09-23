using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>Covers the defensive paths added so malformed input can't crash the app.</summary>
public class HardeningTests
{
    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("Red", 255, 0, 0)]
    [InlineData("  #00FF00  ", 0, 255, 0)]
    public void ParseColor_ParsesValidColors(string hex, byte r, byte g, byte b)
    {
        var c = TemplateSpec.ParseColor(hex);
        Assert.Equal(Color.FromRgb(r, g, b), c);
    }

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseColor_InvalidColor_FallsBackInsteadOfThrowing(string? hex)
    {
        Assert.Equal(Colors.Black, TemplateSpec.ParseColor(hex));
        Assert.Equal(Colors.White, TemplateSpec.ParseColor(hex, Colors.White));
    }

    [Fact]
    public void TemplateSpec_Load_RepairsNullAndDegenerateFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tspec-{Guid.NewGuid():N}.json");
        // Explicit nulls + a zero canvas: a hand-edited template that would otherwise NRE.
        File.WriteAllText(path,
            "{\"name\":null,\"canvasWidth\":0,\"colors\":null,\"artWindow\":null,\"titleFont\":null,\"manaSymbolSize\":0}");
        try
        {
            var spec = TemplateSpec.Load(path);
            Assert.False(string.IsNullOrEmpty(spec.Name));
            Assert.True(spec.CanvasWidth > 0);
            Assert.True(spec.CanvasHeight > 0);
            Assert.NotNull(spec.Colors);
            Assert.NotNull(spec.ArtWindow);
            Assert.NotNull(spec.TitleFont);
            Assert.True(spec.ManaSymbolSize > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CardProject_Load_NullCardsArray_BecomesEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-null-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"name\":\"P\",\"cards\":null}");
        try
        {
            var project = CardProject.Load(path);
            Assert.NotNull(project.Cards);
            Assert.Empty(project.Cards);
            Assert.Equal("P", project.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CardProject_Load_DropsNullCardEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-mixed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"cards\":[null,{\"name\":\"Real\"},null]}");
        try
        {
            var project = CardProject.Load(path);
            Assert.Single(project.Cards);
            Assert.Equal("Real", project.Cards[0].Name);
            Assert.Equal("Untitled Project", project.Name);   // absent name defaulted
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CardProject_RoundTrips_ThroughLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-rt-{Guid.NewGuid():N}.cardinator");
        try
        {
            new CardProject { Name = "Deck", Cards = { new CardModel { Name = "A" } } }.Save(path);
            var loaded = CardProject.Load(path);
            Assert.Equal("Deck", loaded.Name);
            Assert.Single(loaded.Cards);
            Assert.Equal("A", loaded.Cards[0].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportAll_NoTemplates_ReturnsErrorInsteadOfThrowing()
    {
        var result = BatchService.ExportAll(
            new List<CardModel> { new() { Name = "X" } },
            new List<Template>(),                       // empty on purpose
            Path.Combine(Path.GetTempPath(), $"exp-{Guid.NewGuid():N}"),
            new SymbolService());

        Assert.Equal(0, result.Exported);
        Assert.NotEmpty(result.Errors);
    }
}
