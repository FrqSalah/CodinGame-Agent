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
    [InlineData("TARGET: `blocking` (or `speed`?)", "blocking")]
    [InlineData("TARGET:\nfinish\nWHY: ...", "finish")]
    public void Target_is_a_known_cause_or_nothing(string note, string? expected) =>
        Assert.Equal(expected, Trial.TargetOf(note));

    /// <summary>Trial 189: the thinker sent its draft (prompt format quoted, word count) and glued its answer to it.</summary>
    [Fact]
    public void The_draft_before_the_answer_is_dropped()
    {
        var response = """
            Let me recall the format of the answer:
            IDEA: one sentence.
            TARGET: one word, the cause of LOSSES.md this idea fixes.

            Let's do a word count check:
            IDEA: 26 words.
            TARGET: 1 word.
            Everything is consistent.IDEA: Score the opponent runner like ours.

            TARGET: blocking

            WHY: the blocker gets no credit for slowing it.
            """;

        var note = Trial.AnswerOf(response);

        Assert.StartsWith("IDEA: Score the opponent runner like ours.", note);
        Assert.Equal("Score the opponent runner like ours.", Trial.IdeaOf(note));
        Assert.Equal("blocking", Trial.TargetOf(note));
    }

    [Theory]
    [InlineData("**IDEA:** Aim at the apex.\nWHY: ...", "**IDEA:** Aim at the apex.\nWHY: ...")]
    [InlineData("Draft.\n## IDEA: Aim at the apex.", "## IDEA: Aim at the apex.")]
    [InlineData("IDEE : Viser la corde.", "IDEE : Viser la corde.")]
    public void A_single_answer_is_kept_with_its_markdown(string response, string expected) =>
        Assert.Equal(expected, Trial.AnswerOf(response));
}
