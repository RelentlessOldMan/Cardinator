using System;
using System.IO;
using Cardinator.Models;

namespace Cardinator.Tests;

/// <summary>Only a set opens as a set (1.6.12): a card or frame JSON used to "load" as an empty set, and the
/// next Save overwrote it.</summary>
public class NotAProjectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cardinator-notaproject-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string json)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    [Theory]
    [InlineData("{ \"name\": \"Lightning Bolt\", \"manaCost\": \"{R}\", \"typeLine\": \"Instant\" }")]   // a card
    [InlineData("{ \"name\": \"My Frame\", \"canvasWidth\": 750, \"artWindow\": { \"x\": 1 } }")]   // a template.json
    [InlineData("[ { \"name\": \"x\" } ]")]
    [InlineData("42")]
    public void AnythingButASet_IsRefused(string json)
        => Assert.Throws<NotAProjectException>(() => CardProject.Load(Write(json)));

    [Theory]
    [InlineData("{ \"cards\": [] }")]
    [InlineData("{ \"Cards\": [ { \"name\": \"A\" } ], }")]   // casing drift + trailing comma, as JsonCompat allows
    [InlineData("// a comment\n{ \"name\": \"Set\", \"cards\": [] }")]
    public void ASet_StillLoads(string json)
        => Assert.NotNull(CardProject.Load(Write(json)));

    [Fact]
    public void BrokenJson_IsStillTreatedAsCorrupt_NotAsANonSet()
    {
        var ex = Record.Exception(() => CardProject.Load(Write("{ \"cards\": [ ")));
        Assert.NotNull(ex);
        Assert.IsNotType<NotAProjectException>(ex);
    }
}
