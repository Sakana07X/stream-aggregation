using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>一条迁移脚本（嵌入资源中的 *.sql）。</summary>
/// <param name="Version">版本号，取自文件名前缀，如 <c>001_initial.sql</c> → 1。</param>
/// <param name="Name">文件名（不含扩展名）。</param>
/// <param name="Sql">脚本正文。</param>
public sealed record MigrationScript(int Version, string Name, string Sql);

/// <summary>数据库中已应用的一条迁移记录。</summary>
/// <param name="Version">版本号。</param>
/// <param name="Name">迁移名。</param>
/// <param name="AppliedAtUtc">应用时间（UTC）。</param>
public sealed record AppliedMigration(int Version, string Name, DateTime AppliedAtUtc);

/// <summary>
/// 基于版本表的 SQLite 迁移执行器。
/// </summary>
/// <remarks>
/// 迁移脚本以嵌入资源形式随程序集分发（<c>Persistence/Migrations/*.sql</c>）。
/// 每个脚本在自己的事务里执行，成功后写入 <c>schema_version</c>，
/// 保证"改结构"和"记版本"同时成功或同时失败。可重复执行（幂等）。
/// </remarks>
public sealed class SchemaMigrator
{
    private const string ResourceMarker = ".Persistence.Migrations.";
    private const string ResourceSuffix = ".sql";
    private const string VersionTableName = "schema_version";

    private const string CreateVersionTableSql = """
        CREATE TABLE IF NOT EXISTS schema_version
        (
            version    INTEGER NOT NULL PRIMARY KEY,
            name       TEXT    NOT NULL,
            applied_at TEXT    NOT NULL
        ) STRICT;
        """;

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly Func<IReadOnlyList<MigrationScript>> _scriptProvider;

    /// <summary>
    /// 使用默认（当前程序集内嵌的）迁移脚本。
    /// </summary>
    public SchemaMigrator(ISqliteConnectionFactory connectionFactory)
        : this(connectionFactory, () => DiscoverScripts(typeof(SchemaMigrator).Assembly))
    {
    }

    /// <summary>
    /// 使用自定义脚本来源，便于测试注入。
    /// </summary>
    public SchemaMigrator(
        ISqliteConnectionFactory connectionFactory,
        Func<IReadOnlyList<MigrationScript>> scriptProvider)
    {
        Guard.NotNull(connectionFactory);
        Guard.NotNull(scriptProvider);

        _connectionFactory = connectionFactory;
        _scriptProvider = scriptProvider;
    }

    /// <summary>
    /// 把数据库升级到最新版本，返回本次实际应用的迁移数量（已是最新时返回 0）。
    /// </summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var scripts = _scriptProvider().OrderBy(static script => script.Version).ToArray();
        if (scripts.Length == 0)
        {
            throw new InvalidOperationException("未发现任何迁移脚本，数据库结构无法初始化。");
        }

        AssertVersionsAreUnique(scripts);

        // 跨线程串行化：SQLite 层再串行化一次（BEGIN IMMEDIATE），避免多实例并发初始化。
        await MigrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyAsync(scripts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MigrationGate.Release();
        }
    }

    /// <summary>读取已应用的迁移记录，按版本升序。</summary>
    public async Task<IReadOnlyList<AppliedMigration>> GetAppliedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, applied_at FROM schema_version ORDER BY version;";

        var results = new List<AppliedMigration>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AppliedMigration(
                reader.GetInt32(0),
                reader.GetString(1),
                SqliteTime.FromStorage(reader.GetString(2))));
        }

        return results;
    }

    /// <summary>发现程序集内嵌的迁移脚本，按版本升序返回。</summary>
    public static IReadOnlyList<MigrationScript> DiscoverScripts(Assembly assembly)
    {
        Guard.NotNull(assembly);

        var scripts = new List<MigrationScript>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var markerIndex = resourceName.LastIndexOf(ResourceMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var tail = resourceName[(markerIndex + ResourceMarker.Length)..];
            if (!tail.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 资源名形如 <RootNamespace>.Persistence.Migrations.001_initial.sql
            var fileName = tail[..^ResourceSuffix.Length];

            if (!TryParseVersion(fileName, out var version))
            {
                throw new InvalidOperationException(
                    $"迁移资源 '{resourceName}' 命名不符合 '{ResourceMarker}<版本>_<名称>.sql' 约定。");
            }

            scripts.Add(new MigrationScript(version, fileName, ReadResourceText(assembly, resourceName)));
        }

        return scripts.OrderBy(static script => script.Version).ToArray();
    }

    private async Task<int> ApplyAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = 0;
        foreach (var script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsAppliedAsync(connection, script.Version, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await ApplyScriptAsync(connection, script, cancellationToken).ConfigureAwait(false);
            applied++;
        }

        return applied;
    }

    private static async Task ApplyScriptAsync(
        SqliteConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        // deferred: false → BEGIN IMMEDIATE，直接拿写锁，避免升级锁时的 SQLITE_BUSY。
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var scriptCommand = connection.CreateCommand())
        {
            scriptCommand.Transaction = transaction;
            scriptCommand.CommandText = script.Sql;
            await scriptCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var recordCommand = connection.CreateCommand())
        {
            recordCommand.Transaction = transaction;
            recordCommand.CommandText =
                "INSERT INTO schema_version (version, name, applied_at) VALUES ($version, $name, $applied_at);";
            recordCommand.Parameters.AddWithValue("$version", script.Version);
            recordCommand.Parameters.AddWithValue("$name", script.Name);
            recordCommand.Parameters.AddWithValue("$applied_at", SqliteTime.ToStorage(DateTime.UtcNow));
            await recordCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // net48 的 SqliteTransaction 没有 CommitAsync：提交本身是同步的。
        transaction.Commit();
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateVersionTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_version WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1;
    }

    private static void AssertVersionsAreUnique(IReadOnlyList<MigrationScript> scripts)
    {
        var duplicates = scripts
            .GroupBy(static script => script.Version)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"迁移版本号重复：{string.Join(", ", duplicates)}。");
        }
    }

    private static bool TryParseVersion(string fileName, out int version)
    {
        var separatorIndex = fileName.IndexOf('_');
        var prefix = separatorIndex > 0 ? fileName[..separatorIndex] : fileName;
        return int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static string ReadResourceText(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"嵌入资源 '{resourceName}' 不存在。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}