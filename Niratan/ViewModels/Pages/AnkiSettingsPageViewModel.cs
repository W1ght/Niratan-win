using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Models.Settings;
using Niratan.Services.Anki;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Serilog;

namespace Niratan.ViewModels.Pages;

public partial class AnkiFieldMappingViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string FieldName { get; set; } = "";

    [ObservableProperty]
    public partial string Template { get; set; } = "";
}

public partial class AnkiSettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAnkiService _ankiService;
    private bool _isInitializing = true;

    [ObservableProperty]
    public partial string AnkiConnectUrl { get; set; } = "http://localhost:8765";

    [ObservableProperty]
    public partial bool IsTestingConnection { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    public ObservableCollection<AnkiDeck> AvailableDecks { get; } = [];
    public ObservableCollection<AnkiNoteType> AvailableNoteTypes { get; } = [];

    [ObservableProperty]
    public partial AnkiDeck? SelectedDeck { get; set; }

    [ObservableProperty]
    public partial AnkiNoteType? SelectedNoteType { get; set; }

    public ObservableCollection<AnkiFieldMappingViewModel> FieldMappings { get; } = [];
    public ObservableCollection<string> HandlebarOptions { get; } = [];

    [ObservableProperty]
    public partial string Tags { get; set; } = "";

    [ObservableProperty]
    public partial bool AllowDupes { get; set; }

    [ObservableProperty]
    public partial AnkiDuplicateScope SelectedDuplicateScope { get; set; } = AnkiDuplicateScope.Collection;

    [ObservableProperty]
    public partial bool CheckDuplicatesAcrossAllModels { get; set; }

    [ObservableProperty]
    public partial bool CompactGlossaries { get; set; }

    [ObservableProperty]
    public partial bool EmbedMedia { get; set; } = true;

    [ObservableProperty]
    public partial bool AnkiConnectForceSync { get; set; }

    [ObservableProperty]
    public partial bool IsFetchingData { get; set; }

    public AnkiDuplicateScope[] AvailableDuplicateScopes { get; } = Enum.GetValues<AnkiDuplicateScope>();
    public bool HasFieldMappingDefaults => SelectedNoteType != null && LapisPreset.HasDefaults(SelectedNoteType);

    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IAsyncRelayCommand FetchDataCommand { get; }
    public IRelayCommand ApplyDefaultsCommand { get; }

    public AnkiSettingsPageViewModel()
    {
        _settingsService = App.GetService<ISettingsService>();
        _ankiService = App.GetService<IAnkiService>();
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        FetchDataCommand = new AsyncRelayCommand(FetchDataAsync);
        ApplyDefaultsCommand = new RelayCommand(ApplyFieldMappingDefaults);

        HandlebarOptions = new ObservableCollection<string>(
            AnkiHandlebarRenderer.GetHandlebarOptions());

        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var anki = _settingsService.Current.AnkiSettings;

        AnkiConnectUrl = anki.AnkiConnectUrl;
        AnkiConnectForceSync = anki.AnkiConnectForceSync;
        Tags = anki.Tags;
        AllowDupes = anki.AllowDupes;
        SelectedDuplicateScope = anki.DuplicateScope;
        CheckDuplicatesAcrossAllModels = anki.CheckDuplicatesAcrossAllModels;
        CompactGlossaries = anki.CompactGlossaries;
        EmbedMedia = anki.EmbedMedia;

        AvailableDecks.Clear();
        foreach (var deck in anki.AvailableDecks)
            AvailableDecks.Add(deck);

        AvailableNoteTypes.Clear();
        foreach (var nt in anki.AvailableNoteTypes)
            AvailableNoteTypes.Add(nt);

        SelectedDeck = AvailableDecks.FirstOrDefault(d => d.Id == anki.SelectedDeckId);
        SelectedNoteType = AvailableNoteTypes.FirstOrDefault(nt => nt.Id == anki.SelectedNoteTypeId);

        if (SelectedNoteType != null)
            AutofillFieldMappings(SelectedNoteType);
        else
            RefreshFieldMappings();
        RefreshHandlebarOptions();
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        var anki = _settingsService.Current.AnkiSettings;
        anki.AnkiConnectUrl = AnkiConnectUrl;
        anki.AnkiConnectForceSync = AnkiConnectForceSync;
        anki.Tags = Tags;
        anki.AllowDupes = AllowDupes;
        anki.DuplicateScope = SelectedDuplicateScope;
        anki.CheckDuplicatesAcrossAllModels = CheckDuplicatesAcrossAllModels;
        anki.CompactGlossaries = CompactGlossaries;
        anki.EmbedMedia = EmbedMedia;
        anki.SelectedDeckId = SelectedDeck?.Id;
        anki.SelectedDeckName = SelectedDeck?.Name;
        anki.SelectedNoteTypeId = SelectedNoteType?.Id;
        anki.SelectedNoteTypeName = SelectedNoteType?.Name;
        anki.AvailableDecks = AvailableDecks.ToList();
        anki.AvailableNoteTypes = AvailableNoteTypes.ToList();

        anki.FieldMappings.Clear();
        foreach (var fm in FieldMappings)
        {
            if (!string.IsNullOrWhiteSpace(fm.Template) && fm.Template != "-")
                anki.FieldMappings[fm.FieldName] = fm.Template;
        }

        _settingsService.Set(s => s.AnkiSettings, anki);
        _ = _settingsService.SaveAsync();

        _ankiService.UpdateSettings(anki);
    }

    partial void OnAnkiConnectUrlChanged(string value) => SaveSettings();
    partial void OnAnkiConnectForceSyncChanged(bool value) => SaveSettings();
    partial void OnTagsChanged(string value) => SaveSettings();
    partial void OnAllowDupesChanged(bool value) => SaveSettings();
    partial void OnSelectedDuplicateScopeChanged(AnkiDuplicateScope value) => SaveSettings();
    partial void OnCheckDuplicatesAcrossAllModelsChanged(bool value) => SaveSettings();
    partial void OnCompactGlossariesChanged(bool value) => SaveSettings();
    partial void OnEmbedMediaChanged(bool value) => SaveSettings();

    partial void OnSelectedDeckChanged(AnkiDeck? value)
    {
        SaveSettings();
    }

    partial void OnSelectedNoteTypeChanged(AnkiNoteType? value)
    {
        if (value != null)
            AutofillFieldMappings(value);
        else
            RefreshFieldMappings();
        RefreshHandlebarOptions();
        OnPropertyChanged(nameof(HasFieldMappingDefaults));
        SaveSettings();
    }

    private void AutofillFieldMappings(AnkiNoteType noteType)
    {
        var merged = LapisPreset.AutofillDefaults(noteType, CurrentFieldMappings());
        RefreshFieldMappings(merged);
    }

    private void ApplyFieldMappingDefaults()
    {
        if (SelectedNoteType == null)
            return;

        var merged = LapisPreset.ApplyDefaults(SelectedNoteType, CurrentFieldMappings());
        RefreshFieldMappings(merged);
        SaveSettings();
    }

    private System.Collections.Generic.Dictionary<string, string> CurrentFieldMappings()
    {
        var currentMappings = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var fm in FieldMappings)
        {
            if (!string.IsNullOrWhiteSpace(fm.Template))
                currentMappings[fm.FieldName] = fm.Template;
        }

        if (currentMappings.Count == 0)
        {
            foreach (var (field, template) in _settingsService.Current.AnkiSettings.FieldMappings)
            {
                if (!string.IsNullOrWhiteSpace(template))
                    currentMappings[field] = template;
            }
        }

        return currentMappings;
    }

    private void RefreshFieldMappings(System.Collections.Generic.Dictionary<string, string>? mappings = null)
    {
        FieldMappings.Clear();
        if (SelectedNoteType == null) return;

        mappings ??= _settingsService.Current.AnkiSettings.FieldMappings;

        foreach (var field in SelectedNoteType.Fields)
        {
            var template = mappings.TryGetValue(field, out var t) ? t : "";
            FieldMappings.Add(new AnkiFieldMappingViewModel
            {
                FieldName = field,
                Template = template,
            });
        }
    }

    private void RefreshHandlebarOptions()
    {
        HandlebarOptions.Clear();
        foreach (var option in AnkiHandlebarRenderer.GetHandlebarOptions())
            HandlebarOptions.Add(option);
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            IsTestingConnection = true;
            ConnectionStatusText = LocalizedStatusText(
                "AnkiConnectionTestingStatus",
                "正在测试连接...",
                "Testing connection...");

            // Save URL first so the client uses the right endpoint
            var anki = _settingsService.Current.AnkiSettings;
            anki.AnkiConnectUrl = AnkiConnectUrl;
            _ankiService.UpdateSettings(anki);

            var available = await _ankiService.IsAvailableAsync();
            IsConnected = available;
            ConnectionStatusText = available
                ? LocalizedStatusText(
                    "AnkiConnectionConnectedStatus",
                    "已连接到 AnkiConnect",
                    "Connected to AnkiConnect")
                : LocalizedStatusText(
                    "AnkiConnectionFailedStatus",
                    "连接失败。请确认 Anki 已启动，并已启用 AnkiConnect 插件。",
                    "Failed to connect. Make sure Anki is running with the AnkiConnect plugin.");
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatusText = FormatLocalizedStatusText(
                "AnkiConnectionErrorStatus",
                "连接错误：{0}",
                "Connection error: {0}",
                ex.Message);
            Log.Warning(ex, "[AnkiSettings] Connection test failed");
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private async Task FetchDataAsync()
    {
        try
        {
            IsFetchingData = true;

            var decks = await _ankiService.FetchDecksAsync();
            var noteTypes = await _ankiService.FetchNoteTypesAsync();

            AvailableDecks.Clear();
            foreach (var deck in decks)
                AvailableDecks.Add(deck);

            AvailableNoteTypes.Clear();
            foreach (var nt in noteTypes)
                AvailableNoteTypes.Add(nt);

            // Preserve previous selection if possible
            var prevDeckId = SelectedDeck?.Id;
            var prevNoteTypeId = SelectedNoteType?.Id;
            SelectedDeck = decks.FirstOrDefault(d => d.Id == prevDeckId);
            SelectedNoteType = noteTypes.FirstOrDefault(nt => nt.Id == prevNoteTypeId);

            RefreshHandlebarOptions();
            SaveSettings();
        }
        catch (Exception ex)
        {
            ConnectionStatusText = FormatLocalizedStatusText(
                "AnkiFetchDataFailedStatus",
                "获取数据失败：{0}",
                "Failed to fetch data: {0}",
                ex.Message);
            Log.Warning(ex, "[AnkiSettings] Fetch data failed");
        }
        finally
        {
            IsFetchingData = false;
        }
    }

    public void OnFieldMappingChanged(AnkiFieldMappingViewModel mapping, string newTemplate)
    {
        mapping.Template = newTemplate;
        SaveSettings();
    }

    public void OnNavigatedFrom()
    {
        SaveSettings();
    }

    private static string LocalizedStatusText(string key, string zhCn, string enUs)
    {
        _ = key;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? zhCn
            : enUs;
    }

    private static string FormatLocalizedStatusText(string key, string zhCn, string enUs, params object[] args)
    {
        var template = LocalizedStatusText(key, zhCn, enUs);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
