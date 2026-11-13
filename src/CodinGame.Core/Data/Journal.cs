using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodinGame.Core.Data;

public sealed record TrialSummary(
    long Id, string Start, string? Verdict, string? Reason, string? Idea,
    int Wins, int Losses, int Draws, string? Penseur, string? Codeur);

public sealed record ApiGameEntry(long GameId, string BotHash, string Opponent, string Options, string Outcome, bool Fault);

/// <summary>
/// Le journal d'une pipeline (<c>matches.db</c>) : seule source de vérité pour les
/// résultats. Il garde aussi le source de chaque bot essayé, gardé ou jeté, ce qui
/// remplace un historique Git des expériences.
/// </summary>
public sealed class Journal : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public Journal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS bots (
                hash TEXT PRIMARY KEY, source TEXT NOT NULL, cree_le TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS essais (
                id INTEGER PRIMARY KEY, debut TEXT NOT NULL, fin TEXT, verdict TEXT, raison TEXT,
                idee TEXT, note TEXT, penseur TEXT, codeur TEXT, champion_hash TEXT, candidat_hash TEXT,
                victoires INTEGER NOT NULL DEFAULT 0, defaites INTEGER NOT NULL DEFAULT 0,
                egalites INTEGER NOT NULL DEFAULT 0, llr REAL, corrections INTEGER NOT NULL DEFAULT 0,
                tokens INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS parties_locales (
                id INTEGER PRIMARY KEY, essai_id INTEGER NOT NULL, graine INTEGER NOT NULL,
                candidat_joueur INTEGER NOT NULL, resultat TEXT NOT NULL, faute TEXT, jouee_le TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS parties_api (
                game_id INTEGER PRIMARY KEY, bot_hash TEXT NOT NULL, adversaire TEXT NOT NULL,
                options TEXT NOT NULL, resultat TEXT NOT NULL, faute INTEGER NOT NULL,
                essai_id INTEGER, jouee_le TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS champions (
                id INTEGER PRIMARY KEY, bot_hash TEXT NOT NULL, essai_id INTEGER, depuis TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS parties_banc (
                bot_hash TEXT NOT NULL, adversaire TEXT NOT NULL, graine INTEGER NOT NULL, siege INTEGER NOT NULL,
                resultat TEXT NOT NULL, faute TEXT, jouee_le TEXT NOT NULL,
                PRIMARY KEY (bot_hash, adversaire, graine, siege));
            CREATE TABLE IF NOT EXISTS orientations (
                id INTEGER PRIMARY KEY, essai_id INTEGER, texte TEXT NOT NULL, cree_le TEXT NOT NULL);
            """);

        // Colonnes ajoutées après la mise en service : ajoutées aux journaux existants.
        var columns = Query("PRAGMA table_info(essais)", r => r.GetString(1));
        foreach (var role in TokenRoles.Values.Where(c => !columns.Contains(c)))
        {
            Exec($"ALTER TABLE essais ADD COLUMN {role} INTEGER NOT NULL DEFAULT 0");
        }
    }

    /// <summary>Colonne de consommation de chaque rôle d'agent.</summary>
    private static readonly Dictionary<string, string> TokenRoles = new()
    {
        ["penseur"] = "tokens_penseur",
        ["codeur"] = "tokens_codeur",
        ["regard_neuf"] = "tokens_regard_neuf",
    };

    public static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    // ---- Bots et champion ----

    public void SaveBot(string hash, string source) =>
        Exec("INSERT OR IGNORE INTO bots (hash, source, cree_le) VALUES ($h, $s, $t)", ("$h", hash), ("$s", source), ("$t", Now()));

    public string? BotSource(string hash) =>
        Scalar("SELECT source FROM bots WHERE hash = $h", ("$h", hash)) as string;

    public string? ChampionHash() =>
        Scalar("SELECT bot_hash FROM champions ORDER BY id DESC LIMIT 1") as string;

    public void SetChampion(string hash, long? trialId) =>
        Exec("INSERT INTO champions (bot_hash, essai_id, depuis) VALUES ($h, $e, $t)", ("$h", hash), ("$e", trialId), ("$t", Now()));

    public int ChampionCount() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM champions"), CultureInfo.InvariantCulture);

    // ---- Essais ----

    public long StartTrial(string championHash, string penseur, string codeur)
    {
        Exec("INSERT INTO essais (debut, champion_hash, penseur, codeur) VALUES ($t, $c, $p, $k)",
            ("$t", Now()), ("$c", championHash), ("$p", penseur), ("$k", codeur));
        return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
    }

    /// <summary>Recalcule les idées enregistrées vides à partir de leur note. Renvoie le nombre réparé.</summary>
    public int RepairIdeas(Func<string, string> ideaOf)
    {
        var broken = Query(
            "SELECT id, note FROM essais WHERE note IS NOT NULL AND TRIM(COALESCE(idee, '')) IN ('', 'IDEE :', 'IDEE:', 'IDÉE :', 'IDÉE:')",
            r => (r.GetInt64(0), r.GetString(1)));
        foreach (var (id, note) in broken)
        {
            Exec("UPDATE essais SET idee = $i WHERE id = $id", ("$i", ideaOf(note)), ("$id", id));
        }

        return broken.Count;
    }

    public void SetTrialNote(long id, string idea, string note) =>
        Exec("UPDATE essais SET idee = $i, note = $n WHERE id = $id", ("$i", idea), ("$n", note), ("$id", id));

    public (string? Hash, string? Idea, string? Note) TrialCandidate(long id) =>
        Query("SELECT candidat_hash, idee, note FROM essais WHERE id = $id",
            r => (Str(r, 0), Str(r, 1), Str(r, 2)), ("$id", id)).FirstOrDefault();

    public void SetTrialCandidate(long id, string hash, int corrections) =>
        Exec("UPDATE essais SET candidat_hash = $h, corrections = $c WHERE id = $id", ("$h", hash), ("$c", corrections), ("$id", id));

    public void AddTrialTokens(long id, string role, long tokens) =>
        Exec($"UPDATE essais SET tokens = tokens + $t, {TokenRoles[role]} = {TokenRoles[role]} + $t WHERE id = $id",
            ("$t", tokens), ("$id", id));

    /// <summary>Consommation moyenne par rôle sur les derniers essais mesurés par rôle.</summary>
    public (int Trials, double Penseur, double Codeur, double RegardNeuf, double Total) TokenAverages(int last) =>
        Query("""
            SELECT COUNT(*), AVG(tokens_penseur), AVG(tokens_codeur), AVG(tokens_regard_neuf), AVG(tokens) FROM (
                SELECT * FROM essais WHERE fin IS NOT NULL AND tokens_penseur + tokens_codeur + tokens_regard_neuf > 0
                ORDER BY id DESC LIMIT $n)
            """, r => (r.GetInt32(0), D(r, 1), D(r, 2), D(r, 3), D(r, 4)), ("$n", last)).First();

    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    public void FinishTrial(long id, string verdict, string reason, int wins, int losses, int draws, double? llr) =>
        Exec("""
            UPDATE essais SET fin = $f, verdict = $v, raison = $r, victoires = $w, defaites = $l, egalites = $d, llr = $x
            WHERE id = $id
            """, ("$f", Now()), ("$v", verdict), ("$r", reason), ("$w", wins), ("$l", losses), ("$d", draws), ("$x", llr), ("$id", id));

    /// <summary>Clôt les essais restés ouverts (arrêt brutal) : ils ne sont pas repris.</summary>
    public int CloseInterruptedTrials() =>
        Exec("UPDATE essais SET fin = $f, verdict = 'INTERROMPU', raison = 'arrêt pendant l''essai' WHERE fin IS NULL", ("$f", Now()));

    public IReadOnlyList<TrialSummary> RecentTrials(int count) =>
        Query("""
            SELECT id, debut, verdict, raison, idee, victoires, defaites, egalites, penseur, codeur
            FROM essais WHERE fin IS NOT NULL ORDER BY id DESC LIMIT $n
            """, ReadTrial, ("$n", count));

    public IReadOnlyList<TrialSummary> AllTrials() =>
        Query("""
            SELECT id, debut, verdict, raison, idee, victoires, defaites, egalites, penseur, codeur
            FROM essais WHERE fin IS NOT NULL ORDER BY id
            """, ReadTrial);

    public int TrialsSince(DateTime utc) =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM essais WHERE debut >= $t", ("$t", utc.ToString("O", CultureInfo.InvariantCulture))), CultureInfo.InvariantCulture);

    /// <summary>Essais terminés sans promotion depuis la dernière promotion ou la dernière orientation.</summary>
    public int NonKeptSinceLastChange()
    {
        var since = Scalar("""
            SELECT MAX(x) FROM (
                SELECT MAX(essai_id) AS x FROM champions
                UNION ALL SELECT MAX(essai_id) FROM orientations)
            """);
        var from = since is null or DBNull ? 0L : Convert.ToInt64(since, CultureInfo.InvariantCulture);
        return Convert.ToInt32(Scalar(
            "SELECT COUNT(*) FROM essais WHERE id > $f AND fin IS NOT NULL AND verdict NOT IN ('GARDE', 'INTERROMPU')",
            ("$f", from)), CultureInfo.InvariantCulture);
    }

    public long LastTrialId() => Convert.ToInt64(Scalar("SELECT COALESCE(MAX(id), 0) FROM essais"), CultureInfo.InvariantCulture);

    private static TrialSummary ReadTrial(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4),
        r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), Str(r, 8), Str(r, 9));

    // ---- Parties ----

    public void AddLocalGame(long trialId, long seed, int candidatePlayer, string result, string? fault) =>
        Exec("""
            INSERT INTO parties_locales (essai_id, graine, candidat_joueur, resultat, faute, jouee_le)
            VALUES ($e, $g, $j, $r, $f, $d)
            """, ("$e", trialId), ("$g", seed), ("$j", candidatePlayer), ("$r", result), ("$f", fault), ("$d", Now()));

    public void AddApiGame(long gameId, string botHash, string opponent, string options, string outcome, bool fault, long? trialId) =>
        Exec("""
            INSERT OR IGNORE INTO parties_api (game_id, bot_hash, adversaire, options, resultat, faute, essai_id, jouee_le)
            VALUES ($g, $b, $a, $o, $r, $f, $e, $d)
            """, ("$g", gameId), ("$b", botHash), ("$a", opponent), ("$o", options), ("$r", outcome), ("$f", fault ? 1 : 0), ("$e", trialId), ("$d", Now()));

    public IReadOnlyList<ApiGameEntry> ApiGames(string botHash, string opponent) =>
        Query("SELECT game_id, bot_hash, adversaire, options, resultat, faute FROM parties_api WHERE bot_hash = $b AND adversaire = $a",
            r => new ApiGameEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5) != 0),
            ("$b", botHash), ("$a", opponent));

    public Dictionary<Arena.BenchCase, string> BenchResults(string botHash) =>
        Query("SELECT adversaire, graine, siege, resultat FROM parties_banc WHERE bot_hash = $b",
            r => (new Arena.BenchCase(r.GetString(0), r.GetInt64(1), r.GetInt32(2)), r.GetString(3)), ("$b", botHash))
            .ToDictionary(x => x.Item1, x => x.Item2);

    public void AddBenchGame(string botHash, Arena.BenchCase c, string result, string? fault) =>
        Exec("""
            INSERT OR REPLACE INTO parties_banc (bot_hash, adversaire, graine, siege, resultat, faute, jouee_le)
            VALUES ($b, $a, $g, $s, $r, $f, $d)
            """, ("$b", botHash), ("$a", c.Opponent), ("$g", c.Seed), ("$s", c.Seat), ("$r", result), ("$f", fault), ("$d", Now()));

    public void AddOrientation(long? trialId, string text) =>
        Exec("INSERT INTO orientations (essai_id, texte, cree_le) VALUES ($e, $t, $d)", ("$e", trialId), ("$t", text), ("$d", Now()));

    public void Dispose() => _db.Dispose();

    // ---- Accès SQL ----

    private int Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteScalar();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read())
            {
                list.Add(read(reader));
            }

            return list;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
