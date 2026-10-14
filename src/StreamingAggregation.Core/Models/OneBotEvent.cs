namespace StreamingAggregation.Core.Models;

/// <summary>
    /// 表示一个 OneBot 原始事件。
    /// </summary>
    public class OneBotEvent
    {
        /// <summary>事件类型，如 "message"、"notice"、"request"。</summary>
        public string PostType { get; set; } = string.Empty;

        /// <summary>消息类型，如 "private"、"group"。</summary>
        public string MessageType { get; set; } = string.Empty;

        /// <summary>群组 ID。</summary>
        public string GroupId { get; set; } = string.Empty;

        /// <summary>发送者用户 ID。</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>原始消息内容。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>事件时间。</summary>
        public DateTime Time { get; set; }

        /// <summary>机器人自身 ID。</summary>
        public string SelfId { get; set; } = string.Empty;
    }
