using StreamingAggregation.Core;
using StreamingAggregation.Infrastructure.Llm;

namespace StreamingAggregation.Tests.Llm;

/// <summary>
/// 提示词与 <see cref="DigestSchemaValidator"/> 的一致性测试。
/// </summary>
/// <remarks>
/// 这组测试存在的理由：提示词只写三字段、校验器要六字段，两边各写各的，
/// 结果是切到新映射器后每一条模型输出都校验失败。因此把「提示词必须覆盖六字段契约」
/// 变成可执行断言，而不是靠人肉同步。
/// </remarks>
public sealed class SystemPromptContractTests
{
    /// <summary>校验器要求的六个字段名。</summary>
    private static readonly string[] RequiredFields =
        ["category", "summary", "action", "deadline", "importance", "reason"];

    /// <summary>校验器接受的全部 <see cref="DigestCategory"/> 取值。</summary>
    public static TheoryData<string> CategoryValuesData()
    {
        var data = new TheoryData<string>();
        foreach (var value in Enum.GetNames<DigestCategory>())
        {
            data.Add(value);
        }

        return data;
    }

    /// <summary>校验器接受的全部 <see cref="Importance"/> 取值。</summary>
    public static TheoryData<string> ImportanceValuesData()
    {
        var data = new TheoryData<string>();
        foreach (var value in Enum.GetNames<Importance>())
        {
            data.Add(value);
        }

        return data;
    }

    /// <summary>六个字段名必须逐个出现在提示词里，缺一个就意味着模型可能不输出它。</summary>
    [Theory]
    [MemberData(nameof(RequiredFieldsData))]
    public void Prompt_MentionsEveryRequiredField(string field)
    {
        Assert.Contains(field, LlmRequestBuilder.DefaultSystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>分类的合法枚举值必须写进提示词，否则模型只能靠猜。</summary>
    [Theory]
    [MemberData(nameof(CategoryValuesData))]
    public void Prompt_MentionsEveryCategoryValue(string value)
    {
        // 提示词统一用小写书写；校验器大小写不敏感，两者对得上。
        var lowered = value.ToLowerInvariant();

        Assert.Contains($"\"{lowered}\"", LlmRequestBuilder.DefaultSystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>重要度的合法枚举值必须写进提示词。</summary>
    [Theory]
    [MemberData(nameof(ImportanceValuesData))]
    public void Prompt_MentionsEveryImportanceValue(string value)
    {
        var lowered = value.ToLowerInvariant();

        Assert.Contains($"\"{lowered}\"", LlmRequestBuilder.DefaultSystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>空值写法必须交代清楚：没行动项时 action 填空串、没截止时间时 deadline 填 null。</summary>
    [Fact]
    public void Prompt_ExplainsNullAndEmptyConventions()
    {
        var prompt = LlmRequestBuilder.DefaultSystemPrompt;

        Assert.Contains("空字符串", prompt, StringComparison.Ordinal);
        Assert.Contains("null", prompt, StringComparison.Ordinal);
    }

    /// <summary>必须明确禁止 Markdown 围栏与解释文字。</summary>
    [Fact]
    public void Prompt_ForbidsMarkdownFenceAndProse()
    {
        Assert.Contains("不要输出 Markdown 代码块", LlmRequestBuilder.DefaultSystemPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// 端到端护栏：提示词里给出的每条示例输出，都必须能被
    /// <see cref="DigestSchemaValidator"/> 原样通过。
    /// </summary>
    /// <remarks>
    /// 这是最强的漂移检测——示例跟着提示词走，提示词改了示例这里立刻红。
    /// </remarks>
    [Fact]
    public void Prompt_ExamplesPassValidation()
    {
        var examples = ExtractExamplePayloads();

        Assert.True(
            examples.Length >= 2,
            $"提示词应至少给出「有行动项」和「没有行动项」两条示例，实际找到 {examples.Length} 条。");

        foreach (var example in examples)
        {
            var result = DigestSchemaValidator.Validate(example);
            Assert.True(result.IsValid, $"提示词示例无法通过校验：{example} → {result.Describe()}");
        }
    }

    /// <summary>提示词里的示例必须覆盖「无行动项」这条路径，否则最容易漏 action/deadline 的空值写法。</summary>
    [Fact]
    public void Prompt_IncludesExampleWithoutActionItem()
    {
        var withoutAction = ExtractExamplePayloads()
            .Where(payload => payload.Contains("\"action\":\"\"", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(withoutAction);
    }

    public static TheoryData<string> RequiredFieldsData()
    {
        var data = new TheoryData<string>();
        foreach (var field in RequiredFields)
        {
            data.Add(field);
        }

        return data;
    }

    /// <summary>从提示词正文里抠出示例 JSON 行（以 <c>{"category"</c> 开头的那些）。</summary>
    private static string[] ExtractExamplePayloads()
    {
        return LlmRequestBuilder.DefaultSystemPrompt
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("{\"category\"", StringComparison.Ordinal))
            .ToArray();
    }
}
