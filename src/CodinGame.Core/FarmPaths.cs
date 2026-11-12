namespace CodinGame.Core;

/// <summary>
/// Emplacements de la ferme, lus dans l'environnement :
/// <c>FARM_DATA</c> (données vivantes, modifiables), <c>FARM_SEED</c> (pipelines livrées
/// avec l'application), <c>CODINGAME_TOKEN_FILE</c> (token, hors dépôt).
/// </summary>
public sealed class FarmPaths
{
    public FarmPaths(string dataRoot, string seedRoot, string tokenFile)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        SeedRoot = Path.GetFullPath(seedRoot);
        TokenFile = tokenFile;
    }

    public string DataRoot { get; }
    public string SeedRoot { get; }
    public string TokenFile { get; }

    public static FarmPaths FromEnvironment() => new(
        Environment.GetEnvironmentVariable("FARM_DATA") ?? Path.Combine(Environment.CurrentDirectory, "farm-data"),
        Environment.GetEnvironmentVariable("FARM_SEED") ?? Path.Combine(AppContext.BaseDirectory, "pipelines"),
        Environment.GetEnvironmentVariable("CODINGAME_TOKEN_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "codingame", "token"));

    /// <summary>Les pipelines livrées : un dossier par jeu, contenant un <c>pipeline.json</c>.</summary>
    public IEnumerable<string> PipelineNames() =>
        Directory.Exists(SeedRoot)
            ? Directory.EnumerateDirectories(SeedRoot)
                .Where(d => File.Exists(Path.Combine(d, "pipeline.json")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order()
            : [];

    public PipelinePaths Pipeline(string name) => new(Path.Combine(SeedRoot, name), Path.Combine(DataRoot, "pipelines", name));
}

/// <summary>Fichiers d'une pipeline : la graine (lecture seule) et le dossier de données (vivant).</summary>
public sealed class PipelinePaths(string seedDir, string dataDir)
{
    public string SeedDir { get; } = seedDir;
    public string DataDir { get; } = dataDir;

    public string ConfigSeed => Path.Combine(SeedDir, "pipeline.json");
    public string ConfigData => Path.Combine(DataDir, "pipeline.json");
    public string Journal => Path.Combine(DataDir, "matches.db");
    public string Rules => Path.Combine(DataDir, "regles.md");

    /// <summary>Ce que les agents doivent savoir du moteur du jeu (livré avec la pipeline).</summary>
    public string EngineNotes => Path.Combine(SeedDir, "moteur.md");

    /// <summary>Techniques qui ont fait leurs preuves dans ce jeu, pour orienter le penseur (livré avec la pipeline).</summary>
    public string Strategy => Path.Combine(SeedDir, "strategie.md");
    public string Orientation => Path.Combine(DataDir, "orientation.md");
    public string Trials => Path.Combine(DataDir, "essais");
    public string BotCache => Path.Combine(DataDir, "cache", "bots");

    /// <summary>Le champion courant, gardé en clair pour que l'utilisateur puisse le lire ou le soumettre.</summary>
    public string ChampionFile(string botFile) => Path.Combine(DataDir, "champion" + Path.GetExtension(botFile));

    /// <summary>Bot de départ livré avec la pipeline.</summary>
    public string SeedBot(string botFile) => Path.Combine(SeedDir, "bot", botFile);

    /// <summary>Consignes de l'utilisateur : copiées une fois dans les données, puis modifiables.</summary>
    public string UserGuidance()
    {
        var data = Path.Combine(DataDir, "consignes.md");
        var seed = Path.Combine(SeedDir, "consignes.md");
        if (!File.Exists(data) && File.Exists(seed))
        {
            Directory.CreateDirectory(DataDir);
            File.Copy(seed, data);
        }

        return data;
    }

    /// <summary>Un gabarit de consigne d'agent : la version du dossier de données l'emporte si elle existe.</summary>
    public string Prompt(string role)
    {
        var data = Path.Combine(DataDir, "prompts", role + ".md");
        return File.Exists(data) ? data : Path.Combine(SeedDir, "prompts", role + ".md");
    }
}
