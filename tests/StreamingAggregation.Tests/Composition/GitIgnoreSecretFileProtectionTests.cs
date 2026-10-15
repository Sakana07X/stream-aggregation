using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 真实 git 行为测试：密钥保护规则必须挡住 DPAPI 密钥库及其伴生文件。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能用源码正则断言</b>：<c>.gitignore</c> 的匹配语义由 git 自己实现
/// （锚定、目录性、negation、通配符优先级），正则模拟必然与真实行为漂移。这里在临时目录
/// <c>git init</c>、写入密钥保护规则、造出密钥文件，跑真实的 <c>git add -A</c>，
/// 再读 <c>git ls-files</c>——断言的是 git 的真实决定，不是我们的猜测。
/// </para>
/// <para>
/// <b>守护的回归</b>：<see cref="StreamingAggregation.Infrastructure.Security.DpapiSecretStore"/>
/// 把密文写到 <c>%LOCALAPPDATA%\流式聚合\secrets.dat</c>，写坏时还会隔离出
/// <c>secrets.dat.corrupt</c>。一旦有人误把密钥库配置改成落在仓库内
/// （或把 <c>secrets.dat.*</c> 这类配套模式删掉），
/// 本机凭据副本就会跟着 <c>git add -A</c> 进暂存区，随下一次 push 公开。
/// </para>
/// <para>
/// <b>为什么必须自包含</b>：测试绝不在真实仓库里跑 <c>git add</c>，那会污染用户的暂存区。
/// 每个用例都在 <see cref="Path.GetTempPath"/> 下自建独立仓库，并屏蔽全局 git 配置
/// （<c>GIT_CONFIG_GLOBAL</c>），保证结果只由测试写入的规则决定。
/// </para>
/// </remarks>
public sealed class GitIgnoreSecretFileProtectionTests : IDisposable
{
    /// <summary>临时测试仓库的忽略文件名。</summary>
    private const string GitIgnoreFileName = ".gitignore";
    /// <summary>应用密钥库及其损坏备份必须被忽略。</summary>
    private const string SecretIgnoreRules = "secrets.dat\nsecrets.dat.*\n";

    /// <summary>不能被 git 跟踪的密钥文件。</summary>
    public static TheoryData<string> SecretFileNames =>
    [
        "secrets.dat",
        "secrets.dat.tmp",
        "secrets.dat.corrupt",
    ];

    /// <summary>对照文件：证明 <c>git add -A</c> 本身是通的，不是"什么都被忽略"。</summary>
    private const string ControlFileName = "keep-me.txt";

    /// <summary>单条 git 命令的等待上限：超时就当失败，不让测试挂死。</summary>
    private const int TimeoutMilliseconds = 60_000;

    private readonly string _repositoryDirectory;
    private readonly HashSet<string> _trackedFiles;

    /// <summary>在临时目录里建一个真实 git 仓库，并完成 <c>init</c> + <c>add -A</c>。</summary>
    public GitIgnoreSecretFileProtectionTests()
    {
        _repositoryDirectory = Path.Combine(Path.GetTempPath(), "sa-gitignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryDirectory);

        CopyRepositoryGitIgnore();
        WriteSecretFiles();
        File.WriteAllText(Path.Combine(_repositoryDirectory, ControlFileName), "test-control-file");

        RunGit(args: ["init", "-b", "main", "--quiet"]);
        RunGit(args: ["add", "-A"]);
        _trackedFiles = ReadTrackedFiles();
    }

    /// <summary>清理临时 git 仓库。</summary>
    public void Dispose()
    {
        try
        {
            DeleteRepositoryDirectory();
        }
        catch (IOException)
        {
            // 清理失败不应让测试变红；临时目录由系统回收。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：残留目录比一条红色断言更不值得在意。
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>递归删除临时仓库；先摘掉 git 打在松散对象上的只读位。</summary>
    /// <remarks>
    /// <c>git add</c> 写出的 <c>.git/objects/xx/yyyy</c> 松散对象带 <see cref="FileAttributes.ReadOnly"/>，
    /// 而 <see cref="Directory.Delete(string, bool)"/> 不会顺手清只读属性，直接删会抛
    /// <see cref="UnauthorizedAccessException"/>——git 仓库比普通临时目录多这一层坑。
    /// </remarks>
    private void DeleteRepositoryDirectory()
    {
        if (!Directory.Exists(_repositoryDirectory))
        {
            return;
        }

        ClearReadOnlyAttributes(new DirectoryInfo(_repositoryDirectory));
        Directory.Delete(_repositoryDirectory, recursive: true);
    }

    /// <summary>递归清掉目录树里所有文件的只读位。</summary>
    /// <param name="directory">待清理的目录。</param>
    private static void ClearReadOnlyAttributes(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        foreach (var subDirectory in directory.EnumerateDirectories())
        {
            ClearReadOnlyAttributes(subDirectory);
        }
    }

    /// <summary>密钥文件与它的伴生文件都不能进暂存区。</summary>
    /// <param name="fileName">密钥文件名。</param>
    [Theory]
    [MemberData(nameof(SecretFileNames))]
    public void SecretFilesAreNotStagedByGitAdd(string fileName)
    {
        Assert.True(
            File.Exists(Path.Combine(_repositoryDirectory, fileName)),
            $"用例准备失败：{fileName} 没有被写进临时仓库，断言无从谈起。");

        Assert.DoesNotContain(fileName, _trackedFiles);
    }

    /// <summary>对照组：普通文件照常入库，确保上面的断言不是"全被忽略"造成的假绿。</summary>
    [Fact]
    public void NonSecretFileIsStillStagedSoTheIgnoreAssertionIsMeaningful()
    {
        Assert.Contains(ControlFileName, _trackedFiles);
    }

    /// <summary>忽略规则已写入临时仓库，否则上面两条断言都失去意义。</summary>
    [Fact]
    public void SecretIgnorePolicyIsPresentInTheTemporaryRepository()
    {
        Assert.Contains(GitIgnoreFileName, _trackedFiles);
    }

    /// <summary>把密钥保护规则写进临时仓库。</summary>
    private void CopyRepositoryGitIgnore()
    {
        File.WriteAllText(Path.Combine(_repositoryDirectory, GitIgnoreFileName), SecretIgnoreRules);
    }

    /// <summary>造出 DPAPI 密钥库会产生的三类文件。</summary>
    /// <remarks>
    /// 内容全是显式假值（夹具约定要求 <c>test-</c> 前缀），文件永远写在临时仓库里。
    /// </remarks>
    private void WriteSecretFiles()
    {
        foreach (var fileName in SecretFileNames)
        {
            File.WriteAllText(Path.Combine(_repositoryDirectory, fileName), $"test-fake-secret-{fileName}");
        }
    }

    /// <summary>在临时仓库里跑一条 git 命令并返回其标准输出。</summary>
    /// <param name="args">git 参数（已按参数列表传入，不做手工拼接）。</param>
    /// <returns>标准输出文本。</returns>
    private string RunGit(string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repositoryDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // 隔离全局 git 配置与交互提示：结果只由测试仓库的 .gitignore 决定，也不会卡在凭据输入上。
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_repositoryDirectory, "no-such-global-gitconfig");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "无法启动 git，请确认 git.exe 在 PATH 上（本测试需要真实 git 行为验证）。", ex);
        }

        // 两条管道必须同时开读：先同步读完 stdout 再读 stderr 的话，
        // git 若先把 stderr 写满就会互相卡死。用异步读把两个泵一起跑起来。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        Assert.True(
            process.WaitForExit(TimeoutMilliseconds),
            $"git {string.Join(' ', args)} 在 {_repositoryDirectory} 超时未退出。");

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', args)} 在临时仓库退出码 {process.ExitCode}。stderr：{stderr}");

        return stdout;
    }

    /// <summary>读回 <c>git ls-files</c>，即当前索引里真正被跟踪的文件。</summary>
    /// <returns>仓库相对路径集合（git 一律用正斜杠）。</returns>
    private HashSet<string> ReadTrackedFiles()
    {
        var output = RunGit(args: ["ls-files"]);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
