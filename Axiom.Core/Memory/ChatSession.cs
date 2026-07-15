using System;
using System.Collections.Generic;

namespace Axiom.Core.Memory
{
    // Plain data model for JsonChatPersistence. The WPF app's ChatMessage/ChatSession are
    // INotifyPropertyChanged view-models with WPF-only concerns (DispatcherTimer-driven
    // streaming-text reveal animation, FlowDocument rich rendering) baked in — none of that
    // applies to a terminal, so this is a clean model holding just what gets persisted, not a
    // port of the WPF classes.
    public sealed class ChatMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ThinkingContent { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int CloudPromptTokens { get; set; }
        public int CloudCompletionTokens { get; set; }
        public int CloudTotalTokens { get; set; }

        public ChatMessage()
        {
        }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content ?? string.Empty;
        }
    }

    public sealed class ChatSession
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public ChatSession()
        {
        }

        public ChatSession(string name)
        {
            Name = name;
        }
    }
}
