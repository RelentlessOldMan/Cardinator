using System.IO;
using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>
/// The backward-compatibility promise: a new build must read files written by any older build. These are
/// golden "old format" fixtures (minimal / pre-feature JSON) asserted to load with sensible defaults.
/// Add a case here whenever a persisted shape changes. See <c>JsonCompat</c> for the policy.
/// </summary>
public class BackwardCompatTests
{
    private static string Temp(string ext)
        => Path.Combine(Path.GetTempPath(), "cardinator_compat_" + System.Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void Project_PreVersioning_LoadsWithDefaults()
    {
        // A very old project: no formatVersion, no profile, no defaultTemplate, minimal card fields.
        var path = Temp(".cardinator");
        File.WriteAllText(path, @"{
            ""name"": ""Old Set"",
            ""cards"": [ { ""name"": ""Grizzly Bears"", ""typeLine"": ""Creature — Bear"" } ]
        }");
        try
        {
            var p = CardProject.Load(path);
            Assert.Equal("Old Set", p.Name);
            Assert.Equal(0, p.FormatVersion);          // absent => pre-versioning
            Assert.NotNull(p.Profile);
            Assert.True(p.Profile.IsEmpty);
            Assert.Equal("", p.DefaultTemplate);
            Assert.Single(p.Cards);
            Assert.Equal("Grizzly Bears", p.Cards[0].Name);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Project_Resave_StampsCurrentFormatVersion()
    {
        var path = Temp(".cardinator");
        File.WriteAllText(path, @"{ ""name"": ""X"", ""cards"": [] }");
        try
        {
            var p = CardProject.Load(path);
            p.Save(path);
            var reloaded = CardProject.Load(path);
            Assert.Equal(CardProject.CurrentFormatVersion, reloaded.FormatVersion);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Project_UnknownProperties_AreIgnored()
    {
        // A file written by a HYPOTHETICAL newer version with fields this build doesn't know about.
        var path = Temp(".cardinator");
        File.WriteAllText(path, @"{
            ""name"": ""Future Set"",
            ""someFutureFeature"": { ""nested"": true },
            ""cards"": [ { ""name"": ""A"", ""unknownCardField"": 42 } ]
        }");
        try
        {
            var p = CardProject.Load(path);       // must not throw
            Assert.Equal("Future Set", p.Name);
            Assert.Single(p.Cards);
            Assert.Equal("A", p.Cards[0].Name);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Project_TolerantJson_CommentsTrailingCommasQuotedNumbers()
    {
        var path = Temp(".cardinator");
        File.WriteAllText(path, @"{
            // a hand-edited file
            ""name"": ""Hand Edited"",
            ""formatVersion"": ""1"",            /* quoted number */
            ""cards"": [ { ""name"": ""A"", } ],  // trailing comma
        }");
        try
        {
            var p = CardProject.Load(path);
            Assert.Equal("Hand Edited", p.Name);
            Assert.Equal(1, p.FormatVersion);     // read from a quoted string
            Assert.Single(p.Cards);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Template_OldMinimalSpec_LoadsAndNormalizes()
    {
        // A pre-feature template.json: just a name + canvas + art window, no newer styling fields.
        var json = @"{
            ""name"": ""Legacy Frame"",
            ""canvasWidth"": 750,
            ""canvasHeight"": 1050,
            ""artWindow"": { ""x"": 48, ""y"": 124, ""w"": 654, ""h"": 464 }
        }";
        var spec = TemplateSpec.LoadFromJson(json);   // must not throw
        Assert.Equal("Legacy Frame", spec.Name);
        Assert.Equal(750, spec.CanvasWidth);
        Assert.Equal(1050, spec.CanvasHeight);
        Assert.NotNull(spec.ArtWindow);
        Assert.Equal(654, spec.ArtWindow!.W);
    }

    [Fact]
    public void Card_CaseInsensitiveKeys_StillBind()
    {
        // Casing drift (e.g. a third-party or older writer using PascalCase) must still bind.
        var json = @"{ ""Name"": ""Bolt"", ""TypeLine"": ""Instant"", ""ManaCost"": ""{R}"" }";
        var card = System.Text.Json.JsonSerializer.Deserialize<CardModel>(json, Cardinator.Services.JsonCompat.Options)!;
        Assert.Equal("Bolt", card.Name);
        Assert.Equal("Instant", card.TypeLine);
        Assert.Equal("{R}", card.ManaCost);
    }
}
