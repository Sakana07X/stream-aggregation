using System.Text.Json;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 一帧之内可能包含多个顶层 JSON 值（粘包），本类负责把它们逐个切出来。
/// </summary>
/// <remarks>
/// <para>
/// 这是 WebSocket 分帧职责，与「OneBot 事件 → 领域模型」的协议解析职责无关，因此独立于
/// <see cref="OneBotEventParser"/> / <see cref="OneBotMapper"/>；调用方在
/// <see cref="OneBotWebSocketClient"/> 中先拆分、再解析。
/// </para>
/// <para>行为约定：</para>
/// <list type="bullet">
///   <item><description>值之间允许出现任意 JSON 空白（空格、制表符、CR、LF），空白不会被当作值产出。</description></item>
///   <item><description>只认 <c>{</c> 与 <c>[</c> 开头的值；其它字节视为坏数据，逐字节跳过并在下一个 <c>{</c> / <c>[</c> 处重新同步，
///   避免坏数据连累同一帧内的其它事件。</description></item>
///   <item><description>字符串字面量内的 <c>{</c>、<c>}</c>、<c>"</c> 与转义序列（<c>\"</c>、<c>\\</c>）不会被误判为结构符号。</description></item>
///   <item><description>帧末尾出现不完整的值时抛出 <see cref="JsonException"/>；此前已经产出的值依然有效，调用方可继续处理。</description></item>
/// </list>
/// <para>
/// 产出的 <see cref="ReadOnlyMemory{T}"/> 切片都指向传入的原始缓冲区，不做拷贝，
/// 因此调用方必须在枚举期间保持原始缓冲区有效。
/// </para>
/// </remarks>
public static class OneBotFrameSplitter
{
    /// <summary>
    /// 按顺序产出帧中每一个顶层 JSON 值。
    /// </summary>
    /// <param name="payload">完整的一帧 UTF-8 字节，可能包含多个顶层 JSON 值。</param>
    /// <returns>
    /// 每个顶层 JSON 值的切片序列（0 个或多个）。空帧与全空白帧返回空序列。
    /// 枚举到帧末尾的不完整值时会抛出 <see cref="JsonException"/>，此前已产出的值不受影响。
    /// </returns>
    /// <exception cref="JsonException">帧末尾存在不完整的 JSON 值。</exception>
    public static IEnumerable<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> payload)
    {
        var offset = 0;

        while (offset < payload.Length)
        {
            if (!TryTakeValue(payload, offset, out var value, out var next))
            {
                // 空白结尾或跳过一个坏字节后继续；next 必定已经前移，不会死循环。
                offset = next;
                continue;
            }

            yield return value;
            offset = next;
        }
    }

    /// <summary>
    /// 从 <paramref name="start"/> 起取出下一个顶层 JSON 值。
    /// </summary>
    /// <param name="payload">完整的一帧 UTF-8 字节。</param>
    /// <param name="start">扫描起点。</param>
    /// <param name="value">取到的值切片；未取到时为默认值。</param>
    /// <param name="next">
    /// 下一次扫描的起点：跳过的空白与坏字节也在这里被消化，因此必定大于 <paramref name="start"/>。
    /// </param>
    /// <returns>取到一个完整值时返回 <see langword="true"/>；帧已结束或当前只有坏字节时返回 <see langword="false"/>。</returns>
    /// <exception cref="JsonException">从 <paramref name="start"/> 开始的 JSON 值不完整。</exception>
    private static bool TryTakeValue(
        ReadOnlyMemory<byte> payload,
        int start,
        out ReadOnlyMemory<byte> value,
        out int next)
    {
        var utf8 = payload.Span;
        var offset = SkipWhitespace(utf8, start);

        if (offset >= utf8.Length)
        {
            value = default;
            next = utf8.Length;
            return false;
        }

        if (utf8[offset] != (byte)'{' && utf8[offset] != (byte)'[')
        {
            // 遇到无法识别的字节：跳过并重新同步，避免坏数据连累同一帧内的其它事件。
            value = default;
            next = offset + 1;
            return false;
        }

        if (!TryFindValueEnd(utf8, offset, out var end))
        {
            value = default;
            next = utf8.Length;
            throw new JsonException("事件帧末尾存在不完整的 JSON 值。");
        }

        value = payload.Slice(offset, end - offset);
        next = end;
        return true;
    }

    /// <summary>
    /// 扫描出一个顶层 JSON 值的结束下标（不含）。
    /// 正确处理字符串字面量与转义，因此消息正文里的 <c>{</c>、<c>"</c> 不会被误判。
    /// </summary>
    private static bool TryFindValueEnd(ReadOnlySpan<byte> utf8, int start, out int end)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < utf8.Length; i++)
        {
            var current = utf8[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == (byte)'\\')
                {
                    escaped = true;
                }
                else if (current == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case (byte)'"':
                    inString = true;
                    break;

                case (byte)'{':
                case (byte)'[':
                    depth++;
                    break;

                case (byte)'}':
                case (byte)']':
                    depth--;
                    break;

                default:
                    break;
            }

            if (depth <= 0)
            {
                end = i + 1;
                return true;
            }
        }

        end = utf8.Length;
        return false;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> utf8, int offset)
    {
        while (offset < utf8.Length)
        {
            var current = utf8[offset];

            if (current is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                break;
            }

            offset++;
        }

        return offset;
    }
}