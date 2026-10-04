using System;
using System.Collections.Generic;
using System.Linq;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator.Tests;

/// <summary>
/// Which multi-face cards become ONE double-faced card and which stay separate cards — and the invariants
/// that keep two cards from sharing one back face. A split card ("Fire // Ice") is printed on a single
/// side, so turning its second half into a "back" would invent a flip, stamp a sun/moon indicator and
/// export a bogus -back.png.
/// </summary>
public class DfcCorrectnessTests
{
    private static List<CardModel> Faces(string layout, params string[] names) =>
        names.Select(n => new CardModel { Name = n, Layout = layout, TypeLine = "Creature" }).ToList();

    // --- B2: only genuinely two-sided layouts may become a back face ----------

    [Theory]
    [InlineData("transform")]
    [InlineData("modal_dfc")]
    [InlineData("battle")]
    [InlineData("reversible_card")]
    [InlineData("double_faced_token")]
    public void TwoSidedLayouts_BecomeOneCardWithABackFace(string layout)
    {
        var card = new CardModel { Name = "Front", TemplateName = "Gold" };
        var extras = CardDetailsFill.AttachFaces(card, Faces(layout, "Front", "Back"));

        Assert.Empty(extras);
        Assert.True(card.IsDoubleFaced);
        Assert.Equal("Back", card.BackFace!.Name);
        Assert.True(card.BackFace.IsBackFace);
        Assert.Equal("Gold", card.BackFace.TemplateName);    // the back inherits the front's frame
        Assert.Equal("sunmoon", card.DfcStyle);              // default indicator
    }

    [Theory]
    [InlineData("split")]
    [InlineData("flip")]
    [InlineData("aftermath")]
    [InlineData("adventure")]
    [InlineData("")]
    public void SingleSidedMultiFaceLayouts_DoNotBecomeDoubleFaced(string layout)
    {
        var card = new CardModel { Name = "Fire" };
        var extras = CardDetailsFill.AttachFaces(card, Faces(layout, "Fire", "Ice"));

        Assert.False(card.IsDoubleFaced);
        Assert.Equal("", card.DfcStyle);                     // no indicator invented
        Assert.Single(extras);
        Assert.Equal("Ice", extras[0].Name);                 // handed back to be added as its own card
    }

    [Fact]
    public void ASingleFacedCard_GetsNoBackAndNoExtras()
    {
        var card = new CardModel { Name = "Bolt" };
        Assert.Empty(CardDetailsFill.AttachFaces(card, Faces("normal", "Bolt")));
        Assert.False(card.IsDoubleFaced);
    }

    [Fact]
    public void AttachFaces_KeepsTheBacksArtUrl_WhichCloneDrops()
    {
        var faces = Faces("transform", "Front", "Back");
        faces[1].ArtUrl = "https://example.com/back-art.jpg";

        var card = new CardModel { Name = "Front" };
        CardDetailsFill.AttachFaces(card, faces);

        // The art download runs after the attach, so losing the transient URL would mean a back with no art.
        Assert.Equal("https://example.com/back-art.jpg", card.BackFace!.ArtUrl);
    }

    [Fact]
    public void AttachFaces_DoesNotAliasTheSourceFace()
    {
        var faces = Faces("transform", "Front", "Back");
        var a = new CardModel { Name = "A" };
        var b = new CardModel { Name = "B" };

        CardDetailsFill.AttachFaces(a, faces);
        CardDetailsFill.AttachFaces(b, faces);
        a.BackFace!.Name = "Changed";

        Assert.NotSame(a.BackFace, b.BackFace);
        Assert.Equal("Back", b.BackFace!.Name);              // two cards never share one back face
        Assert.Equal("Back", faces[1].Name);                 // nor is the looked-up face mutated
    }

    [Fact]
    public void AttachFaces_KeepsAnIndicatorTheUserAlreadyChose()
    {
        var card = new CardModel { Name = "Front", DfcStyle = "arrow" };
        CardDetailsFill.AttachFaces(card, Faces("transform", "Front", "Back"));
        Assert.Equal("arrow", card.DfcStyle);
    }

    // --- B3: CopyFrom must deep-copy the back face ----------------------------

    [Fact]
    public void CopyFrom_DeepCopiesTheBackFace_RatherThanSharingIt()
    {
        var source = new CardModel { Name = "Src" };
        source.BackFace = new CardModel { Name = "SrcBack" };
        var target = new CardModel { Name = "Dst" };

        target.CopyFrom(source);

        Assert.NotSame(source.BackFace, target.BackFace);
        Assert.Equal("SrcBack", target.BackFace!.Name);
        Assert.True(target.BackFace.IsBackFace);

        target.BackFace.Name = "Mine";
        Assert.Equal("SrcBack", source.BackFace!.Name);      // the source card is untouched
    }

    [Fact]
    public void CopyFrom_ASnapshotWithoutABack_RemovesOne() // details-editor Cancel after a DFC fill
    {
        var card = new CardModel { Name = "C" };
        var snapshot = card.Clone();                          // taken before the fill: single-faced
        card.BackFace = new CardModel { Name = "Added" };

        card.CopyFrom(snapshot);

        Assert.False(card.IsDoubleFaced);
    }

    // --- B4: the back face is validated too -----------------------------------

    [Fact]
    public void ValidateAll_ReportsAProblemOnTheBackFace()
    {
        var spec = new TemplateSpec { Name = "T" };
        var card = new CardModel { Name = "Front", TypeLine = "Creature — Werewolf", Power = "2", Toughness = "2" };
        card.BackFace = new CardModel
        {
            Name = "Nightform",
            TypeLine = "Creature — Werewolf",
            Power = "4",            // half a P/T on the BACK — invalid, and previously invisible
        };

        var issues = SetValidator.ValidateAll(new[] { card }, _ => spec);

        Assert.Single(issues);
        Assert.Contains(issues[0].Issues, i => i.Message.StartsWith("Back face:", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_CleanDoubleFacedCard_HasNoIssues()
    {
        var spec = new TemplateSpec { Name = "T" };
        var card = new CardModel { Name = "Front", TypeLine = "Creature — Human", Power = "1", Toughness = "1" };
        card.BackFace = new CardModel { Name = "Back", TypeLine = "Creature — Werewolf", Power = "3", Toughness = "3" };

        Assert.Empty(SetValidator.ValidateAll(new[] { card }, _ => spec));
    }
}
