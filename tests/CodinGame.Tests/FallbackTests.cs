using CodinGame.Core.Agents;
using CodinGame.Core.Config;

namespace CodinGame.Tests;

public class FallbackTests
{
    [Theory]
    [InlineData("statut ERROR : Quota exceeded for this model, try again later", true)]
    [InlineData("statut ERROR : 429 Too Many Requests", true)]
    [InlineData("statut ERROR : RESOURCE_EXHAUSTED", true)]
    [InlineData("statut ERROR : model is currently unavailable", true)]
    [InlineData("statut ERROR : Your previous response was cut off because it exceeded the output token limit", false)]
    [InlineData("action refusée par Antigravity (read_file) : il manque une règle d'autorisation", false)]
    [InlineData("durée maximale dépassée (20 min) : processus arrêté", false)]
    [InlineData(null, false)]
    public void Only_limits_and_unavailability_move_to_the_next_model(string? error, bool exhausted) =>
        Assert.Equal(exhausted, FallbackAgentRunner.IsExhausted(error));

    [Fact]
    public void Chain_starts_with_the_strongest_and_skips_duplicates()
    {
        var role = new AgentRole("agy", "fort", 20, ["moyen", "fort", "faible"]);

        Assert.Equal(["fort", "moyen", "faible"], role.Chain());
    }
}
