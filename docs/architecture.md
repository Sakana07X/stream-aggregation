# 架构说明

本文说明流式聚合的代码结构、运行时数据流和几处关键设计取舍。想了解"怎么装、怎么配"请看[使用说明](usage.md)和[配置说明](configuration.md)。

## 分层

项目分三个程序集，依赖方向单向向下，`Core` 不引用任何其他项目：

```text
StreamingAggregation.App           WinUI 3 界面、托盘、组合根
            │
            ▼
StreamingAggregation.Infrastructure  OneBot / SQLite / DPAPI / 模型客户端
            │
            ▼
StreamingAggregation.Core            接口、模型、聚合与过滤规则
```

- **Core** — 只定义接口和数据结构。仓储、事件源、密钥库这些能力在这里全是接口，不含任何 I/O。
- **Infrastructure** — 所有具体实现：SQLite 仓储、OneBot WebSocket 客户端、DPAPI 密钥库、OpenAI 兼容摘要客户端。
- **App** — WinUI 3 unpackaged 程序集，外加一个手写的极简服务注册表。

分层带来的直接好处是规则部分（聚合窗口、闲聊过滤、去重）可以脱离网络和数据库单测。`CoreInterfaceCatalog` 把 Core 对外声明的接口集中列出，启动时会逐个核对是否有实现登记，漏一个就直接启动失败——比等到第一条消息进来才空引用要早得多。

## 组合根

`AppCompositionRoot` 是唯一的组装点。应用里不允许出现 `new` 出来的仓储、客户端或过滤器；新增依赖在这里登记一次，其余代码只认接口。

登记用的是 `ServiceRegistry`，一个两百行的手写容器，没有引入第三方 DI：

- **单例**（`AddSingleton`）——仓储、密钥库、模型客户端、通知通道。这些与"当前设置"无关，整个进程只应有一份。
- **易失**（`AddTransient`）——聚合窗口、过滤器、事件源。它们在构造时就把白名单、窗口长度、连接地址固化了，设置一改就必须整体重建。

服务清单是分开的，因为两者的生命周期不同：设置保存后只有易失服务需要重建，仓储和密钥库继续复用。

### 启动顺序

连接先起、窗口后开：

1. 应用数据库迁移（`SchemaMigrator`）
2. 读取设置（`SettingsStore`，顺带从密钥库取回 Token 与 API Key）
3. 构造视图模型，探测 Windows 通知通道
4. 按保留天数清理过期数据
5. 拉起消息管线（后台任务）
6. 最后才创建并激活主窗口和托盘

反过来的话，用户会先看到一个空壳界面，误以为功能不可用；而连接失败时，托盘里的状态提示才有东西可显示。

### 退出顺序

与启动相反：先停托盘和窗口，再停管线，最后释放服务。管线还在收尾时会等它真正退出——直接释放它依赖的仓储和 `HttpClient`，会在它正读写的过程中把连接池拆掉。

## 消息管线

一条群消息从 WebSocket 到摘要界面，要走完整条链路：

```text
OneBot WebSocket
      │
      ▼
① 事件解析        OneBotEventParser → GroupMessageEvent
      │
      ▼
② 白名单匹配      不在名单里的群直接丢弃，不落库、不计入成本
      │
      ▼
③ 消息过滤        ChatterFilter（闲聊）+ DuplicateMessageFilter（1 分钟内重复）
      │
      ▼
④ 落库            MessageRepository 写入 messages 表
      │
      ▼
⑤ 聚合窗口        TimeWindowAggregator：攒够窗口时长或到达 200 条即成批
      │
      ▼
⑥ 模型摘要        OpenAiCompatibleClient → {Endpoint}/chat/completions
      │
      ▼
⑦ 校验与回退      DigestSchemaValidator 不过则 FallbackDigestBuilder 兜底
      │
      ▼
⑧ 通知与界面      紧急事项弹 Windows 通知，其余进摘要/待办页面
```

### 三个值得注意的点

**白名单在落库之前。** 不在名单里的消息连数据库都不进。这是隐私边界，不只是省流量。

**闲聊过滤用本地规则，不用模型。** `ChatterFilter` 只看长度和符号占比，两条规则几微秒就能跑完。含高信号关键词（`@所有人`、`紧急`、`P0`、`告警` 等）的消息直接豁免，不受过滤影响。把"要不要花钱调模型"这个决定交给本地规则，是为了让模型预算花在真有信息量的消息上。

**模型不可用时客户端仍能工作。** 摘要走 `DigestSchemaValidator` 校验结构，不合格就由 `FallbackDigestBuilder` 用本地规则生成一份保守摘要。模型超时、返回格式不对、JSON 解析失败——这些都不会让管线停摆，最多让摘要质量降级。失败原因记录在 `llm_calls` 表里，方便回头查。

## 聚合窗口

`TimeWindowAggregator` 按固定时长把消息攒成批。两个触发条件：

- 窗口时间到（默认 3 分钟）
- 消息数达到 200 条（`AggregationOptions.DefaultMaxMessagesPerWindow`）

第二个条件是为高频群准备的——否则一个刷屏群会在内存里堆出几千条消息，然后一次性把摘要接口打爆。到达上限会提前成批，**不丢消息**，后面的消息进入下一批。

## 连接与重连

`OneBotWebSocketClient` 只订阅、不发送任何 OneBot API 请求，也不写 SnowLuma 的数据。

断线由 `ReconnectPolicy` 处理：指数退避（初始 1 秒、倍数 2、上限 30 秒）叠加 ±20% 抖动。抖动的作用是避免 SnowLuma 重启后所有客户端在同一时刻涌上来；延迟由"连续失败次数"这个纯输入决定，类型本身无状态，因此同一个失败序号总是得到同一基准延迟，测试可以直接断言。

事件源每次重连都会重新从密钥库读 Token，所以轮换 token 后不需要重启应用——改设置保存即可。

## 数据存储

一个 SQLite 库放在 `%LOCALAPPDATA%\流式聚合\stream-aggregation.db`，开启 WAL 日志模式，外键约束打开。

迁移脚本以嵌入资源方式随程序集分发（`Persistence/Migrations/*.sql`），单文件发布不需要额外携带 `.sql`。`SchemaMigrator` 启动时按版本号顺序应用未执行的迁移，记录在 `schema_migrations` 表里。

写操作全是参数化绑定。时间列统一存定宽 ISO-8601 UTC 文本，字典序等价于时间序，可以直接用 SQL 的 `<` / `>` 比较，排障时也肉眼可读。

主要表：

| 表 | 用途 |
| --- | --- |
| `app_settings` | 设置单行表；Token 和 API Key 只留空串占位，真实值在密钥库 |
| `messages` | 通过过滤的原始消息 |
| `aggregation_batches` | 聚合批次 |
| `digests` | 摘要、分类、行动项、截止时间 |
| `processed_events` | 去重记录，保留天数同消息 |
| `llm_calls` | 模型调用审计（端点、耗时、成功与否） |
| `schema_migrations` | 已应用的迁移版本 |

`RetentionCleaner` 每次启动按保留天数（默认 30 天）清理过期批次、摘要、消息和去重记录。清理失败只记日志，不影响启动。

## 密钥存储

`DpapiSecretStore` 用 Windows DPAPI（`DataProtectionScope.CurrentUser`）加密 Token 和 API Key，密文以 Base64 存在 `%LOCALAPPDATA%\流式聚合\secrets.dat`。

几个设计细节：

- **密文落盘，明文不落盘。** `app_settings` 表里这两列只写空串占位。
- **解密是惰性的。** 构造函数只把密文读进内存，`GetSecret` 才真正解密。DPAPI 的 CurrentUser 范围把密文绑死在"当前 Windows 用户 + 本机"上，文件被拷到别处时密文本身完好，只有解密那一步会失败——错误信息因此能准确指出是哪个键出的问题。
- **写入是原子的。** 先写 `.tmp` 再覆盖，避免进程中途被杀留下半截文件——那等于把所有凭据一次性报废。
- **文件损坏不阻断启动。** 解析失败时把坏文件改名成 `secrets.dat.corrupt` 留档，密钥库以空库继续，用户重新填一次凭据即可。凭据文件不值得让整个应用起不来。

密钥和数据库放在同一个目录：它们同属应用数据，分成两处只会让备份和清理变麻烦。目录本身不做额外 ACL 加固——`%LOCALAPPDATA%` 已按用户隔离，且没有当前用户的 DPAPI 主密钥，即使文件被读到也解不开密文。

## 日志

目前统一走 `System.Diagnostics.Trace`，没有引入日志框架。日志里只写端点、耗时和失败原因，**不写消息正文，也不写 API Key**——`DescribeEndpoint` 会剥掉 userinfo、query 和 fragment，异常信息里的 Key 也会被 `Redact` 替换掉。

诊断信息通过构造函数注入的委托传递，各层不直接依赖 `Trace`，测试因此可以断言"发生了什么"而不必去接真实输出。

## 测试策略

`tests/StreamingAggregation.Tests` 用 xUnit。

- **规则测试**直接测 Core 的纯逻辑，不需要数据库或网络。
- **接口覆盖测试**（`CoreInterfaceCoverageTests`）用反射确认 Core 每个接口都有实现。新加接口忘了写实现时，这条测试会红——否则编译照样通过，缺的那行要到运行时才炸。
- **App 层实现**没法在测试宿主里加载（WinUI 栈不可靠），改成核对源码里确实声明了实现类型，同样能挡住回归，又不必把 WinUI 拖进测试。
- **落盘测试**（`DpapiSecretStoreTests`）用各自的临时目录，绝不碰用户真实的 `%LOCALAPPDATA%\流式聚合\secrets.dat`。

## 已知取舍

- **消息、摘要和待办页面目前是骨架。** 管线已经跑通，页面还没有接真实的读取查询。
- **不解析图片和文件。** OneBot 事件里的非文本段会被跳过，只处理纯文本消息。
- **只有 Windows 通知一个外发渠道。** 邮件、微信等渠道暂缓。
- **密钥库不支持跨用户迁移。** 这是 DPAPI CurrentUser 范围的固有限制，换 Windows 账户后需要重新填写凭据。