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

    [Fact]
    public void A_setting_left_at_its_default_follows_a_new_default()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        var notes = new List<string>();

        var cfg = ConfigLoader.Load(NewSeed(o => o["arene"]!["parties_prolongation"] = 5000), DataPath, notes);

        Assert.Equal(5000, cfg.Arene.PartiesProlongation);
        Assert.Contains(notes, n => n.Contains("arene.parties_prolongation (3000 → 5000)"));
    }

    [Fact]
    public void A_setting_changed_by_hand_keeps_its_value_when_the_default_changes()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        Edit(o => o["arene"]!["parties_prolongation"] = 1234);

        var cfg = ConfigLoader.Load(NewSeed(o => o["arene"]!["parties_prolongation"] = 5000), DataPath);

        Assert.Equal(1234, cfg.Arene.PartiesProlongation);
    }

    [Fact]
    public void Without_the_last_defaults_every_live_value_counts_as_set_by_hand()
    {
        ConfigLoader.Load(SeedPath, DataPath);
        File.Delete(ConfigLoader.DefaultsPath(DataPath));
        var seed = NewSeed(o => o["arene"]!["parties_prolongation"] = 5000);

        Assert.Equal(3000, ConfigLoader.Load(seed, DataPath).Arene.PartiesProlongation);
        Assert.True(File.Exists(ConfigLoader.DefaultsPath(DataPath)));
        Assert.Equal(3000, ConfigLoader.Load(seed, DataPath).Arene.PartiesProlongation);
    }

    /// <summary>A copy of the shipped seed with one default changed, as a new version would ship it.</summary>
    private string NewSeed(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(File.ReadAllText(SeedPath))!.AsObject();
        change(o);
        var path = Path.Combine(_dir, "seed.json");
        File.WriteAllText(path, o.ToJsonString());
        return path;
    }

    private void Edit(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(File.ReadAllText(DataPath))!.AsObject();
        change(o);
        File.WriteAllText(DataPath, o.ToJsonString());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
