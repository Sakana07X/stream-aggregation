# 流式聚合项目计划

## 目标

构建一个 C# + WinUI 3 Windows 原生客户端，连接同机运行的 SnowLuma，通过 OneBot WebSocket 接收指定 QQ 群消息，使用可配置的 OpenAI 兼容模型生成摘要和待办，并对紧急事项发送 Windows 通知。

首版只交付 Windows 客户端。手机网页、独立服务端、图片 OCR、文件解析和外部推送放到后续版本。

## 首版功能

- 连接 SnowLuma 的 OneBot WebSocket。
- 指定群白名单、消息去重和默认 3 分钟聚合窗口。
- 本地规则过滤闲聊。
- 调用 OpenAI 兼容接口提取摘要、分类、行动项和截止时间。
- 模型失败时回退本地规则摘要。
- WinUI 3 摘要页、待办页、消息页和设置页。
- 托盘常驻、连接状态和紧急 Windows 通知。

## 数据存储

使用本地 SQLite，不修改 SnowLuma 自己的数据库。应用数据放在 `%LOCALAPPDATA%\流式聚合\`。

数据库保存消息、聚合批次、摘要、待办、模型调用记录、设置和去重状态。默认保留 30 天消息，可在设置中调整。

SnowLuma 的 data、messages.db、日志和 token 只在本机使用，不提交到仓库。API Key 和 SnowLuma token 使用 Windows DPAPI 加密保存。

## 项目结构

- `src/StreamingAggregation.App`：WinUI 3 界面、托盘和通知。
- `src/StreamingAggregation.Core`：消息模型、聚合、规则和接口。
- `src/StreamingAggregation.Infrastructure`：OneBot、SQLite、模型和安全存储。
- `tests/StreamingAggregation.Tests`：核心逻辑和集成测试。

## 验收条件

- 能连接当前运行的 SnowLuma。
- 只有白名单群消息进入分析。
- 消息可以聚合、去重并显示摘要和待办。
- 模型不可用时客户端仍能工作。
- 客户端重启和 WebSocket 断线后可以恢复。
- Release win-x64 发布包可以启动。
- 自动化测试覆盖消息解析、过滤、聚合、模型回退、SQLite 恢复和敏感信息脱敏。

## GitHub

公开仓库：`Sakana07X/stream-aggregation`。提交中不得包含真实消息、数据库、日志、token、API Key、本机临时文件和 SnowLuma 运行数据。

## 默认选择

C# + WinUI 3；本机 OneBot WebSocket；本地 SQLite；指定群白名单；3 分钟聚合；OpenAI 兼容模型；仅紧急通知；手机端后续实现。
