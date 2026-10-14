using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Infrastructure.Persistence;

namespace StreamingAggregation.Infrastructure.Security;

/// <summary>
/// 基于 Windows DPAPI 的安全存储实现。
/// 使用 DataProtectionScope.CurrentUser 范围加密，密文以 Base64 形式持久化到
/// <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么落盘</b>：密钥库是登录态凭据的落脚点。Token 与 API Key 只在设置页输入一次，
/// 若只留在内存里，用户每次重启都要重填一遍。密文落盘既解决了这个体验问题，
/// 又因为始终是 DPAPI 密文而不引入明文风险。
/// </para>
/// <para>
/// <b>为什么解密是惰性的</b>：构造函数只把密文读进内存，<see cref="GetSecret"/> 才真正解密。
/// DPAPI 的 CurrentUser 范围把密文绑死在"当前 Windows 用户 + 本机"上，
/// 文件被拷到别的机器或别的账户下时，密文本身完好，只有解密那一步会失败——
/// 惰性解密让这种"读不出来"精确地发生在取值的时刻，错误信息才说得出是哪个键出了问题。
/// </para>
/// <para>
/// <b>为什么落盘目录不做 ACL 加固</b>：%LOCALAPPDATA% 本身就是按用户隔离的目录，
/// 其他 Windows 用户本来就读不到里面的文件；而即使读到了，没有当前用户的 DPAPI
/// 主密钥也解不开密文。在这套组合下再加一层 ACL 只是多一处可能失败的写操作。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>默认密钥文件名（位于 %LOCALAPPDATA%\流式聚合 下）。</summary>
    public const string DefaultStoreFileName = "secrets.dat";

    /// <summary>
    /// 默认数据目录名。与 <see cref="SqliteConnectionFactory.DefaultDirectoryName"/> 同源——
    /// 密钥与数据库本就该待在同一个应用数据目录里，分成两处只会让备份和清理变麻烦。
    /// </summary>
    public const string DefaultDirectoryName = SqliteConnectionFactory.DefaultDirectoryName;

    /// <summary>密钥文件格式版本；格式变更时递增，旧文件按不兼容处理。</summary>
    private const int CurrentFormatVersion = 1;

    /// <summary>损坏文件的隔离后缀。</summary>
    private const string CorruptSuffix = ".corrupt";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly Lock _gate = new();
    private readonly string _storePath;
    private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

    /// <summary>
    /// 使用指定路径创建密钥库并立即把已有密文读入内存；
    /// 不传路径时使用 <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>。
    /// </summary>
    /// <param name="storePath">
    /// 密钥文件路径；为 <see langword="null"/> 或空白时使用默认路径。
    /// 测试请显式传入临时路径，不要依赖默认值。
    /// </param>
    public DpapiSecretStore(string? storePath = null)
    {
        _storePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(storePath) ? GetDefaultStorePath() : storePath);

        Load();
    }

    /// <summary>密钥文件的绝对路径。</summary>
    public string StorePath => _storePath;

    /// <summary>解析默认密钥路径：<c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>。</summary>
    public static string GetDefaultStorePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法解析 %LOCALAPPDATA%，无法确定默认密钥路径。");
        }

        return Path.Combine(localAppData, DefaultDirectoryName, DefaultStoreFileName);
    }

    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        string? encrypted;
        lock (_gate)
        {
            _store.TryGetValue(key, out encrypted);
        }

        if (encrypted is null)
        {
            return null;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(encrypted);
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                SecretProtectorOptions.FixedEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"无法解密密钥 '{key}'。数据可能已损坏，或是由其他用户/其他机器加密的。", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"密钥 '{key}' 的密文格式无效。", ex);
        }
    }

    /// <inheritdoc />
    public void SetSecret(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            SecretProtectorOptions.FixedEntropy,
            DataProtectionScope.CurrentUser);
        var encoded = Convert.ToBase64String(encryptedBytes);

        // 先改内存再落盘：落盘失败时内存与文件仍然一致，
        // 反过来先写文件再改内存，一旦写失败就会读到读不出来的密文。
        lock (_gate)
        {
            _store[key] = encoded;
            Persist();
        }
    }

    /// <inheritdoc />
    public void RemoveSecret(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_gate)
        {
            // 键本来就不存在时不必惊动文件——包括不存在的密钥库文件。
            if (!_store.Remove(key))
            {
                return;
            }

            Persist();
        }
    }

    /// <summary>
    /// 从磁盘载入密文。文件缺失视为空库；文件损坏则隔离后以空库继续，
    /// 绝不因为一个坏掉的凭据文件让整个应用起不来。
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_storePath))
        {
            return;
        }

        string payload;
        try
        {
            payload = File.ReadAllText(_storePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QuarantineCorruptFile();
            return;
        }

        SecretFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SecretFile>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            QuarantineCorruptFile();
            return;
        }

        // 版本不认识时同样按损坏处理：硬解一个未知格式比丢掉凭据更危险。
        if (file is null || file.Version != CurrentFormatVersion || file.Secrets is null)
        {
            QuarantineCorruptFile();
            return;
        }

        lock (_gate)
        {
            foreach (var (key, value) in file.Secrets)
            {
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    _store[key] = value;
                }
            }
        }
    }

    /// <summary>把当前内存中的全部密文原子写入磁盘。调用方须已持有 <see cref="_gate"/>。</summary>
    private void Persist()
    {
        var payload = JsonSerializer.Serialize(
            new SecretFile { Version = CurrentFormatVersion, Secrets = new Dictionary<string, string>(_store, StringComparer.Ordinal) },
            SerializerOptions);

        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再覆盖：直接覆写时进程中途被杀会留下半截文件，
        // 那等于把所有密钥一次性报废。
        var temporaryPath = _storePath + ".tmp";
        File.WriteAllText(temporaryPath, payload);
        File.Move(temporaryPath, _storePath, overwrite: true);
    }

    /// <summary>
    /// 把坏文件改名留档。不直接删除：用户可能还指望从里面找回东西，
    /// 而凭据文件的存在本身就是"这个应用在用过"的证据。
    /// </summary>
    private void QuarantineCorruptFile()
    {
        try
        {
            File.Move(_storePath, _storePath + CorruptSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 隔离失败也不能阻断启动；此时密钥库以空库运行，
            // 下一次写入会正常覆盖这个文件。
        }
    }

    /// <summary>密钥文件的磁盘格式。</summary>
    private sealed class SecretFile
    {
        /// <summary>格式版本。</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; }

        /// <summary>键 → Base64 密文。</summary>
        [JsonPropertyName("secrets")]
        public Dictionary<string, string>? Secrets { get; set; }
    }
}