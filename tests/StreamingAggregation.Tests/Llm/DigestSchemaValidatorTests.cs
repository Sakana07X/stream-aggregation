using StreamingAggregation.Core;
using StreamingAggregation.Infrastructure.Llm;

namespace StreamingAggregation.Tests.Llm;

/// <summary>
/// <see cref="DigestSchemaValidator"/> 的六字段契约与降级行为测试。
/// </summary>
/// <remarks>
/// <para>
/// 设计取向是「归一化而非丢弃」：模型漏写一个可猜的字段（<c>importance</c>），
/// 不该让已经合格的摘要和行动项整轮作废退回本地规则；但真正的契约错误
/// （类型写错、必填字段缺失）仍然必须暴露出来。
/// </para>
/// </remarks>
public sealed class DigestSchemaValidatorTests
{
    /// <summary>构造一个六字段齐全的合法输出；各字段可单独替换以构造异常样本。</summary>
    private static string Json(
        string category = "\"todo\"",
        string summary = "\"张三承诺周五前提交接口文档\"",
        string action = "\"跟进张三提交接口文档\"",
        string deadline = "\"2026-10-17T18:00:00\"",
        string? importance = "\"high\"",
        string reason = "\"群友明确认领了带截止时间的待办\"")
    {
        var importancePart = importance is null ? string.Empty : $",\"importance\":{importance}";
        return $"{{\"category\":{category},\"summary\":{summary},\"action\":{action},"
            + $"\"deadline\":{deadline}{importancePart},\"reason\":{reason}}}";
    }

    /// <summary>六个字段齐全时校验通过，且字段值原样承接。</summary>
    [Fact]
    public void Validate_AcceptsCompleteSixFieldPayload()
    {
        var result = DigestSchemaValidator.Validate(Json());

        Assert.True(result.IsValid, result.Describe());
        var draft = Assert.IsType<ValidatedDigestDraft>(result.Draft);
        Assert.Equal(DigestCategory.Todo, draft.Category);
        Assert.Equal("张三承诺周五前提交接口文档", draft.Summary);
        Assert.Equal("跟进张三提交接口文档", draft.Action);
        Assert.Equal("群友明确认领了带截止时间的待办", draft.Reason);
        Assert.Equal(Importance.High, draft.Importance);
        Assert.Equal(new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc), draft.Deadline);
        Assert.True(draft.HasActionItem);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// 回归护栏（核心）：<c>importance</c> 整个字段缺失时不再让整轮失败，降级为 Medium。
    /// </summary>
    [Fact]
    public void Validate_MissingImportance_DegradesToDefault()
    {
        var result = DigestSchemaValidator.Validate(Json(importance: null));

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(Importance.Medium, result.Draft!.Importance);
        Assert.Equal(DigestSchemaValidator.DefaultImportance, result.Draft.Importance);
        Assert.DoesNotContain(result.Errors, error => error.Field == "importance");
    }

    /// <summary>
    /// <c>importance</c> 存在但读不出取值（null、空串、未知枚举名）一律降级为 Medium，
    /// 摘要与行动项照常可用。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"critical\"")]
    [InlineData("\"重要\"")]
    public void Validate_UnreadableImportance_DegradesToDefault(string importance)
    {
        var result = DigestSchemaValidator.Validate(Json(importance: importance));

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(Importance.Medium, result.Draft!.Importance);
    }

    /// <summary>大小写不敏感：提示词里写的小写枚举名同样要被接受。</summary>
    [Theory]
    [InlineData("\"high\"", Importance.High)]
    [InlineData("\"HIGH\"", Importance.High)]
    [InlineData("\" medium \"", Importance.Medium)]
    [InlineData("\"low\"", Importance.Low)]
    public void Validate_AcceptsImportanceCaseInsensitively(string importance, Importance expected)
    {
        var result = DigestSchemaValidator.Validate(Json(importance: importance));

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(expected, result.Draft!.Importance);
    }

    /// <summary>
    /// 降级只覆盖「信息缺失」，不覆盖「契约写错」：<c>importance</c> 给了数字仍判失败。
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("[\"high\"]")]
    public void Validate_ImportanceWithWrongJsonType_StillFails(string importance)
    {
        var result = DigestSchemaValidator.Validate(Json(importance: importance));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.Field == "importance");
        Assert.Equal(DigestValidationErrorKind.TypeMismatch, error.Kind);
    }

    /// <summary>
    /// 降级是 importance 专属的：其余五个字段缺失仍然判失败，
    /// 否则「六个字段」的契约就名存实亡了。
    /// </summary>
    [Theory]
    [InlineData(
        "{\"summary\":\"内容\",\"action\":\"\",\"deadline\":null,\"reason\":\"理由\",\"importance\":\"low\"}",
        "category")]
    [InlineData(
        "{\"category\":\"todo\",\"action\":\"\",\"deadline\":null,\"reason\":\"理由\",\"importance\":\"low\"}",
        "summary")]
    [InlineData(
        "{\"category\":\"todo\",\"summary\":\"内容\",\"action\":\"\",\"deadline\":null,\"importance\":\"low\"}",
        "reason")]
    [InlineData(
        "{\"category\":\"todo\",\"summary\":\"内容\",\"action\":\"\",\"reason\":\"理由\",\"importance\":\"low\"}",
        "deadline")]
    public void Validate_MissingRequiredField_StillFails(string payload, string missingField)
    {
        var result = DigestSchemaValidator.Validate(payload);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.Field == missingField && e.Kind == DigestValidationErrorKind.FieldMissing);
    }

    /// <summary>deadline 为 null 是合法写法（对照 importance 的降级路径）。</summary>
    [Fact]
    public void Validate_NullDeadline_IsAccepted()
    {
        var result = DigestSchemaValidator.Validate(Json(deadline: "null"));

        Assert.True(result.IsValid, result.Describe());
        Assert.Null(result.Draft!.Deadline);
    }

    /// <summary>action 为空串表示「本轮没有行动项」，不该产生行动项。</summary>
    [Fact]
    public void Validate_EmptyAction_MeansNoActionItem()
    {
        var result = DigestSchemaValidator.Validate(Json(action: "\"\""));

        Assert.True(result.IsValid, result.Describe());
        Assert.False(result.Draft!.HasActionItem);
        Assert.Null(result.Draft.Action);
    }

    /// <summary>字段名大小写不敏感。</summary>
    [Fact]
    public void Validate_AcceptsFieldNamesCaseInsensitively()
    {
        // net48 没有 Replace(旧, 新, StringComparison)（netstandard2.1）；
        // 双参重载本身就是 Ordinal 比较，与这里传的值一致。
        var payload = Json().Replace("\"category\"", "\"Category\"");

        var result = DigestSchemaValidator.Validate(payload);

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(DigestCategory.Todo, result.Draft!.Category);
    }

    /// <summary>summary 为空仍判失败：必填文本不能降级。</summary>
    [Fact]
    public void Validate_EmptySummary_Fails()
    {
        var result = DigestSchemaValidator.Validate(Json(summary: "\"   \""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "summary" && e.Kind == DigestValidationErrorKind.EmptyValue);
    }

    /// <summary>枚举字段取值非法时，category 仍然判失败（对照 importance 的降级）。</summary>
    [Fact]
    public void Validate_UnknownCategory_Fails()
    {
        var result = DigestSchemaValidator.Validate(Json(category: "\"critical\""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "category" && e.Kind == DigestValidationErrorKind.InvalidEnum);
    }
}
