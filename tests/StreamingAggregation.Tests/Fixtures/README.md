# 测试夹具与假数据

本目录包含测试基座和共享夹具的说明文档。

## 原则

**所有测试数据必须使用明显的假值，严禁使用真实数据。**

| 禁止 | 替代 |
|------|------|
| 真实 token | `test-token-xxx` |
| 真实 API Key | `test-api-key-xxx` |
| 真实群组 ID | `test-group-id` |
| 真实用户 ID | `test-user-id` |
| 真实消息内容 | `This is a test message.` |
| 真实数据库路径 | `test.db` 或内存数据库 |
| 真实文件路径 | `test-file.txt` |

## 测试工具

### FakeClock

可控时间源，实现 `IClock` 接口。支持：
- `SetTime(DateTime)` — 设置当前时间
- `Advance(TimeSpan)` — 推进时间

```csharp
var clock = new FakeClock(new DateTime(2024, 1, 1));
clock.Advance(TimeSpan.FromMinutes(5));
```

### MessageBuilder

`Message` 对象的链式构建器。

```csharp
var message = new MessageBuilder()
    .WithGroupId("test-group-2")
    .WithSender("Test User 2")
    .WithText("Custom test text")
    .Build();
```

### OneBotEventBuilder

`OneBotEvent` 对象的链式构建器。

```csharp
var evt = new OneBotEventBuilder()
    .WithGroupId("test-group-3")
    .WithUserId("test-user-3")
    .WithMessage("Hello from test")
    .Build();
```

## 命名约定

- 假值统一使用 `test-` 前缀
- 构建器默认值均为假数据
- 测试类命名：`<被测类>Tests`
- 测试方法命名：`<场景>_<预期行为>`
