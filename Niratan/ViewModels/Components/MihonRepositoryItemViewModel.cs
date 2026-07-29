using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Niratan.Models.Manga;

namespace Niratan.ViewModels.Components;

public sealed class MihonRepositoryItemViewModel
{
    public MihonRepositoryItemViewModel(
        MihonRepositoryConfiguration repository,
        Func<MihonRepositoryItemViewModel, Task> edit,
        Func<MihonRepositoryItemViewModel, Task> remove)
    {
        Repository = repository;
        EditCommand = new AsyncRelayCommand(() => edit(this));
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public MihonRepositoryConfiguration Repository { get; }
    public string Id => Repository.Id;
    public string Name => Repository.Name;
    public string IndexUrl => Repository.IndexUrl;
    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    public string AutomationId =>
        $"MihonRepository_{SanitizeAutomationSegment(Id)}";
    public string EditAutomationId =>
        $"{AutomationId}_Edit";
    public string RemoveAutomationId =>
        $"{AutomationId}_Remove";

    public MihonRepositoryConfiguration ToConfiguration() => new()
    {
        Id = Repository.Id,
        Name = Repository.Name,
        IndexUrl = Repository.IndexUrl,
    };

    private static string SanitizeAutomationSegment(string value) =>
        new(value.Select(character =>
                char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
}
