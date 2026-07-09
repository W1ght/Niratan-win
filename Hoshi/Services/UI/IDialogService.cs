using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Hoshi.Services.UI;

public interface IDialogService
{
    void Initialize(XamlRoot root);
    Task<string?> OpenFilePickerAsync(params string[] fileTypeFilters);
    Task<string?> OpenFolderPickerAsync();
    Task<string?> PromptTextAsync(
        string title,
        string placeholder,
        string primaryButtonText,
        string secondaryButtonText);
    Task<bool> ConfirmAsync(string title, string message);
}
