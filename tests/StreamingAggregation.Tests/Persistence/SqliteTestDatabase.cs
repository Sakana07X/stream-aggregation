using Microsoft.Data.Sqlite;
using StreamingAggregation.Infrastructure.Persistence;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Persistence;

/// <summary>
/// 每个测试实例独占一份的临时 SQLite 数据库：建库、跑迁移、析构时删除文件。
/// </summary>
/// <remarks>
/// 数据库文件放在仓库的 <c>.tmp/</c> 下而不是系统临时目录，与本机约定一致；
/// 文件名带 GUID，避免 xunit 并行执行时互相覆盖。
/// </remarks>
internal sealed class SqliteTestDatabase : IDisposable
{
    private const string TestDirectoryName = "test-db";

    private readonly string _databasePath;

    private SqliteTestDatabase(string databasePath)
    {
        _databasePath = databasePath;
        Connections = new SqliteConnectionFactory(databasePath);
    }

    /// <summary>指向该临时库的连接工厂（已带 WAL / 外键等 PRAGMA）。</summary>
    public SqliteConnectionFactory Connections { get; }

    /// <summary>建一个临时库，并把迁移跑到最新版本。</summary>
    public static async Task<SqliteTestDatabase> CreateAsync()
    {
        var database = new SqliteTestDatabase(BuildDatabasePath());
        await new SchemaMigrator(database.Connections).MigrateAsync();

        return database;
    }

    public BatchRepository CreateBatchRepository() => new(Connections);

    public DigestRepository CreateDigestRepository() => new(Connections);

    public MessageRepository CreateMessageRepository() => new(Connections);

    /// <summary>
    /// 用一条直连批量写入若干条摘要/消息。
    /// </summary>
    /// <remarks>
    /// 走仓储的 <c>SaveAsync</c> 每条都要开一次连接，几千条会慢到拖垮测试；
    /// 这里只在"需要把表撑到钳制上限"时使用，测的是查询而不是写入路径。
    /// </remarks>
    /// <param name="table">目标表名。</param>
    /// <param name="columns">列名（逗号分隔）。</param>
    /// <param name="values">每行的列值。</param>
    public async Task InsertRowsAsync(string table, string columns, IEnumerable<object[]> values)
    {
        var columnNames = columns.Split(',').Select(static column => column.Trim()).ToArray();
        var placeholders = string.Join(", ", columnNames.Select(static column => $"${column}"));

        await using var connection = await Connections.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var row in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} ({columns}) VALUES ({placeholders});";
            for (var index = 0; index < columnNames.Length; index++)
            {
                command.Parameters.AddWithValue($"${columnNames[index]}", row[index]);
            }

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public void Dispose()
    {
        // 连接池默认开启，池里还活着的连接会让 Windows 上的文件删除失败，先清空池。
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string BuildDatabasePath()
    {
        var root = RepositoryRootLocator.Find()
            ?? throw new InvalidOperationException("未能从测试输出目录向上找到仓库根目录，无法放置临时数据库。");

        var directory = Path.Combine(root, ".tmp", TestDirectoryName);
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, $"sa-test-{Guid.NewGuid():N}.db");
    }
}