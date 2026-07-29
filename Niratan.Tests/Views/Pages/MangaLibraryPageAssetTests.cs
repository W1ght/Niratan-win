using FluentAssertions;

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
        xaml.Should().Contain("AutomationProperties.AutomationId=\"BrowseNavItem\"");
        xaml.Should().Contain("Tag=\"Niratan.Views.Pages.BrowsePage\"");
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
        viewModel.Should().NotContain("supportsOnlineLibrary: false");
        viewModel.Should().NotContain("OpenOnlineMangaAsync");
        viewModel.Should().NotContain("OpenMihonMangaAsync");
    }

    [Fact]
    public void Browse_IsATopLevelModuleWithSourceAndExtensionTabs()
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

        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"BrowseMangaSourcesTab\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"BrowseMangaExtensionsTab\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"BrowseSourceSettingsTab\"");
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
        var mihon = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Manga", "MihonExtensionService.cs"));

        library.Should().Contain("x:Key=\"RemoteMangaBookTemplate\"");
        xaml.Should().Contain("x:Key=\"RemoteMangaBookTemplate\"");
        xaml.Should().Contain("x:DataType=\"components:RemoteMangaLibraryItemViewModel\"");
        xaml.Should().Contain(
            "AutomationProperties.AutomationId=\"BrowseSourceSettingsTab\"");
        xaml.Should().Contain(
            "<manga:MangaSourceSettingsContent");
        xaml.Should().NotContain("<Button.Flyout>");
        xaml.Should().Contain(
            "ScrollViewer.VerticalScrollMode=\"Enabled\"");
        xaml.Should().Contain(
            "Source=\"{x:Bind IconImage, Mode=OneWay}\"");
        xaml.Should().Contain("<manga:MihonExtensionBrowser");
        xaml.Should().Contain(
            "Source=\"{Binding BrowseSourceGroups}\"");
        extensionBrowser.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonRepositorySearchTextBox\"");
        extensionBrowser.Should().Contain(
            "AutomationProperties.AutomationId=\"MihonRepositorySourcesList\"");
        extensionBrowser.Should().Contain("Command=\"{x:Bind InstallCommand}\"");
        extensionBrowser.Should().Contain(
            "ItemsSource=\"{Binding Source={StaticResource GroupedMihonRepositorySources}}\"");
        extensionBrowser.Should().Contain("<ListView.GroupStyle>");
        extensionBrowser.Should().Contain(
            "ScrollViewer.VerticalScrollMode=\"Enabled\"");
        extensionBrowser.Should().Contain(
            "Source=\"{x:Bind IconImage, Mode=OneWay}\"");
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
        navigation.Should().Contain("x:Uid=\"BrowseNavigationItem\"");
        var browse = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "BrowsePage.xaml"));
        browse.Should().Contain("x:Uid=\"BrowseMangaSourcesTab\"");
        browse.Should().Contain("x:Uid=\"BrowseMangaExtensionsTab\"");
        browse.Should().Contain("x:Uid=\"BrowseSourceSettingsTab\"");
        chinese.Should().Contain(
            "name=\"MangaLibraryTitleText.Text\" xml:space=\"preserve\"><value>漫画</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryImportFileButton.Label\" xml:space=\"preserve\"><value>导入漫画</value>");
        chinese.Should().Contain(
            "name=\"MangaLibraryOnlineSurfaceButton.Content\" xml:space=\"preserve\"><value>在线</value>");
        chinese.Should().Contain(
            "name=\"BrowseNavigationItem.Content\" xml:space=\"preserve\"><value>浏览</value>");
        chinese.Should().Contain(
            "name=\"BrowseMangaSourcesTab.Content\" xml:space=\"preserve\"><value>漫画源</value>");
        chinese.Should().Contain(
            "name=\"BrowseMangaExtensionsTab.Content\" xml:space=\"preserve\"><value>漫画扩展</value>");
        chinese.Should().Contain(
            "name=\"BrowseSourceSettingsTab.Content\" xml:space=\"preserve\"><value>来源设置</value>");
        chinese.Should().Contain(
            "name=\"MihonRepositoriesHeader.Text\" xml:space=\"preserve\"><value>扩展仓库</value>");
        chinese.Should().Contain(
            "name=\"MihonAddRepositoryButton.Content\" xml:space=\"preserve\"><value>添加仓库</value>");
        chinese.Should().Contain(
            "name=\"MangaOcrPausedStatus\" xml:space=\"preserve\"><value>文字识别已暂停，已完成页面仍可立即查词。</value>");
        chinese.Should().Contain(
            "name=\"MangaSourcesConnectButton.Content\" xml:space=\"preserve\"><value>连接</value>");
    }
}
