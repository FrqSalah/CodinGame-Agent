using CodinGame.Core.Pipeline;

namespace CodinGame.Tests;

public class IdeaTests
{
    [Theory]
    [InlineData("IDEE : Viser la corde.\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("IDEE :\nViser la corde.\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("**IDEE :**\n\n**Viser la corde.**\nPOURQUOI : ...", "Viser la corde.")]
    [InlineData("## IDÉE : Viser la corde.", "Viser la corde.")]
    [InlineData("IDEA: Aim at the apex.\nWHY: ...", "Aim at the apex.")]
    [InlineData("**IDEA:**\n\nAim at the apex.\nWHY: ...", "Aim at the apex.")]
    [InlineData("Viser la corde, sans étiquette.", "Viser la corde, sans étiquette.")]
    public void Idea_is_found_on_the_same_or_the_next_line(string note, string expected) =>
        Assert.Equal(expected, Trial.IdeaOf(note));

    [Theory]
    [InlineData("IDEA: Block better.\nTARGET: blocking\nWHY: ...", "blocking")]
    [InlineData("**TARGET:** Speed.", "speed")]
    [InlineData("TARGET: the corners", null)]
    [InlineData("IDEA: no target line", null)]
    public void Target_is_a_known_cause_or_nothing(string note, string? expected) =>
        Assert.Equal(expected, Trial.TargetOf(note));
}
