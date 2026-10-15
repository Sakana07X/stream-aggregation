-- 2) 界面列表查询所需的索引。
--
-- 001_initial.sql 里的摘要索引是 (batch_id, created_at)，只在「按批次查」时可用；
-- 摘要页的「最近摘要」与「按分类列出」都是不带 batch_id 前缀的查询，会退化成全表扫描 + 排序。
-- 同理，消息索引 (group_id, timestamp) 无法服务不带 group_id 的「最近消息」查询。
--
-- 这两个索引是前缀相反的：把排序列放在最左，WHERE 上的等值列跟在后面。

-- 摘要页：最近摘要 / 按分类列出。
-- WHERE category = ? ORDER BY created_at DESC, id DESC
CREATE INDEX IF NOT EXISTS ix_digests_category_created_at
    ON digests (category, created_at DESC, id DESC);

-- 摘要页：最近摘要（不按分类过滤）。
-- ORDER BY created_at DESC, id DESC
CREATE INDEX IF NOT EXISTS ix_digests_created_at
    ON digests (created_at DESC, id DESC);

-- 消息页：最近消息。
-- ORDER BY timestamp DESC, message_id DESC
CREATE INDEX IF NOT EXISTS ix_messages_timestamp
    ON messages (timestamp DESC, message_id DESC);