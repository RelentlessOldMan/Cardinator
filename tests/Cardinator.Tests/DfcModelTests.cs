using System.IO;
using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>DFC foundation: a card can carry an optional back face (recursive, additive, back-compatible).</summary>
public class DfcModelTests
{
    [Fact]
    public void NewCard_IsSingleFaced()
    {
        var c = new CardModel { Name = "A" };
        Assert.False(c.IsDoubleFaced);
        Assert.Null(c.BackFace);
    }

    [Fact]
    public void SettingBackFace_MarksItAsBack_AndEnforcesOneLevel()
    {
        var front = new CardModel { Name = "Front" };
        var back = new CardModel { Name = "Back", BackFace = new CardModel { Name = "Nested" } };

        front.BackFace = back;

        Assert.True(front.IsDoubleFaced);
        Assert.True(front.BackFace!.IsBackFace);
        Assert.Null(front.BackFace.BackFace);   // nested back dropped — one level only
    }

    [Fact]
    public void RoundTrip_PreservesBackFace_AndReMarksIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "cardinator_dfc_" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var front = new CardModel
            {
                Name = "Werewolf", TypeLine = "Creature — Human",
                DfcStyle = "sunmoon",
                BackFace = new CardModel { Name = "Nightform", TypeLine = "Creature — Werewolf", Power = "4", Toughness = "4" },
            };
            front.Save(path);

            var loaded = CardModel.Load(path);
            Assert.True(loaded.IsDoubleFaced);
            Assert.Equal("sunmoon", loaded.DfcStyle);
            Assert.Equal("Nightform", loaded.BackFace!.Name);
            Assert.Equal("4", loaded.BackFace.Power);
            Assert.True(loaded.BackFace.IsBackFace);          // transient flag re-applied on load
            Assert.Null(loaded.BackFace.BackFace);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Clone_DeepCopiesBackFace()
    {
        var front = new CardModel { Name = "F", BackFace = new CardModel { Name = "B" } };
        var clone = front.Clone();

        Assert.NotSame(front.BackFace, clone.BackFace);
        Assert.Equal("B", clone.BackFace!.Name);
        clone.BackFace.Name = "Changed";
        Assert.Equal("B", front.BackFace!.Name);              // original untouched
    }

    [Fact]
    public void CopyFrom_DoesNotClobberIsBackFace()   // review fix: back-face editor Cancel kept the back glyph
    {
        var front = new CardModel { Name = "F", BackFace = new CardModel { Name = "B" } };
        var back = front.BackFace!;
        Assert.True(back.IsBackFace);

        var snapshot = back.Clone();           // a standalone clone has IsBackFace == false (transient/JsonIgnore)
        Assert.False(snapshot.IsBackFace);
        back.CopyFrom(snapshot);               // simulate the details-editor Cancel

        Assert.True(back.IsBackFace);          // still a back face — the flag wasn't overwritten
    }

    [Fact]
    public void ExpandFaces_FlattensFrontThenBack()   // single-sided sheet proxy expansion
    {
        var cards = new List<CardModel>
        {
            new() { Name = "Plain" },
            new() { Name = "Front", BackFace = new CardModel { Name = "Back" } },
        };

        var faces = Cardinator.Services.BatchService.ExpandFaces(cards);

        Assert.Equal(3, faces.Count);
        Assert.Equal("Plain", faces[0].Name);
        Assert.Equal("Front", faces[1].Name);
        Assert.Equal("Back", faces[2].Name);
    }

    [Fact]
    public void OldFileWithoutBackFace_LoadsAsSingleFaced()
    {
        var path = Path.Combine(Path.GetTempPath(), "cardinator_dfc_old_" + System.Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"name\": \"Legacy\", \"typeLine\": \"Instant\" }");
        try
        {
            var c = CardModel.Load(path);
            Assert.Equal("Legacy", c.Name);
            Assert.False(c.IsDoubleFaced);
            Assert.Equal("", c.DfcStyle);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
