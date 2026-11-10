using System.Text.Json.Nodes;
using CodinGame.Core.Config;

namespace CodinGame.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("farm-config-").FullName;

    private static string SeedPath =>
        Path.Combine(AppContext.BaseDirectory, "pipelines", "mad-pod-racing", "pipeline.json");

    private string DataPath => Path.Combine(_dir, "pipeline.json");

    [Fact]
    public void Shipped_configuration_is_valid_and_copied_on_first_load()
    {
        var notes = new List<string>();
        var cfg = ConfigLoader.Load(SeedPath, DataPath, notes);

        Assert.Equal("mad-pod-racing", cfg.Jeu);
        Assert.True(File.Exists(DataPath));
        Assert.Contains(notes, n => n.Contains("créée"));
    }

    [Fact]
    public void User_edits_win_and_are_reread()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        Edit(o => o["agents"]!["codeur"]!["modele"] = "autre-modele");

        Assert.Equal("autre-modele", ConfigLoader.Load(SeedPath, DataPath).Agents.Codeur.Modele);
    }

    [Fact]
    public void Missing_keys_are_restored_from_the_seed()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        Edit(o => o.Remove("contexte"));
        var notes = new List<string>();

        var cfg = ConfigLoader.Load(SeedPath, DataPath, notes);

        Assert.Equal(15, cfg.Contexte.EssaisResumes);
        Assert.Contains(notes, n => n.Contains("contexte"));
    }

    [Fact]
    public void A_typo_is_refused_with_its_name()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        Edit(o => o["boucle"]!["essais_par_jor"] = 3);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(SeedPath, DataPath));
        Assert.Contains("essais_par_jor", ex.Message);
    }

    [Fact]
    public void An_unknown_tool_is_refused_with_its_field()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        Edit(o => o["agents"]!["penseur"]!["outil"] = "inconnu");

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(SeedPath, DataPath));
        Assert.Contains("agents.penseur.outil", ex.Message);
    }

    private void Edit(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(File.ReadAllText(DataPath))!.AsObject();
        change(o);
        File.WriteAllText(DataPath, o.ToJsonString());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
