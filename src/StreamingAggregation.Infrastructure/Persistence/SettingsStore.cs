using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的设置存储（<c>app_settings</c> 单行表）。
/// </summary>
/// <remarks>
/// OneBot Token 与模型 API Key 属于敏感信息：注入了 <see cref="ISecretStore"/> 时，
/// 这两个值写入密钥库（DPAPI），<c>app_settings</c> 里只留空串占位；
/// 未注入时明文落库，仅适用于本地开发与测试。
/// </remarks>
public sealed class SettingsStore : ISettingsStore
{
    /// <summary>OneBot Token 在 <see cref="ISecretStore"/> 中的键名。</summary>
    public const string OneBotTokenSecretKey = "onebot.token";

    /// <summary>模型 API Key 在 <see cref="ISecretStore"/> 中的键名。</summary>
    public const string ModelApiKeySecretKey = "model.api_key";

    private const string SelectSql = """
        SELECT one_bot_host, one_bot_port, one_bot_token, one_bot_use_ssl,
               model_endpoint, model_api_key, model, model_timeout_seconds,
               aggregation_window_minutes, aggregation_retention_days, group_whitelist
        FROM app_settings
        WHERE id = 1;
        """;

    private const string UpsertSql = """
        INSERT INTO app_settings
            (id, one_bot_host, one_bot_port, one_bot_token, one_bot_use_ssl,
             model_endpoint, model_api_key, model, model_timeout_seconds,
             aggregation_window_minutes, aggregation_retention_days, group_whitelist, updated_at)
        VALUES
            (1, $one_bot_host, $one_bot_port, $one_bot_token, $one_bot_use_ssl,
             $model_endpoint, $model_api_key, $model, $model_timeout_seconds,
             $aggregation_window_minutes, $aggregation_retention_days, $group_whitelist, $updated_at)
        ON CONFLICT(id) DO UPDATE SET
            one_bot_host               = excluded.one_bot_host,
            one_bot_port               = excluded.one_bot_port,
            one_bot_token              = excluded.one_bot_token,
            one_bot_use_ssl            = excluded.one_bot_use_ssl,
            model_endpoint             = excluded.model_endpoint,
            model_api_key              = excluded.model_api_key,
            model                      = excluded.model,
            model_timeout_seconds      = excluded.model_timeout_seconds,
            aggregation_window_minutes = excluded.aggregation_window_minutes,
            aggregation_retention_days = excluded.aggregation_retention_days,
            group_whitelist            = excluded.group_whitelist,
            updated_at                 = excluded.updated_at;
        """;

    private const string SelectExistsSql = """
        SELECT EXISTS(SELECT 1 FROM app_settings);
        """;

    // 只更新 welcome_dismissed 列，其余列保持不变（包括已有行与新建行两种情况）。
    // updated_at 是 NOT NULL 且无默认值，新建行时必须显式提供。
    private const string MarkWelcomeDismissedSql = """
        INSERT INTO app_settings (id, welcome_dismissed, updated_at)
        VALUES (1, 1, $updated_at)
        ON CONFLICT(id) DO UPDATE SET welcome_dismissed = 1;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISecretStore? _secretStore;
    private readonly IClock _clock;

    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="secretStore">密钥库（DPAPI）；为 null 时敏感字段明文落库，仅限开发/测试。</param>
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟。</param>
    public SettingsStore(
        ISqliteConnectionFactory connectionFactory,
        ISecretStore? secretStore = null,
        IClock? clock = null)
    {
        Guard.NotNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _secretStore = secretStore;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>读取设置；数据库尚未初始化行时返回默认值。</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AppSettings();
        }

        var settings = new AppSettings
        {
            OneBotConnection = new OneBotConnectionSettings
            {
                Host = reader.GetRequiredString(0),
                Port = reader.GetInt32(1),
                Token = reader.GetRequiredString(2),
                UseSsl = reader.GetInt64(3) != 0,
            },
            Model = new ModelSettings
            {
                Endpoint = reader.GetRequiredString(4),
                ApiKey = reader.GetRequiredString(5),
                Model = reader.GetRequiredString(6),
                TimeoutSeconds = reader.GetInt32(7),
            },
            Aggregation = new AggregationSettings
            {
                WindowMinutes = reader.GetInt32(8),
                RetentionDays = reader.GetInt32(9),
            },
            GroupWhitelist = new GroupWhitelist { GroupIds = ReadWhitelist(reader) },
        };

        // 必须在读同一连接上的下一条语句之前释放 reader：SQLite 一条连接上同时只有一个
        // 活跃语句。提前 Dispose 与 await using 析构的时机等价，而 net48 只有同步 Dispose。
        reader.Dispose();

        ApplySecrets(settings);
        return settings;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(settings);
        Validate(settings);

        var token = settings.OneBotConnection.Token ?? string.Empty;
        var apiKey = settings.Model.ApiKey ?? string.Empty;

        // 先写密钥库再写数据库：密钥库是覆盖写，不会留下脏数据；
        // 反过来一旦数据库写失败，密钥库里的新值就成了孤儿。
        StoreSecrets(token, apiKey);

        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        command
            .Bind("$one_bot_host", settings.OneBotConnection.Host ?? string.Empty)
            .Bind("$one_bot_port", settings.OneBotConnection.Port)
            .Bind("$one_bot_token", _secretStore is null ? token : string.Empty)
            .Bind("$one_bot_use_ssl", settings.OneBotConnection.UseSsl ? 1 : 0)
            .Bind("$model_endpoint", settings.Model.Endpoint ?? string.Empty)
            .Bind("$model_api_key", _secretStore is null ? apiKey : string.Empty)
            .Bind("$model", settings.Model.Model ?? string.Empty)
            .Bind("$model_timeout_seconds", settings.Model.TimeoutSeconds)
            .Bind("$aggregation_window_minutes", settings.Aggregation.WindowMinutes)
            .Bind("$aggregation_retention_days", settings.Aggregation.RetentionDays)
            .Bind("$group_whitelist", SqliteCommandExtensions.ToJsonArray(settings.GroupWhitelist.GroupIds ?? []))
            .Bind("$updated_at", SqliteTime.ToStorage(_clock.Now));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasStoredSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SelectExistsSql;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <inheritdoc />
    public async Task MarkWelcomeDismissedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = MarkWelcomeDismissedSql;
        command.Bind("$updated_at", SqliteTime.ToStorage(_clock.Now));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StoreSecrets(string token, string apiKey)
    {
        if (_secretStore is null)
        {
            return;
        }

        _secretStore.SetSecret(OneBotTokenSecretKey, token);
        _secretStore.SetSecret(ModelApiKeySecretKey, apiKey);
    }

    private void ApplySecrets(AppSettings settings)
    {
        if (_secretStore is null)
        {
            return;
        }

        settings.OneBotConnection.Token = _secretStore.GetSecret(OneBotTokenSecretKey) ?? string.Empty;
        settings.Model.ApiKey = _secretStore.GetSecret(ModelApiKeySecretKey) ?? string.Empty;
    }

    private static IReadOnlyList<string> ReadWhitelist(SqliteDataReader reader) => reader.GetStringList(10);

    private static void Validate(AppSettings settings)
    {
        Guard.ThrowIfLessThan(settings.OneBotConnection.Port, 1, "OneBotConnection.Port");
        Guard.ThrowIfGreaterThan(settings.OneBotConnection.Port, 65535, "OneBotConnection.Port");
        Guard.ThrowIfLessThan(settings.Model.TimeoutSeconds, 1, "Model.TimeoutSeconds");
        Guard.ThrowIfLessThan(settings.Aggregation.WindowMinutes, 1, "Aggregation.WindowMinutes");
        Guard.ThrowIfLessThan(settings.Aggregation.RetentionDays, 1, "Aggregation.RetentionDays");
    }
}