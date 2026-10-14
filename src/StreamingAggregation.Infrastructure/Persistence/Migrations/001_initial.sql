-- =============================================================================
-- 001_initial.sql — 初始表结构
--
-- 约定：
--   * 所有时间列一律存 ISO-8601 UTC 文本（DateTime.ToString("O")），定宽且字典序
--     等价于时间序，可直接用 SQL 比较与排序。
--   * 所有布尔列存 INTEGER 0/1。
--   * 所有表使用 STRICT，列类型由 SQLite 强制，避免写入意外类型。
--
-- 不在本文件中出现 PRAGMA：
--   * journal_mode=WAL 与 foreign_keys=ON 由 SqliteConnectionFactory 在每次
--     建立连接时下发（连接串 Foreign Keys=True + 打开后执行 PRAGMA）。
--     这两个 PRAGMA 在事务内是空操作，放在迁移事务里不会生效。
--   * 版本表 schema_version 由 SchemaMigrator 在事务之外先建，
--     否则"记录版本"与"建表"无法保持原子性。
-- =============================================================================

-- 1) 聊天消息：原始落库数据，去重后的唯一消息。
CREATE TABLE IF NOT EXISTS messages
(
    message_id TEXT NOT NULL PRIMARY KEY,
    group_id   TEXT NOT NULL,
    sender     TEXT NOT NULL DEFAULT '',
    text       TEXT NOT NULL DEFAULT '',
    timestamp  TEXT NOT NULL
) STRICT;

-- GetByGroupAsync(groupId, since) 的主查询路径。
CREATE INDEX IF NOT EXISTS ix_messages_group_timestamp
    ON messages (group_id, timestamp);

-- 2) 聚合批次：一个时间窗口内某群组的待处理消息集合。
CREATE TABLE IF NOT EXISTS aggregation_batches
(
    id            TEXT    NOT NULL PRIMARY KEY,
    group_id      TEXT    NOT NULL,
    group_name    TEXT    NOT NULL DEFAULT '',
    window_start  TEXT    NOT NULL,
    window_end    TEXT    NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0,
    -- 批次内消息 ID 的 JSON 数组，避免与 messages 建多对多中间表。
    message_ids   TEXT    NOT NULL DEFAULT '[]',
    message_count INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    updated_at    TEXT    NOT NULL
) STRICT;

-- status 取值与 Core 的 BatchStatus 枚举顺序一致：Pending=0, Processing=1, Completed=2, Failed=3。
CREATE INDEX IF NOT EXISTS ix_batches_status_window_end
    ON aggregation_batches (status, window_end);

CREATE INDEX IF NOT EXISTS ix_batches_group_window_end
    ON aggregation_batches (group_id, window_end DESC);

-- 3) 摘要：对一个批次的归纳结果。批次删除时级联删除。
CREATE TABLE IF NOT EXISTS digests
(
    id         TEXT    NOT NULL PRIMARY KEY,
    batch_id   TEXT    NOT NULL,
    category   INTEGER NOT NULL DEFAULT 4,
    summary    TEXT    NOT NULL,
    reason     TEXT    NOT NULL DEFAULT '',
    created_at TEXT    NOT NULL,
    FOREIGN KEY (batch_id) REFERENCES aggregation_batches (id) ON DELETE CASCADE
) STRICT;

-- category 取值与 Core 的 DigestCategory 枚举顺序一致：Urgent=0, Todo=1, Study=2, Notice=3, Normal=4。
CREATE INDEX IF NOT EXISTS ix_digests_batch_created_at
    ON digests (batch_id, created_at);

-- 4) 行动项（待办）。用户可见数据，不参与保留期清理。
CREATE TABLE IF NOT EXISTS tasks
(
    id              TEXT    NOT NULL PRIMARY KEY,
    action          TEXT    NOT NULL,
    deadline        TEXT        NULL,
    importance      INTEGER NOT NULL DEFAULT 1,
    reason          TEXT    NOT NULL DEFAULT '',
    source_group_id TEXT    NOT NULL DEFAULT '',
    completed       INTEGER NOT NULL DEFAULT 0,
    ignored         INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
) STRICT;

-- importance 取值与 Core 的 Importance 枚举顺序一致：High=0, Medium=1, Low=2。
CREATE INDEX IF NOT EXISTS ix_tasks_status
    ON tasks (completed, ignored, importance);

CREATE INDEX IF NOT EXISTS ix_tasks_source_group_id
    ON tasks (source_group_id);

-- 5) 模型调用记录（诊断用）。batch_id 可空，批次删除后置空而不是连带删除。
CREATE TABLE IF NOT EXISTS llm_calls
(
    id               TEXT    NOT NULL PRIMARY KEY,
    batch_id         TEXT        NULL,
    model            TEXT    NOT NULL DEFAULT '',
    endpoint         TEXT    NOT NULL DEFAULT '',
    success          INTEGER NOT NULL DEFAULT 0,
    error_message    TEXT        NULL,
    prompt_chars     INTEGER NOT NULL DEFAULT 0,
    completion_chars INTEGER NOT NULL DEFAULT 0,
    duration_ms      INTEGER NOT NULL DEFAULT 0,
    created_at       TEXT    NOT NULL,
    FOREIGN KEY (batch_id) REFERENCES aggregation_batches (id) ON DELETE SET NULL
) STRICT;

CREATE INDEX IF NOT EXISTS ix_llm_calls_created_at
    ON llm_calls (created_at);

-- 6) 应用设置，单行表（CHECK (id = 1) 保证只有一行）。
-- API Key 与 OneBot Token 由 ISecretStore（DPAPI）保存，此表只存占位空串。
CREATE TABLE IF NOT EXISTS app_settings
(
    id                         INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    one_bot_host                TEXT    NOT NULL DEFAULT '127.0.0.1',
    one_bot_port                INTEGER NOT NULL DEFAULT 3001,
    one_bot_token               TEXT    NOT NULL DEFAULT '',
    one_bot_use_ssl             INTEGER NOT NULL DEFAULT 0,
    model_endpoint              TEXT    NOT NULL DEFAULT '',
    model_api_key               TEXT    NOT NULL DEFAULT '',
    model                       TEXT    NOT NULL DEFAULT '',
    model_timeout_seconds       INTEGER NOT NULL DEFAULT 30,
    aggregation_window_minutes  INTEGER NOT NULL DEFAULT 3,
    aggregation_retention_days  INTEGER NOT NULL DEFAULT 30,
    group_whitelist             TEXT    NOT NULL DEFAULT '[]',
    updated_at                  TEXT    NOT NULL
) STRICT;

-- 7) 去重状态：已处理过的 OneBot 事件。
CREATE TABLE IF NOT EXISTS processed_events
(
    id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    message_id   TEXT    NOT NULL,
    event_type   TEXT    NOT NULL DEFAULT '',
    outcome      TEXT    NOT NULL DEFAULT '',
    processed_at TEXT    NOT NULL
) STRICT;

-- 幂等去重的唯一依据：同一 message_id 只允许一行，重复写入走 UPSERT。
CREATE UNIQUE INDEX IF NOT EXISTS ux_processed_events_message_id
    ON processed_events (message_id);

CREATE INDEX IF NOT EXISTS ix_processed_events_processed_at
    ON processed_events (processed_at);