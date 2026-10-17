using System.Net;
using System.Net.Sockets;

namespace StreamingAggregation.Core.Configuration;

/// <summary>
/// OneBot 与模型端点共用的传输判定入口。
/// 失败即收紧：无法确认是回环就当作远程。
/// </summary>
public static class EndpointTransportPolicy
{
    /// <summary>
    /// 判定给定主机是否为回环地址。
    /// 仅确认是回环才返回 <c>true</c>；其余一律返回 <c>false</c>（视为远程）。
    /// </summary>
    public static bool IsLoopback(string? host)
    {
        // net48 的 string.IsNullOrWhiteSpace 没有 [NotNullWhen(false)]，
        // 编译器无法据此收窄 host，所以改成先 Trim 再判空——语义等价
        // （纯空白 Trim 后为空串，原本也会一路判到末尾 return false）。
        var h = host?.Trim();
        if (h is null || h.Length == 0)
        {
            return false;
        }

        // 带尾点的主机名（如 localhost.）一律视为远程
        // net48 的 string.EndsWith 没有 char 重载，用 Ordinal 字符串比较等价替代
        if (h.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        // localhost（OrdinalIgnoreCase）；localhost.localdomain 等不命中 → 远程
        if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 优先 Uri.IsLoopback；构造失败（格式非法）→ 远程
        try
        {
            if (new Uri($"http://{h}").IsLoopback)
            {
                return true;
            }
        }
        catch (UriFormatException)
        {
            // 解析失败，继续按远程处理
        }

        // 裸 IP 兜底：IPAddress.IsLoopback + 手动覆盖整个 127.0.0.0/8
        // （IPAddress.IsLoopback 只认 127.0.0.1 与 ::1）
        if (IPAddress.TryParse(h, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 127)
                {
                    return true;
                }
            }
        }

        // 无法解析的主机名、空串、格式非法输入 → 远程（失败即收紧）
        return false;
    }
}
