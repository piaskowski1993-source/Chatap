using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chatap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chatap.App.ViewModels;

// One tab. Holds the reference to its connection handler and turns the handler's events into what the tab shows.
// The handler raises its events on a background thread; every reaction is posted to the UI thread first.
public partial class ConversationViewModel : ViewModelBase
{
    // Below the 64 KB protocol limit even when every character needs 3 bytes in UTF-8.
    const int MaxTextLength = 20_000;

    readonly ConnectionHandler _handler;
    readonly ConversationStore _store;
    readonly MainWindowViewModel _main;
    readonly string _peerIp;
    DateTimeOffset _readyAt;

    // Called inside Node.ConversationStarted, before the handler sends its hello,
    // so that no event can be missed.
    public ConversationViewModel(ConnectionHandler handler, ConversationStore store, MainWindowViewModel main)
    {
        _handler = handler;
        _store = store;
        _main = main;
        _peerIp = handler.RemoteEndPoint.Split(':')[0];
        _title = $"connecting · {_peerIp}";

        handler.Ready += _ => Dispatcher.UIThread.Post(OnReady);
        handler.MessageReceived += message => Dispatcher.UIThread.Post(
            () => Messages.Add(new MessageViewModel(false, message.Text, message.Sent)));
        handler.NotSent += texts => Dispatcher.UIThread.Post(() => MarkNotSent(texts));
        handler.Disconnected += reason => Dispatcher.UIThread.Post(() => OnDisconnected(reason));
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _draft = "";

    [ObservableProperty]
    private bool _canShowHistory;

    public bool IsReady => _handler.State == HandlerState.Ready;

    [RelayCommand]
    private async Task SendAsync()
    {
        string text = Draft.Trim();
        if (text.Length == 0)
            return;
        if (text.Length > MaxTextLength)
        {
            _main.Status = $"The message is too long: {text.Length} characters. The limit is {MaxTextLength}.";
            return;
        }

        Draft = "";
        var line = new MessageViewModel(true, text, DateTimeOffset.UtcNow);
        Messages.Add(line);
        if (!await _handler.SendAsync(text))
            line.NotSent = true;
    }

    // Shows the saved messages from earlier sessions above the current ones, one time.
    [RelayCommand]
    private void ShowHistory()
    {
        if (_handler.PeerId is not Guid peer)
            return;

        var earlier = _store.ReadHistory(peer)
            .Where(entry => entry.Message.Sent < _readyAt)
            .Select(entry => new MessageViewModel(entry.Message.From != peer, entry.Message.Text, entry.Message.Sent, entry.NotSent))
            .ToList();
        for (int i = 0; i < earlier.Count; i++)
            Messages.Insert(i, earlier[i]);

        CanShowHistory = false;
        if (earlier.Count == 0)
            _main.Status = "No earlier messages with this peer.";
    }

    void OnReady()
    {
        _readyAt = DateTimeOffset.UtcNow;
        Title = $"{_handler.PeerId.ToString()![..8]} · {_peerIp}";
        CanShowHistory = true;
        _main.ShowTab(this); // an incoming conversation appears only now
    }

    // A text can occur more than once; mark the newest of its lines that is not marked yet.
    void MarkNotSent(IReadOnlyList<string> texts)
    {
        foreach (string text in texts)
        {
            var line = Messages.LastOrDefault(m => m.FromMe && !m.NotSent && m.Text == text);
            if (line is not null)
                line.NotSent = true;
        }
    }

    void OnDisconnected(CloseReason reason)
    {
        bool wasReady = _readyAt != default;
        string? message = reason switch
        {
            CloseReason.VersionMismatch =>
                $"Cannot chat with {_peerIp}: you use version {Protocol.Version}, the other node uses version {_handler.PeerVersion}. Both must use the same version.",
            _ when wasReady => $"Conversation with {Title} ended.",
            _ when _handler.IsOutgoing => $"Could not start a chat with {_peerIp}: no answer from Chatap.",
            _ => null, // an incoming connection that never said hello: the log has it
        };
        _main.EndTab(this, message);
    }
}
