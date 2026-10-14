# 流式聚合项目介绍

## 项目定位

流式聚合是一个运行在 Windows 上的 QQ 群消息整理工具。它不负责发送 QQ 消息，而是读取 SnowLuma 提供的 OneBot 消息，将重要信息从连续群聊中提取出来。

首版以 Windows 原生客户端为中心。手机网页和独立服务端会在 Windows 版本稳定后再考虑。

## 用户使用流程

1. 启动 SnowLuma 和流式聚合。
2. 在设置中填写或读取 SnowLuma 的 OneBot WebSocket 配置。
3. 选择需要处理的 QQ 群。
4. 配置 OpenAI 兼容模型的 endpoint、模型名和 API Key。
5. 客户端接收消息并按群进行短窗口聚合。
6. 用户在摘要页查看通知，在待办页处理行动项。

## 技术方案

- 桌面框架：C# + WinUI 3。
- 消息接入：SnowLuma OneBot WebSocket。
- 本地存储：SQLite。
- 模型协议：OpenAI 兼容的 chat completions 接口。
- 密钥保护：Windows DPAPI。
- 默认聚合窗口：3 分钟。

## 数据存储设计

应用独立使用自己的 SQLite 数据库，计划保存以下信息：

- 原始消息及其群、发送人、时间和消息 ID。
- 聚合批次及处理状态。
- 摘要、分类、重要性和原文关联。
- 待办、截止时间和完成状态。
- 模型调用耗时、成功状态和用量信息。
- 应用设置和去重记录。

默认保留最近 30 天数据。SnowLuma 的 messages.db 不作为本项目的业务数据库，也不会被写入。

## 设计边界

首版只做文字消息、指定群和本地 Windows 客户端。图片、文件、历史补采、手机网页、云端同步和外部推送不属于首版交付内容。

## 安全边界

项目不保存 QQ 登录凭证，不调用 QQ 发送接口，不把 SnowLuma 的真实消息数据库提交到 GitHub。模型 API Key 和 SnowLuma token 只保存在本机加密配置中。

## 开发阶段

1. 完成 Windows 客户端骨架和配置页面。
2. 接通 SnowLuma OneBot WebSocket。
3. 完成本地存储、过滤、去重和聚合。
4. 接入模型分析和失败回退。
5. 完成摘要、待办、托盘和通知。
6. 使用真实 SnowLuma 连接做只读联调。
7. 发布 Windows 版本。
