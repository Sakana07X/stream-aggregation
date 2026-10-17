using Microsoft.Data.Sqlite;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 创建已应用本项目连接参数的 SQLite 连接。
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>数据库文件绝对路径。</summary>
    string DatabasePath { get; }

    /// <summary>创建一个未打开的连接（连接串已带 Foreign Keys 等参数）。</summary>
    SqliteConnection CreateConnection();

    /// <summary>创建连接、打开并下发 PRAGMA（WAL / foreign_keys 等）。</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认连接工厂：数据库位于 <c>%LOCALAPPDATA%\流式聚合\stream-aggregation.db</c>。
/// </summary>
/// <remarks>
/// 每次调用 <see cref="OpenAsync"/> 都返回一个新连接。SQLite 连接本身不支持并发复用，
/// 由本工厂统一负责连接的创建与 PRAGMA 下发，仓储层因此可以保持无状态、线程安全。
/// 测试请把临时文件路径传给构造函数：
/// <code>
/// var path = Path.Combine(Path.GetTempPath(), "sa-" + Guid.NewGuid().ToString("N") + ".db");
/// var factory = new SqliteConnectionFactory(path);
/// </code>
/// </remarks>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    /// <summary>默认数据库文件名。</summary>
    public const string DefaultDatabaseFileName = "stream-aggregation.db";

    /// <summary>默认数据目录名（位于 %LOCALAPPDATA% 下）。</summary>
    public const string DefaultDirectoryName = "流式聚合";

    private const int BusyTimeoutMilliseconds = 30_000;

    private static readonly Lock InitializationLock = new();
    private static bool _batteriesInitialized;

    private readonly string _connectionString;

    /// <summary>
    /// 使用指定数据库路径创建工厂；不传路径时使用
    /// <c>%LOCALAPPDATA%\流式聚合\stream-aggregation.db</c>。
    /// </summary>
    public SqliteConnectionFactory(string? databasePath = null)
    {
        EnsureBatteriesInitialized();

        DatabasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath) ? GetDefaultDatabasePath() : databasePath);

        EnsureDirectoryExists(DatabasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <summary>解析默认数据库路径：<c>%LOCALAPPDATA%\流式聚合\stream-aggregation.db</c>。</summary>
    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法解析 %LOCALAPPDATA%，无法确定默认数据库路径。");
        }

        return Path.Combine(localAppData, DefaultDirectoryName, DefaultDatabaseFileName);
    }

    /// <inheritdoc />
    public SqliteConnection CreateConnection() => new(_connectionString);

    /// <inheritdoc />
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // net48 的 SqliteConnection 没有 DisposeAsync：Dispose() 关闭连接并把它
            // 归还连接池（连接串里 Pooling = true），同步释放在这里完全等价。
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 下发连接级 PRAGMA。journal_mode 是库级设置，每次打开都显式确认一次，
    /// 避免误回退到 DELETE 模式导致读写互相阻塞。
    /// </summary>
    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var journalMode = await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(journalMode?.Trim(), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"无法启用 WAL 日志模式，当前模式为 '{journalMode}'。请确认数据库文件所在目录可写。");
        }

        await ExecutePragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};", cancellationToken)
            .ConfigureAwait(false);

        // 连接串已带 Foreign Keys=True，这里再确认一次，双保险。
        var foreignKeys = await ExecutePragmaAsync(connection, "PRAGMA foreign_keys;", cancellationToken)
            .ConfigureAwait(false);

        if (foreignKeys?.Trim() != "1")
        {
            throw new InvalidOperationException("无法启用外键约束（PRAGMA foreign_keys=ON）。");
        }
    }

    private static async Task<string?> ExecutePragmaAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? value?.ToString();
    }

    private static void EnsureDirectoryExists(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void EnsureBatteriesInitialized()
    {
        if (_batteriesInitialized)
        {
            return;
        }

        lock (InitializationLock)
        {
            if (_batteriesInitialized)
            {
                return;
            }

            // 加载 e_sqlite3 原生库；必须在建立任何连接之前完成。
            SQLitePCL.Batteries_V2.Init();
            _batteriesInitialized = true;
        }
    }
}