using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Hoshi.Services.UI;

public interface IDialogService
{
    void Initialize(XamlRoot root);
    Task<string?> OpenFilePickerAsync(string fileTypeFilter = "*");
    Task<bool> ConfirmAsync(string title, string message);
}
