using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chatap.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chatap.App.ViewModels;

// The whole window: your address, the connect form, the tabs and the status line.
public partial class MainWindowViewModel : ViewModelBase
{
    readonly AppServices _services;

    // Subscribes to the node before it starts, so that no conversation is missed.
    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        services.Node.ConversationStarted += OnConversationStarted;
        services.Log.LogFailed += error => Dispatcher.UIThread.Post(
            () => Status = $"Chatap continues without a log: {error.Message}");
        services.Store.StoreFailed += error => Dispatcher.UIThread.Post(
            () => Status = $"Chatap continues without saving messages: {error.Message}");
    }

    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];

    [ObservableProperty]
    private ConversationViewModel? _selected;

    [ObservableProperty]
    private string _ownAddress = "";

    [ObservableProperty]
    private string _connectHost = "";

    [ObservableProperty]
    private string _connectPort = "";

    [ObservableProperty]
    private string _status = "Tell your address to the other person, or connect to theirs.";

    // Call after Node.Start: the port is known only then.
    public void ShowOwnAddress()
    {
        int port = _services.Node.Port;
        var lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => $"{a.Address} : {port}")
            .ToList();

        OwnAddress = lan.Count == 0
            ? $"You are: 127.0.0.1 : {port}   (no network found: same computer only)"
            : $"You are: {string.Join("   or   ", lan)}   (also 127.0.0.1 : {port})";
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        string host = ConnectHost.Trim();
        if (host.Length == 0)
        {
            Status = "Type the IP address of the other node.";
            return;
        }
        if (!int.TryParse(ConnectPort.Trim(), out int port) || port is < 1 or > 65535)
        {
            Status = "The port must be a number from 1 to 65535.";
            return;
        }

        Status = $"Connecting to {host} : {port} …";
        try
        {
            await _services.Node.ConnectAsync(host, port);
            Status = $"Connected to {host} : {port}. Waiting for its hello …";
        }
        catch (SocketException e)
        {
            Status = $"Cannot connect to {host} : {port}. {e.Message}";
        }
    }

    // Runs inside Node.ConversationStarted, on any thread.
    void OnConversationStarted(ConnectionHandler handler)
    {
        var conversation = new ConversationViewModel(handler, _services.Store, this);
        if (handler.IsOutgoing)
            Dispatcher.UIThread.Post(() => ShowTab(conversation)); // shows "connecting · <ip>" at once
        // An incoming conversation shows its tab itself when it is Ready.
    }

    internal void ShowTab(ConversationViewModel conversation)
    {
        if (!Conversations.Contains(conversation))
            Conversations.Add(conversation);
        Selected = conversation;
        if (conversation.IsReady)
            Status = $"Chatting with {conversation.Title}.";
    }

    internal void EndTab(ConversationViewModel conversation, string? message)
    {
        Conversations.Remove(conversation);
        if (message is not null)
            Status = message;
    }
}
