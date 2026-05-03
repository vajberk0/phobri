using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.Views;

/// <summary>
/// Dialog for password setup (first run) and unlock (subsequent runs).
/// Shows different UI based on whether a password is already configured.
/// </summary>
public partial class PasswordDialog : Window
{
    private readonly IPasswordManagerService _passwordManager;
    private bool _isSetupMode;

    /// <summary>True if the user successfully set up or unlocked the vault.</summary>
    public bool Success { get; private set; }

    /// <summary>
    /// Creates a password dialog in setup mode (first run) or unlock mode.
    /// </summary>
    /// <param name="passwordManager">The password manager service.</param>
    /// <param name="isSetupMode">True for first-time setup, false for unlock.</param>
    public PasswordDialog(IPasswordManagerService passwordManager, bool isSetupMode = false)
    {
        InitializeComponent();
        _passwordManager = passwordManager;
        _isSetupMode = isSetupMode;

        ConfigureMode();

        // Auto-focus the first text field so user can start typing immediately
        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>
    /// Creates a password dialog. Automatically detects if setup or unlock is needed.
    /// Convenience overload.
    /// </summary>
    public static PasswordDialog Create(IPasswordManagerService passwordManager)
    {
        return new PasswordDialog(passwordManager, !passwordManager.IsConfigured);
    }

    private void ConfigureMode()
    {
        if (_isSetupMode)
        {
            Title = "Set Up Password";
            TitleText.Text = "Set Up Phobri Password";
            DescriptionText.Text =
                "Create a password to protect your messages and call logs. " +
                "This password encrypts all data on disk and is never sent over the network.";
            ActionButton.Content = "Set Password";
            ConfirmPanel.IsVisible = true;
            CancelButton.IsVisible = false;
        }
        else
        {
            Title = "Unlock Phobri";
            TitleText.Text = "Unlock Phobri";
            DescriptionText.Text =
                "Enter your Phobri password to access messages and call logs.";
            ActionButton.Content = "Unlock";
            ConfirmPanel.IsVisible = false;
            CancelButton.IsVisible = true;
        }
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;

        var password = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            ShowError("Password cannot be empty.");
            return;
        }

        if (_isSetupMode)
        {
            // Setup: validate confirmation and strength
            var confirm = ConfirmPasswordBox.Text ?? string.Empty;
            if (password != confirm)
            {
                ShowError("Passwords do not match.");
                return;
            }

            if (password.Length < 4)
            {
                ShowError("Password must be at least 4 characters.");
                return;
            }

            try
            {
                _passwordManager.SetupPassword(password);
                Success = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set password: {ex.Message}");
            }
        }
        else
        {
            // Unlock
            try
            {
                if (_passwordManager.Unlock(password))
                {
                    Success = true;
                    Close();
                }
                else
                {
                    ShowError("Incorrect password. Please try again.");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error: {ex.Message}");
            }
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Success = false;
        Close();
    }

    /// <summary>
    /// Allow Enter key in either password field to trigger the action.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter)
        {
            OnActionClick(null, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !_isSetupMode)
        {
            OnCancelClick(null, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
