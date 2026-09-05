using FluentAssertions;
using System.Xml.Linq;

namespace Niratan.Tests.Views.Pages;

public sealed class MangaLibraryPageAssetTests
{
    private static readonly string ProjectRoot = ResolveProjectRoot();

    private static string ResolveProjectRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(
            "NIRATAN_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredProject = Path.Combine(configuredRoot, "Niratan");
            if (Directory.Exists(configuredProject))
                return Path.GetFullPath(configuredProject);
        }
        var workingTree = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "Niratan"));
        return Directory.Exists(workingTree)
            ? workingTree
            : Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "Niratan"));
    }

    [Fact]
    public void Navigation_ExposesMangaAsIndependentTopLevelDomain()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"MangaNavItem\"");
        xaml.Should().Contain("Tag=\"Niratan.Views.Pages.MangaLibraryPage\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"BrowseNavItem\"");
        xaml.Should().NotContain("Tag=\"Niratan.Views.Pages.BrowsePage\"");
    }

    [Fact]
    public void MangaLibrary_ExposesDirectReadOnlyImportActions()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"ImportMangaFolderButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ImportMangaFileButton\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.ImportFolderCommand}\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.ImportFileCommand}\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.Books, Mode=OneWay}\"");
        xaml.Should().Contain("AllowDrop=\"True\"");
        code.Should().Contain("ViewModel.ImportDroppedCommand.ExecuteAsync(paths)");
    }

    [Fact]
    public void MangaLibrary_ReusesNovelBookshelfCardAndCommandBar()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));

        xaml.Should().Contain("<controls:NovelBookCard");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryCommandBar\"");
        xaml.Should().Contain("MinItemWidth=\"180\"");
        xaml.Should().Contain("MinItemHeight=\"326\"");
        xaml.Should().NotContain("CardBackgroundFillColorDefaultBrush");
        xaml.Should().NotContain("MangaLibraryEmptyDescriptionText");
    }

    [Fact]
    public void MangaLibrary_ProvidesNiratanLocalAndOnlineBookshelfSurfaces()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var service = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "SuwayomiService.cs"));

        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryLocalSurfaceButton\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryOnlineSurfaceButton\"");
        xaml.Should().Contain("ItemTemplate=\"{StaticResource RemoteMangaBookTemplate}\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.OnlineBooks, Mode=OneWay}\"");
        xaml.Should().Contain("ShowsProgress=\"False\"");
        service.Should().Contain("\"category\"");
        service.Should().Contain("GetThumbnailPathAsync");
    }

    [Fact]
    public void RemoteMangaCards_OpenNiratanAlignedDetailsBeforeReader()
    {
        var library = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var browse = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var details = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Views",
                "Manga",
                "RemoteMangaDetailView.xaml"));
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaLibraryPageViewModel.cs"));

        library.Should().Contain("<manga:RemoteMangaDetailView");
        browse.Should().Contain("<manga:RemoteMangaDetailView");
        details.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaRemoteDetailsContinueButton\"");
        details.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaRemoteDetailsLibraryButton\"");
        details.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaRemoteDetailsChaptersList\"");
        details.Should().Contain(
            "ItemsSource=\"{Binding SelectedRemoteMangaDetails.Chapters, Mode=OneWay}\"");
        viewModel.Should().Contain("ShowSuwayomiMangaDetailsAsync");
        viewModel.Should().Contain("ShowMihonMangaDetailsAsync");
        viewModel.Should().Contain("GetMangaDetailsAsync");
        viewModel.Should().Contain("SetLibraryAsync");
        viewModel.Should().Contain("supportsOnlineLibrary: true");
        viewModel.Should().Contain("CreateMihonLibraryEntry");
        viewModel.Should().Contain("SaveConfigurationAsync");
        viewModel.Should().Contain("supportsOnlineLibrary: false");
        viewModel.Should().Contain("ApplyDiscoveryDetails");
        viewModel.Should().Contain("LoadMangaDiscoveryDetailsAsync");
        viewModel.Should().NotContain("OpenOnlineMangaAsync");
        viewModel.Should().NotContain("OpenMihonMangaAsync");
    }

    [Fact]
    public void MangaLibrary_PutsRemoteSourcesExtensionsAndSettingsInTopNavigation()
    {
        var library = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml.cs"));
        var settings = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Views",
                "Manga",
                "MangaSourceSettingsContent.xaml"));

        library.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibrarySourcesNavItem\"");
        library.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryDiscoverNavItem\"");
        library.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryExtensionsNavItem\"");
        library.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibrarySettingsNavItem\"");
        library.Should().Contain("x:Uid=\"MangaLibraryTopDiscoverNavItem\"");
        library.Should().Contain("x:Uid=\"MangaLibraryTopSourcesNavItem\"");
        library.Should().Contain("Tag=\"Discover\"");
        library.Should().Contain("Tag=\"Browse\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverSections\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverSearchButton\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverProviderBox\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverFeedBox\"");
        xaml.Should().Contain(
            "ItemsSource=\"{x:Bind ViewModel.MangaDiscoverSections, Mode=OneWay}\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverSearchResults\"");
        xaml.Should().Contain("MangaDiscoverSectionTemplate");
        code.Should().Contain("MangaHomeSection.Discover");
        code.Should().Contain("InitializeBrowseAsync(section)");
        xaml.Should().Contain(
            "ItemsSource=\"{x:Bind ViewModel.BrowseBooks, Mode=OneWay}\"");
        xaml.Should().Contain(
            "ItemTemplate=\"{StaticResource RemoteMangaBookTemplate}\"");
        settings.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaSourcesConnectButton\"");
        settings.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonRepositoriesList\"");
        settings.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonAddRepositoryButton\"");
        settings.Should().Contain(
            "Command=\"{Binding AddMihonRepositoryCommand}\"");
        settings.Should().Contain(
            "Command=\"{Binding EditCommand}\"");
        settings.Should().Contain(
            "Command=\"{Binding RemoveCommand}\"");
        settings.Should().NotContain("MihonRepositoryUrlTextBox");
        settings.Should().Contain("x:Uid=\"MihonBundledRuntimeInfoBar\"");
        settings.Should().NotContain("MihonBridgeUrlTextBox");
        settings.Should().NotContain("MihonJavaPathTextBox");
        settings.Should().NotContain("MihonServerJarPathTextBox");
        settings.Should().NotContain("MihonConnectButton");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"BrowseSourcesList\"");
        xaml.Should().Contain(
            "ItemsSource=\"{Binding Source={StaticResource GroupedBrowseSources}}\"");
        settings.Should().Contain("x:Uid=\"MangaSourcesBoundaryInfoBar\"");
        xaml.Should().NotContain("BrowseMangaSourcesTab");
        xaml.Should().NotContain("BrowseMangaExtensionsTab");
        xaml.Should().NotContain("BrowseSourceSettingsTab");
        xaml.Should().NotContain("MihonBrowseSourceComboBox");
        library.Should().NotContain("BrowseMangaSourcesTab");
        library.Should().NotContain("MangaSourcesMihonProviderButton");
        xaml.Should().NotContain("OpenSuwayomiButton_Click");
        code.Should().NotContain("SuwayomiBrowserDialog");
        code.Should().NotContain("ContentDialog");
    }

    [Fact]
    public void MangaBrowse_PreloadsFollowingPagesForSuwayomiAndMihon()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml.cs"));
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaLibraryPageViewModel.cs"));

        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaBrowseBookshelf\"");
        xaml.Should().Contain(
            "ElementPrepared=\"MangaBrowseBookshelf_ElementPrepared\"");
        xaml.Should().Contain(
            "ViewModel.IsBrowseLoadingMore");
        code.Should().Contain("ViewModel.BrowseBooks.Count - 6");
        code.Should().Contain("ViewModel.LoadNextBrowsePageCommand");
        viewModel.Should().Contain("page.HasNextPage && additions.Count > 0");
        viewModel.Should().Contain("requestedPage + 1");
        viewModel.Should().Contain("BrowseSuwayomiAsync(_activeBrowseQuery, append: true)");
        viewModel.Should().Contain("BrowseMihonAsync(_activeBrowseQuery, append: true)");
        viewModel.Should().Contain("BrowseBooks.Add(item)");
    }

    [Fact]
    public void MangaReader_UsesOneNativeCanvasForAllLayoutsAndSharedPopup()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Manga", "MangaReaderWindow.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Manga", "MangaReaderWindow.xaml.cs"));

        xaml.Should().Contain("x:Name=\"PagedScrollViewer\"");
        xaml.Should().Contain("x:Name=\"ContinuousScrollViewer\"");
        xaml.Should().Contain("MangaPageView");
        xaml.Should().Contain("x:Name=\"MangaPopupCanvas\"");
        xaml.Should().Contain("PointerWheelChanged=\"PagedScrollViewer_PointerWheelChanged\"");
        code.Should().Contain("new DictionaryPopupOverlay()");
        code.Should().Contain("DictionaryPopupCanvasInputMode.VisibleHostsOnly");
        code.Should().Contain("TextSelectionResolver.LookupCandidate(");
        code.Should().Contain("SentenceOffset = candidate.Utf16Start");
        code.Should().Contain("MangaPagePath = pagePath");
        code.Should().Contain("ActivateGlobalAsync(");
        code.Should().Contain("now - _lastWheelTurnAt < 250");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"MangaZoomSlider\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"MangaPageNumberBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"MangaGoogleOcrButton\"");
        code.Should().Contain("VirtualKeyModifiers.Control");
        code.Should().Contain("VirtualKey.Left");
        code.Should().Contain("VirtualKey.Right");
        code.Should().Contain("PageHorizontalAlignment = pages.Count > 1");
        code.Should().Contain("HorizontalAlignment.Right");
        code.Should().Contain("HorizontalAlignment.Left");

        var pageViewCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Manga", "MangaPageView.xaml.cs"));
        pageViewCode.Should().Contain("PageImage.Width = image.PixelWidth * scale");
        pageViewCode.Should().Contain(
            "HorizontalAlignment.Right => PageRoot.ActualWidth - renderedWidth");
    }

    [Fact]
    public void MangaPointerInteraction_SeparatesLookupPanAndContextMenu()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Manga", "MangaPageView.xaml.cs"));

        code.Should().Contain("IsLeftButtonPressed");
        code.Should().Contain("IsRightButtonPressed");
        code.Should().Contain("distance >= 4");
        code.Should().Contain("PanRequested?.Invoke");
        code.Should().Contain("ContextMenuRequested?.Invoke");
    }

    [Fact]
    public void MangaLibrary_ExposesGoogleOcrAndUserManagedSuwayomiBoundary()
    {
        var browse = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var settings = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Views",
                "Manga",
                "MangaSourceSettingsContent.xaml"));
        var service = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "SuwayomiService.cs"));

        settings.Should().Contain(
            "Mihon APKs execute third-party code in the bundled Java sidecar");
        settings.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaSourcesConnectButton\"");
        service.Should().Contain("PasswordVault");
        service.Should().Contain("/api/v1/");
        service.Should().NotContain("Sqlite");
    }

    [Fact]
    public void MangaLibrary_ReusesOneRemoteBookshelfTemplateForSuwayomiAndMihon()
    {
        var library = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var extensionBrowser = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Views",
                "Manga",
                "MihonExtensionBrowser.xaml"));
        var sourceItemViewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Components",
                "MangaBrowseSourceItemViewModel.cs"));
        var mihon = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "MihonExtensionService.cs"));
        var pageViewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaLibraryPageViewModel.cs"));

        library.Should().Contain("x:Key=\"RemoteMangaBookTemplate\"");
        xaml.Should().Contain("x:Key=\"RemoteMangaBookTemplate\"");
        xaml.Should().Contain("x:DataType=\"components:RemoteMangaLibraryItemViewModel\"");
        xaml.Should().NotContain(
            "AutomationProperties.AutomationId=\"BrowseSourceSettingsTab\"");
        xaml.Should().Contain(
            "<manga:MangaSourceSettingsContent");
        xaml.Should().NotContain("<Button.Flyout>");
        xaml.Should().Contain(
            "ScrollViewer.VerticalScrollMode=\"Enabled\"");
        xaml.Should().Contain(
            "Source=\"{x:Bind IconImage, Mode=OneWay}\"");
        xaml.Should().Contain(
            "ColumnDefinitions=\"Auto,*,Auto,Auto\"");
        xaml.Should().Contain("Command=\"{x:Bind RemoveCommand}\"");
        xaml.Should().Contain("RemoveAutomationId");
        xaml.Should().Contain("<manga:MihonExtensionBrowser");
        xaml.Should().Contain(
            "Source=\"{Binding BrowseSourceGroups}\"");
        extensionBrowser.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonRepositorySearchTextBox\"");
        extensionBrowser.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonRepositorySourcesList\"");
        extensionBrowser.Should().Contain("Command=\"{x:Bind InstallCommand}\"");
        extensionBrowser.Should().Contain("Command=\"{x:Bind RemoveCommand}\"");
        extensionBrowser.Should().Contain("RemoveAutomationId");
        extensionBrowser.Should().Contain("SymbolIcon Symbol=\"Delete\"");
        extensionBrowser.Should().Contain(
            "ItemsSource=\"{Binding Source={StaticResource GroupedMihonRepositorySources}}\"");
        extensionBrowser.Should().Contain("<ListView.GroupStyle>");
        extensionBrowser.Should().Contain(
            "ScrollViewer.VerticalScrollMode=\"Enabled\"");
        extensionBrowser.Should().Contain(
            "Source=\"{x:Bind IconImage, Mode=OneWay}\"");
        sourceItemViewModel.Should().Contain("new BitmapImage");
        sourceItemViewModel.Should().Contain("UriKind.Absolute");
        sourceItemViewModel.Should().Contain("RemoveCommand");
        extensionBrowser.Should().Contain("ImageFailed=\"MihonRepositorySourceIcon_ImageFailed\"");
        extensionBrowser.Should().Contain("IsEnabled=\"{x:Bind CanRemove, Mode=OneTime}\"");
        extensionBrowser.Should().NotContain("Visibility=\"{x:Bind IsInstalled");
        mihon.Should().Contain("GetDetectedRasterImageExtension");
        pageViewModel.Should().Contain("LoadMihonInstalledSourceIconAsync");
        pageViewModel.Should().Contain("RemoveMihonInstalledSourceAsync");
        pageViewModel.Should().Contain("source.IconDownloadUrl");
        extensionBrowser.Should().NotContain("MihonRepositorySourceComboBox");
        extensionBrowser.Should().NotContain(
            "MihonCompatibleSourcesOnlyCheckBox");
        mihon.Should().Contain("\"getPopularManga\"");
        mihon.Should().Contain("\"getSearchManga\"");
        mihon.Should().Contain("\"getChapterList\"");
        mihon.Should().Contain("\"getPageList\"");
        mihon.Should().Contain("NormalizeBridgeUri");
        mihon.Should().Contain(
            "--add-opens=java.base/java.lang=ALL-UNNAMED");
        mihon.Should().Contain(
            "startInfo.ArgumentList.Add(\"-noverify\")");
        mihon.Should().Contain(
            "Path.Combine(AppContext.BaseDirectory, \"MihonBridge\")");
        mihon.Should().Contain("ResolveBundledRuntime");
        mihon.Should().Contain("RemoveAsync(");
        mihon.Should().Contain("HasRasterImageSignature");
        mihon.Should().Contain(
            "headers as optional");
    }

    [Fact]
    public void MangaBuild_PackagesPinnedMExtensionServerWithoutUserDownload()
    {
        var project = File.ReadAllText(
            Path.Combine(ProjectRoot, "Niratan.csproj"));
        var repositoryRoot = Directory.GetParent(ProjectRoot)!.FullName;
        var ensureScript = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "scripts",
                "Ensure-MExtensionServer.ps1"));

        project.Should().Contain(
            "<MExtensionServerVersion>1.0.4</MExtensionServerVersion>");
        project.Should().Contain("Ensure-MExtensionServer.ps1");
        project.Should().Contain("$(TargetDir)MihonBridge");
        project.Should().Contain("$(PublishDir)MihonBridge");
        ensureScript.Should().Contain(
            "4bdd8e068914a769b4ff132080210d2a8be806e9c401a577dd700cb662a302ee");
        ensureScript.Should().Contain(
            "edf198c73f7ffa54e356396833d4c0a34d86366cd59aa0edae9d1559e7960d7c");
        ensureScript.Should().Contain(
            "[System.Security.Cryptography.SHA256]::Create()");
        ensureScript.Should().Contain(
            "New-Object System.Text.UTF8Encoding($false)");
        ensureScript.Should().Contain("LICENSE-MPL-2.0.txt");
        File.Exists(Path.Combine(
            repositoryRoot,
            "ThirdParty",
            "MExtensionServer",
            "overlay",
            "src",
            "mextensionserver",
            "controller",
            "DalvikHandler.java")).Should().BeTrue();
        File.Exists(Path.Combine(
            repositoryRoot,
            "ThirdParty",
            "MExtensionServer",
            "overlay",
            "src",
            "mextensionserver",
            "impl",
            "R8ConstructorFixer.java")).Should().BeTrue();
    }

    [Fact]
    public void MangaStorage_IsJsonOnlyAndDoesNotReferenceSqlite()
    {
        var service = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "MangaLibraryService.cs"));
        var store = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "MangaCatalogStore.cs"));

        service.Should().Contain("IMangaCatalogStore");
        store.Should().Contain("JsonSerializer");
        store.Should().Contain("File.Replace");
        service.Should().NotContain("Sqlite");
        store.Should().NotContain("Sqlite");
        service.Should().NotContain("Services.Novels");
    }

    [Fact]
    public void MangaUi_ProvidesChineseResourcesForReaderOcrAndSuwayomi()
    {
        var library = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var reader = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Manga", "MangaReaderWindow.xaml"));
        var navigation = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml"));
        var chinese = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        library.Should().Contain("x:Uid=\"MangaLibraryTitleText\"");
        navigation.Should().Contain("x:Uid=\"MangaNavigationItem\"");
        reader.Should().Contain("x:Uid=\"MangaReaderLayoutButton\"");
        reader.Should().Contain("x:Uid=\"MangaReaderOcrButton\"");
        navigation.Should().NotContain("x:Uid=\"BrowseNavigationItem\"");
        var browse = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        library.Should().Contain("x:Uid=\"MangaLibraryTopSourcesNavItem\"");
        library.Should().Contain("x:Uid=\"MangaLibraryTopDiscoverNavItem\"");
        library.Should().Contain("x:Uid=\"MangaLibraryTopExtensionsNavItem\"");
        library.Should().Contain("x:Uid=\"MangaLibraryTopSettingsNavItem\"");
        browse.Should().NotContain("BrowseMangaSourcesTab");
        browse.Should().NotContain("BrowseMangaExtensionsTab");
        browse.Should().NotContain("BrowseSourceSettingsTab");
        chinese.Should().Contain(
            "name=\"MangaLibraryTitleText.Text\" xml:space=\"preserve\"><value>漫画</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryImportFileButton.Label\" xml:space=\"preserve\"><value>导入漫画</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryOnlineSurfaceButton.Content\" xml:space=\"preserve\"><value>在线</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryTopHomeNavItem.Content\" xml:space=\"preserve\"><value>漫画库</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryTopDiscoverNavItem.Content\" xml:space=\"preserve\"><value>发现</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryTopSourcesNavItem.Content\" xml:space=\"preserve\"><value>漫画源</value>");
        chinese.Should().Contain(
            "name=\"MangaDiscoverSearchButton.Content\" xml:space=\"preserve\"><value>搜索</value>");
        chinese.Should().Contain(
            "name=\"MangaDiscoverEmptyText.Text\" xml:space=\"preserve\"><value>选择 Bangumi 或 AniList 来发现漫画。</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryTopExtensionsNavItem.Content\" xml:space=\"preserve\"><value>漫画扩展</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryTopSettingsNavItem.Content\" xml:space=\"preserve\"><value>来源设置</value>");
        chinese.Should().Contain(
            "name=\"MihonRepositoriesHeader.Text\" xml:space=\"preserve\"><value>扩展仓库</value>");
        chinese.Should().Contain(
            "name=\"MihonAddRepositoryButton.Content\" xml:space=\"preserve\"><value>添加仓库</value>");
        chinese.Should().Contain(
            "name=\"MangaOcrPausedStatus\" xml:space=\"preserve\"><value>文字识别已暂停，已完成页面仍可立即查词。</value>");
        chinese.Should().Contain(
            "name=\"MangaSourcesConnectButton.Content\" xml:space=\"preserve\"><value>连接</value>");
    }

    [Fact]
    public void MangaLibrary_UsesVideoAlignedTopDiscoverSegment()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "MangaLibraryPage.xaml.cs"));

        xaml.Should().Contain(
            "PaneDisplayMode=\"Top\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibrarySourcesNavItem\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaLibraryDiscoverNavItem\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaDiscoverPage\"");
        xaml.Should().Contain(
            "Tag=\"Discover\"");
        xaml.Should().Contain(
            "Tag=\"Browse\"");
        code.Should().Contain("ShowMangaDiscoverPageAsync");
        code.Should().Contain("typeof(BrowsePage)");
        code.Should().Contain("MangaDiscoverPageHostFrame.Content is BrowsePage browsePage");
        code.Should().Contain("browsePage.ViewModel.SelectBrowseSectionAsync(section)");
        code.Should().NotContain("MangaDiscoverPageHostFrame.Content = null");
        xaml.Should().Contain(
            "Navigated=\"MangaDiscoverPageHostFrame_Navigated\"");
        code.Should().Contain(
            "_browsePageViewModel.PropertyChanged += BrowsePageViewModel_PropertyChanged");
        code.Should().Contain(
            "_browsePageViewModel.PropertyChanged -= BrowsePageViewModel_PropertyChanged");
        code.Should().Contain(
            "ViewModel.SelectedSection = _browsePageViewModel.SelectedSection");
        code.Should().Contain(
            "SetSelectedNavigationItem(_browsePageViewModel.SelectedSection)");
    }

    [Fact]
    public void MangaDiscover_UsesNetworkPosterFeedsAndDirectExtensionCommands()
    {
        var browse = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var browseCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml.cs"));
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaLibraryPageViewModel.cs"));
        var discoveryService = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Services",
                "Manga",
                "MangaDiscoveryService.cs"));
        var item = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Components",
                "MangaDiscoveryCardViewModel.cs"));

        browse.Should().Contain("MangaDiscoverSectionTemplate");
        browse.Should().Contain("PosterImage, Mode=OneWay");
        browse.Should().Contain("SourceText, Mode=OneTime");
        browse.Should().Contain("MangaDiscoverRefreshButton");
        browse.Should().Contain("MangaDiscoverContentScrollViewer_ViewChanged");
        browse.Should().Contain("MangaDiscoveryCard_ElementPrepared");
        browseCode.Should().Contain("sender.ItemsSourceView.GetAt(args.Index)");
        browse.Should().Contain("MangaDiscoverSearchTextBox_KeyDown");
        viewModel.Should().Contain("LoadMangaDiscoveryHomeAsync");
        viewModel.Should().Contain("LoadMangaDiscoverySectionAsync");
        viewModel.Should().Contain("GetPageAsync(");
        viewModel.Should().Contain("_mangaDiscovery.SearchAsync");
        viewModel.Should().Contain("OpenMangaDiscoveryItemAsync");
        viewModel.Should().Contain("FindMihonMangaAsync");
        viewModel.Should().Contain("ShowMihonMangaDetailsAsync");
        discoveryService.Should().Contain("api.bgm.tv");
        discoveryService.Should().Contain("graphql.anilist.co");
        discoveryService.Should().Contain("Discovery");
        item.Should().Contain("SourceText");
        item.Should().Contain("SetPosterPath");
    }

    [Fact]
    public void MangaDiscover_ReusesVideoAlignedCardTemplateForSectionsAndSearch()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var template = document.Descendants(presentation + "DataTemplate")
            .Single(element =>
                (string?)element.Attribute(x + "Key") == "MangaDiscoveryCardTemplate");
        var button = template.Descendants(presentation + "Button").Single();
        ((string?)button.Attribute("Width")).Should().Be("170");
        ((string?)button.Attribute("Height")).Should().Be("350");

        var poster = template.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute("Height") == "238");
        var title = template.Descendants(presentation + "TextBlock")
            .Single(element => ((string?)element.Attribute("Text"))?.Contains("Title") == true);
        title.Ancestors().Should().NotContain(poster);
        ((string?)title.Attribute("Height")).Should().Be("48");
        ((string?)title.Attribute("MaxLines")).Should().Be("2");
        ((string?)title.Attribute("Style")).Should().Be("{StaticResource BodyStrongTextBlockStyle}");

        var facts = template.Descendants(presentation + "TextBlock")
            .Single(element => ((string?)element.Attribute("Text"))?.Contains("FactsText") == true);
        ((string?)facts.Attribute("Height")).Should().Be("24");
        ((string?)facts.Attribute("Foreground"))
            .Should().Be("{ThemeResource TextFillColorSecondaryBrush}");
        var source = template.Descendants(presentation + "TextBlock")
            .Single(element => ((string?)element.Attribute("Text"))?.Contains("SourceText") == true);
        ((string?)source.Attribute("Height")).Should().Be("24");
        ((string?)source.Attribute("Foreground"))
            .Should().Be("{ThemeResource AccentTextFillColorPrimaryBrush}");

        template.Descendants(presentation + "LinearGradientBrush").Should().BeEmpty();
        template.ToString().Should().NotContain("AccentFillColorDefaultBrush");
        xaml.Split("ItemTemplate=\"{StaticResource MangaDiscoveryCardTemplate}\"")
            .Should().HaveCount(3);
        xaml.Should().Contain("MinItemHeight=\"350\"");
    }

    [Fact]
    public void MangaDiscover_SearchToolbarStaysOutsideVerticalContentScroller()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var toolbar = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "MangaDiscoverSearchToolbar");
        var contentScroller = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "MangaDiscoverContentScrollViewer");
        var searchBox = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "MangaDiscoverSearchBox");

        toolbar.Descendants().Should().Contain(searchBox);
        contentScroller.Descendants().Should().NotContain(searchBox);
        ((string?)toolbar.Attribute("Grid.Row")).Should().Be("0");
        ((string?)contentScroller.Attribute("Grid.Row")).Should().Be("1");
    }

    [Fact]
    public void MangaDiscover_RecommendationWheelMatchesVideoDiscoverBehavior()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml.cs"));

        xaml.Should().Contain("Loaded=\"HorizontalMangaList_Loaded\"");
        xaml.Should().Contain("Unloaded=\"HorizontalMangaList_Unloaded\"");
        code.Should().Contain("VirtualKeyModifiers.Shift");
        code.Should().Contain("verticalScrollViewer.ChangeView(");
        code.Should().Contain("horizontalScrollViewer.ChangeView(");
        code.Should().Contain("disableAnimation: false");
        code.Should().Contain("RemoveHandler(");
    }

    [Fact]
    public void MangaRemoteDetails_ExposeInstalledExtensionSwitchBeforeChapters()
    {
        var details = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Views",
                "Manga",
                "RemoteMangaDetailView.xaml"));
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaLibraryPageViewModel.cs"));
        var component = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Components",
                "RemoteMangaDetailViewModel.cs"));

        details.Should().Contain(
            "AutomationProperties.AutomationId=\"MangaRemoteDetailsExtensions\"");
        details.Should().Contain(
            "<ComboBox x:Uid=\"MangaRemoteDetailsExtensionsComboBox\"");
        details.Should().Contain(
            "ItemsSource=\"{Binding SelectedRemoteMangaDetails.ExtensionOptions, Mode=OneWay}\"");
        details.Should().Contain(
            "SelectedItem=\"{Binding SelectedRemoteMangaDetails.SelectedExtension, Mode=OneWay}\"");
        details.Should().Contain(
            "SelectionChanged=\"RemoteMangaExtensionsComboBox_SelectionChanged\"");
        details.Should().NotContain("<ToggleButton MinWidth=\"150\"");
        viewModel.Should().Contain("FindMihonMangaAsync");
        viewModel.Should().Contain("SelectRemoteMangaExtensionAsync");
        component.Should().Contain("SetExtensionOptions");
        component.Should().Contain("SelectedExtension");
        component.Should().Contain("SelectedExtensionId");
        component.Should().Contain("RemoteMangaExtensionOptionViewModel");
    }
}
