using CodinGame.Core;
using CodinGame.Core.Agents;
using CodinGame.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Theory]
    [InlineData("statut ERROR : Your previous response was cut off because it exceeded the output token limit Please continue", true)]
    [InlineData("statut ERROR : print timeout after 20m0s with the turn still in progress; the response is partial", true)]
    [InlineData("durée maximale dépassée (20 min) : processus arrêté", true)]
    [InlineData("statut ERROR : Quota exceeded for this model, try again later", false)]
    [InlineData("action refusée par Antigravity (read_file) : il manque une règle d'autorisation", false)]
    [InlineData(null, false)]
    public void An_unfinished_answer_is_retried_with_the_next_model_without_blocking(string? error, bool overrun)
    {
        Assert.Equal(overrun, FallbackAgentRunner.IsOverrun(error));
        if (overrun)
        {
            Assert.False(FallbackAgentRunner.IsExhausted(error));
        }
    }

    [Fact]
    public void Chain_starts_with_the_strongest_and_skips_duplicates()
    {
        var role = new AgentRole("agy", "fort", 20, ["moyen", "fort", "faible"]);

        Assert.Equal(["fort", "moyen", "faible"], role.Chain());
    }

    private const string CutOff = "statut ERROR : Your previous response was cut off because it exceeded the output token limit";

    [Fact]
    public async Task A_cut_answer_is_asked_again_of_the_same_model_first()
    {
        var inner = new ScriptedRunner(Failed(CutOff), Done());

        var result = await RunAsync(inner);

        Assert.True(result.Success);
        Assert.Equal("fort", result.Model);
        Assert.Equal(["fort", "fort"], inner.Models);
    }

    [Fact]
    public async Task An_answer_cut_twice_moves_to_the_next_model()
    {
        var inner = new ScriptedRunner(Failed(CutOff), Failed(CutOff), Done());

        var result = await RunAsync(inner);

        Assert.True(result.Success);
        Assert.Equal("moyen", result.Model);
        Assert.Equal(["fort", "fort", "moyen"], inner.Models);
    }

    private static Task<AgentResult> RunAsync(IAgentRunner inner)
    {
        var paths = new FarmPaths(Path.Combine(Path.GetTempPath(), $"fallback-{Guid.NewGuid():N}"), ".", "absent-token");
        var runner = new FallbackAgentRunner(inner, paths, NullLogger<FallbackAgentRunner>.Instance);
        var role = new AgentRole("agy", "fort", 20, ["moyen", "faible"]);
        return runner.RunAsync(new AgentRequest("penseur", role, ".", "prompt"), TestContext.Current.CancellationToken);
    }

    private static AgentResult Failed(string error) => new(false, "", error, 1, 0);

    private static AgentResult Done() => new(true, "IDEA: x", null, 1, 0);

    /// <summary>Returns the given results in order and records the model of each call.</summary>
    private sealed class ScriptedRunner(params AgentResult[] results) : IAgentRunner
    {
        private readonly Queue<AgentResult> _results = new(results);

        public List<string> Models { get; } = [];

        public Task<AgentResult> RunAsync(AgentRequest request, CancellationToken ct)
        {
            Models.Add(request.Settings.Modele);
            return Task.FromResult(_results.Dequeue());
        }

        public Task<AgentStatus> CheckAsync(CancellationToken ct) => Task.FromResult(new AgentStatus(AgentAvailability.Ready, [], ""));

        public Task GrantAsync(string directory, CancellationToken ct) => Task.CompletedTask;
    }
}
