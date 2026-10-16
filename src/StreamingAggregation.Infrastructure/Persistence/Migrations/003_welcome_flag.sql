-- =============================================================================
-- 003_welcome_flag.sql — 首启遮罩层「已看过」标记
--
-- 背景：
--   app_settings 是单行表，迁移只建表不插行。首启遮罩层需要两个判据：
--     1. HasStoredSettingsAsync —— 检测表里是否有行（用户是否保存过设置）；
--     2. welcome_dismissed —— 用户点「稍后再说」后写入的持久化标记，
--        保证二次启动不再自动弹出欢迎页。
--
-- 为什么不用 SaveAsync 的 UPSERT 维护这一列：
--   SaveAsync 的 ON CONFLICT DO UPDATE 只更新它显式列出的列，
--   未列出的列保持原值。因此 MarkWelcomeDismissedAsync 写入的标记
--   在用户后续保存设置时不会被覆盖。
--
-- 默认值 0：已有数据库行（升级前保存过设置）的用户不受影响，
-- 他们早已见过应用界面，不需要再看欢迎页。
-- =============================================================================

ALTER TABLE app_settings ADD COLUMN welcome_dismissed INTEGER NOT NULL DEFAULT 0;
