using CodinGame.Core.Pipeline;

namespace CodinGame.Tests;

public class IdeaTests
{
    [Theory]
    [InlineData("IDEE : Viser la corde.\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("IDEE :\nViser la corde.\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("**IDEE :**\n\n**Viser la corde.**\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("## IDÉE : Viser la corde.", "Viser la corde.")]
    [InlineData("Viser la corde, sans étiquette.", "Viser la corde, sans étiquette.")]
    public void Idea_is_found_on_the_same_or_the_next_line(string note, string expected) =>
        Assert.Equal(expected, Trial.IdeaOf(note));
}
