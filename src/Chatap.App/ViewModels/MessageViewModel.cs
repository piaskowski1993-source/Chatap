using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatap.App.ViewModels;

// One line in a conversation tab.
public partial class MessageViewModel(bool fromMe, string text, DateTimeOffset sent, bool notSent = false) : ViewModelBase
{
    public bool FromMe { get; } = fromMe;
    public string Who => FromMe ? "you:" : "them:";
    public string Text { get; } = text;
    public DateTimeOffset Sent { get; } = sent;
    public string Time => Sent.ToLocalTime().ToString("HH:mm");

    // Set later when a send fails; the View shows "✗ not sent".
    [ObservableProperty]
    private bool _notSent = notSent;
}
