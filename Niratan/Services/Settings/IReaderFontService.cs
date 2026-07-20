using System.Collections.Generic;
using System.Threading.Tasks;
using Niratan.Models.Settings;

namespace Niratan.Services.Settings;

public interface IReaderFontService
{
    string FontsPath { get; }
    IReadOnlyList<JapaneseFontOption> GetAvailableFonts();
    Task<JapaneseFontOption> ImportAsync(string sourcePath);
    Task DeleteAsync(string importedFileName);
}
