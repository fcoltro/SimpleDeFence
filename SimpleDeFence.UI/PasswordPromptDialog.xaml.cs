using Microsoft.UI.Xaml.Controls;

namespace SimpleDeFence.UI
{
    /// <summary>Shared unlock prompt, created in this task for SimpleDeFenceDoctor.Uninstall's
    /// unlock-before-stop flow (Task 8) to consume - not used yet. The tray's "Lock" action does not
    /// need it: locking never requires a password, only unlocking does, and the Settings page already
    /// has its own inline unlock flow unrelated to this dialog. Task 8 will be this component's first
    /// call site, replacing WinForms' PasswordForm there.
    ///
    /// Callers set XamlRoot and Title, await ShowAsync(), and read <see cref="Password"/> only when
    /// the result is ContentDialogResult.Primary. Enter submits: DefaultButton="Primary" in the XAML
    /// is what makes the dialog's default button the one the Enter key invokes, so there is
    /// deliberately no KeyDown handler here - Hide() would close the dialog with
    /// ContentDialogResult.None, which is a cancel, the opposite of submitting.</summary>
    public sealed partial class PasswordPromptDialog : ContentDialog
    {
        public string Password => PasswordInput.Password;

        public PasswordPromptDialog()
        {
            InitializeComponent();
        }
    }
}
