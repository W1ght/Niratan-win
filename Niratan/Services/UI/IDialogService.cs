using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Niratan.Services.UI;

public interface IDialogService
{
    void Initialize(XamlRoot root);
    Task<string?> OpenFilePickerAsync(params string[] fileTypeFilters);
    Task<string?> SaveFilePickerAsync(
        string suggestedFileName,
        string fileTypeDescription,
        string fileExtension);
    Task<string?> OpenFolderPickerAsync();
    Task<string?> PromptTextAsync(
        string title,
        string placeholder,
        string primaryButtonText,
        string secondaryButtonText);
    Task<bool> ConfirmAsync(string title, string message);
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        string secondaryButtonText);
}
