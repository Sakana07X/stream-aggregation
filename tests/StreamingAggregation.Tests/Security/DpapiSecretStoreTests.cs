using System.Runtime.Versioning;
using StreamingAggregation.Infrastructure.Security;

namespace StreamingAggregation.Tests.Security;

/// <summary>
/// <see cref="DpapiSecretStore"/> 的落盘行为测试。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试盯的是一个具体的回归：密钥库曾经只在内存里存字典，重启即丢，
/// 用户每次启动都要重新填 Token 与 API Key。修复后密文要落到
/// <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>，并在下次启动时读回来。
/// </para>
/// <para>
/// 每个用例都指向自己的临时目录——测试绝不能碰到用户真实的
/// <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>，否则一次运行就能把真实凭据冲掉。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests : IDisposable
{
    private const string SecretFileName = "secrets.dat";

    private readonly string _directory;
    private readonly string _storePath;

    /// <summary>为当前用例准备一个独立的临时目录。</summary>
    public DpapiSecretStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sa-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _storePath = Path.Combine(_directory, SecretFileName);
    }

    /// <summary>清理临时目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不应让测试变红；临时目录由系统回收。
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>写入后能读回同一个值。</summary>
    [Fact]
    public void SetThenGetReturnsTheStoredValue()
    {
        var store = new DpapiSecretStore(_storePath);

        store.SetSecret("onebot.token", "token-value");

        Assert.Equal("token-value", store.GetSecret("onebot.token"));
    }

    /// <summary>核心回归：换一个实例（等价于重启进程）仍能读回密钥。</summary>
    [Fact]
    public void SecretsSurviveAcrossStoreInstances()
    {
        new DpapiSecretStore(_storePath).SetSecret("model.api_key", "sk-persisted");

        // 第二个实例完全独立，只靠文件里的密文恢复。
        var reopened = new DpapiSecretStore(_storePath);

        Assert.Equal("sk-persisted", reopened.GetSecret("model.api_key"));
    }

    /// <summary>多个密钥并存，互不覆盖。</summary>
    [Fact]
    public void MultipleSecretsCoexistInTheSameFile()
    {
        var writer = new DpapiSecretStore(_storePath);
        writer.SetSecret("onebot.token", "t1");
        writer.SetSecret("model.api_key", "k1");

        var reopened = new DpapiSecretStore(_storePath);

        Assert.Equal("t1", reopened.GetSecret("onebot.token"));
        Assert.Equal("k1", reopened.GetSecret("model.api_key"));
    }

    /// <summary>同一个键重复写入只保留最后一次的值。</summary>
    [Fact]
    public void OverwritingTheSameKeyReplacesThePreviousValue()
    {
        var store = new DpapiSecretStore(_storePath);
        store.SetSecret("onebot.token", "old");
        store.SetSecret("onebot.token", "new");

        Assert.Equal("new", new DpapiSecretStore(_storePath).GetSecret("onebot.token"));
    }

    /// <summary>落盘文件里不能出现明文。</summary>
    [Fact]
    public void PersistedFileDoesNotContainPlaintext()
    {
        const string Plaintext = "super-secret-token-value";

        new DpapiSecretStore(_storePath).SetSecret("onebot.token", Plaintext);

        Assert.True(File.Exists(_storePath), $"密钥文件未创建：{_storePath}");
        var raw = File.ReadAllText(_storePath);
        Assert.DoesNotContain(Plaintext, raw, StringComparison.Ordinal);
    }

    /// <summary>删除密钥后重开实例也不该再读得到。</summary>
    [Fact]
    public void RemovedSecretIsGoneFromDiskAndMemory()
    {
        var store = new DpapiSecretStore(_storePath);
        store.SetSecret("onebot.token", "t1");
        store.SetSecret("model.api_key", "k1");

        store.RemoveSecret("onebot.token");

        var reopened = new DpapiSecretStore(_storePath);
        Assert.Null(reopened.GetSecret("onebot.token"));
        Assert.Equal("k1", reopened.GetSecret("model.api_key"));
    }

    /// <summary>删除不存在的键不应抛异常。</summary>
    [Fact]
    public void RemovingAnUnknownKeyIsANoOp()
    {
        var store = new DpapiSecretStore(_storePath);

        store.RemoveSecret("never.stored");

        Assert.Null(store.GetSecret("never.stored"));
    }

    /// <summary>文件不存在时视为空库，不报错也不主动建空文件。</summary>
    [Fact]
    public void MissingFileYieldsAnEmptyStoreWithoutCreatingIt()
    {
        var store = new DpapiSecretStore(_storePath);

        Assert.Null(store.GetSecret("onebot.token"));
        Assert.False(File.Exists(_storePath), "读取不应凭空创建空的密钥文件。");
    }

    /// <summary>读不出来的键返回 null，而不是抛异常。</summary>
    [Fact]
    public void UnknownKeyReturnsNull()
    {
        var store = new DpapiSecretStore(_storePath);

        Assert.Null(store.GetSecret("not.there"));
    }

    /// <summary>文件损坏时不能让启动崩掉：应当隔离坏文件并以空库继续。</summary>
    [Fact]
    public void CorruptedFileIsQuarantinedAndStoreStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_storePath, "这不是合法的 JSON {{{");

        var store = new DpapiSecretStore(_storePath);

        Assert.Null(store.GetSecret("onebot.token"));
        Assert.True(
            File.Exists(_storePath + ".corrupt"),
            $"损坏文件应被隔离为 {_storePath}.corrupt。");
    }

    /// <summary>目录不存在时要能自动创建，否则首次保存就会失败。</summary>
    [Fact]
    public void MissingDirectoryIsCreatedOnDemand()
    {
        var nested = Path.Combine(_directory, "a", "b", SecretFileName);
        Directory.Delete(_directory, recursive: true);

        var store = new DpapiSecretStore(nested);
        store.SetSecret("onebot.token", "t1");

        Assert.Equal("t1", new DpapiSecretStore(nested).GetSecret("onebot.token"));
    }

    /// <summary>空值也要能存：清空 Token 时会写入空串。</summary>
    [Fact]
    public void EmptyValueRoundTripsAsEmptyStringNotNull()
    {
        var store = new DpapiSecretStore(_storePath);

        store.SetSecret("onebot.token", string.Empty);

        var reopened = new DpapiSecretStore(_storePath);
        Assert.NotNull(reopened.GetSecret("onebot.token"));
        Assert.Equal(string.Empty, reopened.GetSecret("onebot.token"));
    }

    /// <summary>默认路径就是 <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>。</summary>
    [Fact]
    public void DefaultPathLivesUnderLocalAppData()
    {
        var path = DpapiSecretStore.GetDefaultStorePath();

        Assert.Equal(SecretFileName, Path.GetFileName(path));
        Assert.Equal("流式聚合", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.True(IsFullyQualifiedPath(path), $"默认路径应为绝对路径，实际为 {path}。");
    }

    /// <summary>
    /// net48 没有 <c>Path.IsPathFullyQualified</c>（netcore2.1+），这里补一个等价实现。
    /// </summary>
    /// <remarks>
    /// 两者的差别只在「已定根」这一档：<c>C:\a</c> 两者都真，但 <c>C:a</c>（驱动器相对路径）
    /// <c>IsPathRooted</c> 会给 true、<c>IsPathFullyQualified</c> 给 false，
    /// 所以这里在 <c>IsPathRooted</c> 之上把驱动器相对路径单独排除掉。
    /// </remarks>
    private static bool IsFullyQualifiedPath(string path) =>
        Path.IsPathRooted(path) && !IsDriveRelative(path);

    /// <summary>形如 <c>C:</c> 或 <c>C:a</c>：有盘符但没有根分隔符。</summary>
    private static bool IsDriveRelative(string path) =>
        path.Length >= 2
        && char.IsLetter(path[0])
        && path[1] == ':'
        && (path.Length == 2 || (path[2] != '\\' && path[2] != '/'));
}