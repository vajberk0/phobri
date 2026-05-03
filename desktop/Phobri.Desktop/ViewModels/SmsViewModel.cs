using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Models;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// ViewModel for SMS messages list.
/// </summary>
public partial class SmsViewModel : ViewModelBase
{
    private readonly IDataService _dataService;

    public SmsViewModel(IDataService dataService)
    {
        _dataService = dataService;
    }

    [ObservableProperty]
    private ObservableCollection<SmsMessage> _messages = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _selectedContact;

    /// <summary>
    /// Grouped conversations (one entry per contact, latest message).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ConversationGroup> _conversations = [];

    [RelayCommand]
    private async Task LoadMessagesAsync()
    {
        IsLoading = true;
        try
        {
            Messages = new ObservableCollection<SmsMessage>(
                await _dataService.GetSmsMessagesAsync(limit: 200));
        }
        catch (Exception ex)
        {
            Messages = [];
            App.ShowExceptionDialog("Load Messages Error", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadConversationsAsync()
    {
        IsLoading = true;
        try
        {
            var allMessages = await _dataService.GetSmsMessagesAsync(limit: 500);

            // Group by address, take latest message per conversation
            var groups = allMessages
                .GroupBy(m => m.Address)
                .Select(g => new ConversationGroup
                {
                    Address = g.Key,
                    DisplayName = g.First().DisplayName,
                    LastMessage = g.First().Body,
                    LastMessageDate = g.First().DateTime,
                    MessageCount = g.Count(),
                    UnreadCount = g.Count(m => !m.Read && m.Type == SmsType.Inbox)
                })
                .OrderByDescending(g => g.LastMessageDate)
                .ToList();

            Conversations = new ObservableCollection<ConversationGroup>(groups);
        }
        catch (Exception ex)
        {
            Conversations = [];
            App.ShowExceptionDialog("Load Conversations Error", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadConversationAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        IsLoading = true;
        SelectedContact = address;
        try
        {
            Messages = new ObservableCollection<SmsMessage>(
                await _dataService.GetConversationAsync(address, limit: 200));
        }
        catch (Exception ex)
        {
            Messages = [];
            App.ShowExceptionDialog("Load Conversation Error", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add a new message (called from WebSocket push handler).
    /// </summary>
    public void AddMessage(SmsMessage message)
    {
        // Insert at beginning since list is sorted by date DESC
        Messages.Insert(0, message);
    }
}

/// <summary>
/// Represents a conversation group (one entry per phone number).
/// </summary>
public partial class ConversationGroup : ObservableObject
{
    public string Address { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public DateTimeOffset LastMessageDate { get; set; }
    public int MessageCount { get; set; }
    public int UnreadCount { get; set; }

    public string LastMessagePreview =>
        LastMessage.Length > 50 ? LastMessage[..47] + "..." : LastMessage;
}
