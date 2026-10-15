namespace StreamingAggregation.Tests.Fixtures;

/// <summary>
/// 从测试输出目录向上查找仓库根目录。
/// </summary>
internal static class RepositoryRootLocator
{
    /// <summary>仓库根目录的标志文件。</summary>
    private const string SolutionFileName = "StreamingAggregation.slnx";

    /// <summary>
    /// 从测试输出目录向上查找仓库根目录。
    /// </summary>
    /// <returns>仓库根目录；找不到时返回 <see langword="null"/>。</returns>
    public static string? Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
