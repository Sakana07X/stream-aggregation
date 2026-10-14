# 流式聚合

面向 Windows 的 QQ 群消息聚合与智能摘要客户端。它连接本机运行的 SnowLuma，接收指定 QQ 群消息，过滤闲聊，按时间窗口聚合内容，再通过可配置的大模型生成摘要和待办。

> 当前项目处于设计与开发准备阶段。

## 计划解决的问题

QQ群里的通知、作业、考试和活动信息容易被闲聊淹没。流式聚合计划把这些消息整理成更容易查看的摘要、行动项和截止时间。

## 计划中的首版功能

- 连接 SnowLuma 的 OneBot WebSocket。
- 只处理用户选择的 QQ 群。
- 消息去重和默认 3 分钟聚合。
- 本地规则过滤普通闲聊。
- 接入 OpenAI 兼容的大模型接口。
- 提取摘要、分类、行动项和截止时间。
- WinUI 3 Windows 原生界面。
- 本地 SQLite 保存消息处理结果。
- 托盘常驻和紧急事项 Windows 通知。

## 运行架构

```text
SnowLuma → OneBot WebSocket → 流式聚合 → SQLite
                                      ↓
                              OpenAI 兼容模型
                                      ↓
                              摘要与待办界面
```

## 数据与隐私

应用数据计划保存在 Windows 用户目录：

```text
%LOCALAPPDATA%\流式聚合\
```

首版使用本地 SQLite，不修改 SnowLuma 的数据库。SnowLuma 运行数据、QQ 消息、日志、token 和模型 API Key 不应提交到仓库。敏感配置计划使用 Windows DPAPI 加密保存。

## 当前状态

- 项目计划：已建立。
- Windows 客户端：尚未完成。
- 手机网页：暂缓。
- 图片和文件解析：暂缓。
- 外部通知渠道：暂缓。

## 文档

- [完整项目计划](PROJECT_PLAN.md)
- [项目介绍](PROJECT_INTRODUCTION.md)

## 许可

本项目采用 [MIT License](LICENSE)。
