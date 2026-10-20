using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using LumaChart.Application.Workspaces;

namespace LumaChart.Application.Libraries;

public sealed record MapperAnnotation(string[] Tags, string Note = "", string State = "未分类", bool Favorite = false)
{
    public static MapperAnnotation Empty => new([]);
    public string Summary => string.Join(" · ", new[] { Favorite ? "★ 收藏" : "", State == "未分类" ? "" : State,
        string.Join(" ", Tags.Select(tag => "#" + tag)), Note.Replace('\n', ' ') }.Where(s => s.Length > 0));
}

public sealed record LibraryCachedHeader(string Path, long Length, long ModifiedTicks, DesktopLibraryChartEntry? Entry);

/// <summary>Local, durable index. Personal information is in separate tables, never in .osu files.
/// All SQL is parameterized; a rebuild replaces chart headers only, not identities or annotations.</summary>
public sealed class MapperLibraryStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly string personalBackup;
    public string DatabasePath { get; }
    public MapperLibraryStore(string profile)
    {
        DatabasePath = Path.Combine(profile, "mapper-library.sqlite3");
        personalBackup = Path.Combine(profile, "library-personal.json");
        var fresh = !File.Exists(DatabasePath);
        OsuFileSafetyPolicy.RequireWritable(DatabasePath);
        Directory.CreateDirectory(profile);
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS identities(path TEXT PRIMARY KEY COLLATE NOCASE, identity TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS identity_index ON identities(identity);
            CREATE TABLE IF NOT EXISTS personal(identity TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS headers(path TEXT PRIMARY KEY COLLATE NOCASE, length INTEGER NOT NULL,
                modified INTEGER NOT NULL, payload TEXT, search TEXT NOT NULL, keys INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS header_keys ON headers(keys);
            CREATE VIRTUAL TABLE IF NOT EXISTS lookup USING fts5(path UNINDEXED, search, tokenize='trigram');
            CREATE TABLE IF NOT EXISTS index_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        using (var parser = Command(db, "SELECT value FROM index_metadata WHERE key='header_parser'"))
        {
            if (parser.ExecuteScalar() as string != "2")
            {
                using var revision = db.BeginTransaction();
                // Rebuild headers only when parsing rules change. Identities,
                // annotations, JSON backups and Git history remain untouched.
                Execute(db, "DELETE FROM lookup; DELETE FROM headers; INSERT OR REPLACE INTO index_metadata VALUES('header_parser','2');");
                revision.Commit();
            }
        }
        if (fresh && File.Exists(personalBackup))
        {
            var backup = JsonSerializer.Deserialize<PersonalBackup>(File.ReadAllBytes(personalBackup)) ?? throw new IOException("个人资料备份无法读取。");
            using var transaction = db.BeginTransaction();
            foreach (var alias in backup.Aliases) Execute(db, "INSERT OR IGNORE INTO identities VALUES($p,$i)", ("$p", Full(alias.Path)), ("$i", alias.Identity));
            foreach (var item in backup.Personal) Execute(db, "INSERT OR IGNORE INTO personal VALUES($i,$v)", ("$i", item.Identity), ("$v", item.Payload));
            transaction.Commit();
        }
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); Execute(db, "PRAGMA busy_timeout=10000;"); return db; }
    private static int Execute(SqliteConnection db, string sql, params (string Key, object? Value)[] args)
    {
        using var command = Command(db, sql, args); return command.ExecuteNonQuery();
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Key, object? Value)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static string Full(string path) => Path.GetFullPath(path);
    public string Identity(string path)
    {
        lock (gate)
        {
            using var db = Open(); path = Full(path);
            var created = Execute(db, "INSERT OR IGNORE INTO identities VALUES($p,$i)", ("$p", path), ("$i", Guid.NewGuid().ToString("N")));
            if (created > 0) BackupPersonal(db);
            using var cmd = Command(db, "SELECT identity FROM identities WHERE path=$p", ("$p", path));
            return (string)cmd.ExecuteScalar()!;
        }
    }
    public MapperAnnotation GetAnnotation(string path)
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT p.payload FROM identities i JOIN personal p ON p.identity=i.identity WHERE i.path=$p", ("$p", Full(path)));
            return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<MapperAnnotation>(text)! : MapperAnnotation.Empty;
        }
    }
    public void SetAnnotation(string path, MapperAnnotation annotation) => SetAnnotations([new(path, annotation)]);
    public void SetAnnotations(IEnumerable<KeyValuePair<string, MapperAnnotation>> annotations)
    {
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            foreach (var pair in annotations)
            {
                var annotation = pair.Value;
                var normalized = annotation with { Tags = annotation.Tags.Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray(), Note = annotation.Note.Trim() };
                var path = Full(pair.Key);
                Execute(db, "INSERT OR IGNORE INTO identities VALUES($p,$i)", ("$p", path), ("$i", Guid.NewGuid().ToString("N")));
                using var command = Command(db, "SELECT identity FROM identities WHERE path=$p", ("$p", path));
                var id = (string)command.ExecuteScalar()!;
                Execute(db, "INSERT INTO personal VALUES($i,$v) ON CONFLICT(identity) DO UPDATE SET payload=excluded.payload",
                    ("$i", id), ("$v", JsonSerializer.Serialize(normalized)));
                ReindexIdentity(db, id);
            }
            transaction.Commit();
            BackupPersonal(db);
        }
    }
    private sealed record Alias(string Path, string Identity);
    private sealed record Personal(string Identity, string Payload);
    private sealed record PersonalBackup(Alias[] Aliases, Personal[] Personal);
    private void BackupPersonal(SqliteConnection db)
    {
        var aliases = new List<Alias>(); var personal = new List<Personal>();
        using (var command = Command(db, "SELECT path,identity FROM identities"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) aliases.Add(new(reader.GetString(0), reader.GetString(1)));
        using (var command = Command(db, "SELECT identity,payload FROM personal"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) personal.Add(new(reader.GetString(0), reader.GetString(1)));
        var temporary = personalBackup + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, new PersonalBackup(aliases.ToArray(), personal.ToArray())); stream.Flush(true); }
        File.Move(temporary, personalBackup, true);
    }
    public void ClearHeaders()
    {
        lock (gate) { using var db = Open(); using var transaction = db.BeginTransaction(); Execute(db, "DELETE FROM lookup; DELETE FROM headers;"); transaction.Commit(); }
    }
    private static string PersonalSearch(MapperAnnotation a) => ("\n" + string.Join(' ', a.Tags) + "\n" + a.Note + "\n" + a.State).ToLowerInvariant();
    private static void ReindexIdentity(SqliteConnection db, string identity)
    {
        using var get = Command(db, "SELECT h.path,h.search,p.payload FROM identities i JOIN headers h ON h.path=i.path LEFT JOIN personal p ON p.identity=i.identity WHERE i.identity=$i", ("$i", identity));
        using var reader = get.ExecuteReader(); var rows = new List<(string Path, string Search)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1) + (reader.IsDBNull(2) ? "" : PersonalSearch(JsonSerializer.Deserialize<MapperAnnotation>(reader.GetString(2))!))));
        reader.Close();
        foreach (var row in rows)
        {
            Execute(db, "DELETE FROM lookup WHERE rowid=(SELECT rowid FROM headers WHERE path=$p)", ("$p", row.Path));
            Execute(db, "INSERT INTO lookup(rowid,path,search) SELECT rowid,$p,$s FROM headers WHERE path=$p", ("$p", row.Path), ("$s", row.Search));
        }
    }
    /// <summary>Index bookkeeping that must survive a restart (for example the
    /// last explicit refresh time) but is not part of any user document.</summary>
    public string? ReadIndexMetadata(string key)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = Command(db, "SELECT value FROM index_metadata WHERE key=$k", ("$k", key));
            return command.ExecuteScalar() as string;
        }
    }

    public void WriteIndexMetadata(string key, string value)
    {
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            Execute(db, "INSERT INTO index_metadata VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
            transaction.Commit();
        }
    }

    public IReadOnlyList<LibraryCachedHeader> LoadHeaders()
    {
        lock (gate)
        {
            using var db = Open(); using var command = Command(db, "SELECT path,length,modified,payload FROM headers"); using var reader = command.ExecuteReader();
            var paths = new List<string>(); var lengths = new List<long>();
            var modified = new List<long>(); var payloads = new List<string?>();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0)); lengths.Add(reader.GetInt64(1)); modified.Add(reader.GetInt64(2));
                payloads.Add(reader.IsDBNull(3) ? null : reader.GetString(3));
            }
            reader.Close();
            // JSON parsing is the dominant cost of a large index snapshot; it is
            // CPU-bound and independent per row, so it runs on the pool while the
            // window is already showing the cached list.
            var entries = new DesktopLibraryChartEntry?[paths.Count];
            Parallel.For(0, paths.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
                index =>
                {
                    var payload = payloads[index];
                    entries[index] = payload == null
                        ? null
                        : JsonSerializer.Deserialize(payload, LibraryIndexJsonContext.Default.DesktopLibraryChartEntry);
                });
            var list = new LibraryCachedHeader[paths.Count];
            for (var index = 0; index < paths.Count; index++)
                list[index] = new(paths[index], lengths[index], modified[index], entries[index]);
            return list;
        }
    }
    public void ForgetHeaders(IEnumerable<string> paths)
    {
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            foreach (var path in paths)
            {
                Execute(db, "DELETE FROM lookup WHERE rowid=(SELECT rowid FROM headers WHERE path=$p)", ("$p", Full(path)));
                Execute(db, "DELETE FROM headers WHERE path=$p", ("$p", Full(path)));
            }
            transaction.Commit();
        }
    }
    public void UpdateHeaders(IEnumerable<LibraryCachedHeader> headers)
    {
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            foreach (var header in headers)
            {
                var path = Full(header.Path); var chart = header.Entry;
                Execute(db, "INSERT INTO headers VALUES($p,$l,$m,$j,$s,$k) ON CONFLICT(path) DO UPDATE SET length=excluded.length,modified=excluded.modified,payload=excluded.payload,search=excluded.search,keys=excluded.keys",
                    ("$p", path), ("$l", header.Length), ("$m", header.ModifiedTicks), ("$j", chart == null ? null : JsonSerializer.Serialize(chart, LibraryIndexJsonContext.Default.DesktopLibraryChartEntry)), ("$s", chart?.SearchText ?? ""), ("$k", chart?.KeyCount ?? 0));
                Execute(db, "DELETE FROM lookup WHERE rowid=(SELECT rowid FROM headers WHERE path=$p)", ("$p", path));
                Execute(db, "INSERT INTO lookup(rowid,path,search) SELECT h.rowid,h.path,h.search || char(10) || coalesce(json_extract(p.payload,'$.Note'),'') || char(10) || coalesce(json_extract(p.payload,'$.State'),'') || char(10) || coalesce((SELECT group_concat(value,' ') FROM json_each(p.payload,'$.Tags')),'') FROM headers h LEFT JOIN identities i ON i.path=h.path LEFT JOIN personal p ON p.identity=i.identity WHERE h.path=$p", ("$p", path));
            }
            transaction.Commit();
        }
    }
    public void LinkCopies(string source, string destination) => LinkCopiesBatch([(source, destination)]);
    public void LinkCopiesBatch(IEnumerable<(string Source, string Destination)> copies)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var (source, destination) in copies)
            {
                var sourcePath = Full(source);
                Execute(db, "INSERT OR IGNORE INTO identities VALUES($p,$i)", ("$p", sourcePath), ("$i", Guid.NewGuid().ToString("N")));
                using var command = Command(db, "SELECT identity FROM identities WHERE path=$p", ("$p", sourcePath));
                var id = (string)command.ExecuteScalar()!;
                Execute(db, "INSERT INTO identities VALUES($p,$i) ON CONFLICT(path) DO UPDATE SET identity=excluded.identity", ("$p", Full(destination)), ("$i", id));
                ReindexIdentity(db, id);
            }
            tx.Commit();
            BackupPersonal(db);
        }
    }
    public IReadOnlyDictionary<string, MapperAnnotation> AllAnnotations()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = Command(db, "SELECT i.path,p.payload FROM identities i JOIN personal p ON p.identity=i.identity"); using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, MapperAnnotation>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) result[reader.GetString(0)] = JsonSerializer.Deserialize<MapperAnnotation>(reader.GetString(1))!;
            return result;
        }
    }
    /// <summary>AND terms, quoted phrases, -negative, tag:, state:, keys:, creator:, note:, star:true.</summary>
    public HashSet<string> Search(string query)
    {
        lock (gate)
        {
            using var db = Open(); var conditions = new List<string>(); var args = new List<(string, object?)>(); var positiveText = new List<string>();
            foreach (Match match in Regex.Matches(query, "(?:[^\\s\"]+|\"[^\"]*\")+"))
            {
                var term = match.Value; var negative = term.StartsWith('-'); if (negative) term = term[1..];
                var colon = term.IndexOf(':'); var field = colon > 0 ? term[..colon].ToLowerInvariant() : "";
                var value = (colon > 0 ? term[(colon + 1)..] : term).Trim('"').ToLowerInvariant(); if (value.Length == 0) continue;
                var parameter = "$v" + args.Count; string condition;
                if (field == "keys" && int.TryParse(value.TrimEnd('k'), out var keys)) { condition = "h.keys=" + parameter; args.Add((parameter, keys)); }
                else if (field == "star") { condition = "coalesce(json_extract(p.payload,'$.Favorite'),0)=" + parameter; args.Add((parameter, value is "true" or "1" or "yes" ? 1 : 0)); }
                // 0.1.38: substring semantics, matching the library UI's osu!-style tag= filter.
                else if (field == "tag") { condition = "EXISTS(SELECT 1 FROM json_each(p.payload,'$.Tags') WHERE instr(lower(value)," + parameter + ")>0)"; args.Add((parameter, value)); }
                else if (field is "state" or "note" or "creator")
                {
                    var expr = field == "state" ? "coalesce(json_extract(p.payload,'$.State'),'未分类')" : field == "note" ? "coalesce(json_extract(p.payload,'$.Note'),'')" : "coalesce(json_extract(h.payload,'$.Creator'),'')";
                    condition = "instr(lower(" + expr + ")," + parameter + ")>0"; args.Add((parameter, value));
                }
                else
                {
                    if (!negative && value.Length >= 3) { positiveText.Add("\"" + value.Replace("\"", "\"\"") + "\""); continue; }
                    // FTS trigrams accelerate long substrings; one/two-character CJK still work through instr.
                    condition = value.Length >= 3 ? "h.rowid IN (SELECT rowid FROM lookup WHERE lookup MATCH " + parameter + ")" : "h.rowid IN (SELECT rowid FROM lookup WHERE instr(lower(search)," + parameter + ")>0)";
                    args.Add((parameter, value.Length >= 3 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value));
                }
                conditions.Add(negative ? "NOT (" + condition + ")" : condition);
            }
            if (positiveText.Count > 0)
            {
                var parameter = "$v" + args.Count;
                conditions.Add("h.rowid IN (SELECT rowid FROM lookup WHERE lookup MATCH " + parameter + ")");
                args.Add((parameter, string.Join(" AND ", positiveText)));
            }
            using var command = Command(db, "SELECT h.path FROM headers h LEFT JOIN identities i ON i.path=h.path LEFT JOIN personal p ON p.identity=i.identity" +
                (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions)), args.ToArray());
            using var resultReader = command.ExecuteReader(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (resultReader.Read()) paths.Add(resultReader.GetString(0)); return paths;
        }
    }
}