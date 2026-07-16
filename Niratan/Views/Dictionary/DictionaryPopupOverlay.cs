using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Niratan.Enums;
using Niratan.Models.Anki;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Anki;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;
using Serilog;

namespace Niratan.Views.Dictionary;

public readonly record struct DictionaryPopupHostBounds(
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record DictionaryPopupExternalChildRequest(
    DictionaryPopupRequest PopupRequest,
    double AnchorX,
    double AnchorY,
    double AnchorWidth,
    double AnchorHeight);

public enum DictionaryPopupCanvasInputMode
{
    ModalSurface,
    VisibleHostsOnly,
}

public enum DictionaryPopupShowCancellationResult
{
    NoOwnership,
    Cancelled,
    CommitAccepted,
}

public sealed class DictionaryPopupOverlay : IDisposable
{
    private sealed record DictionaryPopupRootContext(
        string? TraceId,
        Dictionary<string, string> Styles,
        DictionaryDisplaySettings DisplaySettings,
        ThemeMode Theme,
        AudioSettings AudioSettings,
        AnkiSettings AnkiSettings,
        AnkiMiningContext MiningContext,
        bool IsVertical);

    private const double ScreenBorderPadding = DictionaryPopupLayoutCalculator.ScreenBorderPadding;
    private const int RootPopupZIndex = 10;
    private const int ChildPopupZIndexBase = 20;
    private const int PopupZIndexStep = 10;
    private const int PrewarmedChildHostCount = 1;
    private const int NestedLookupMaxResults = 1;

    private Canvas _canvas;
    private readonly IDictionaryLookupService _lookupService;
    private readonly List<DictionaryLookupPopup> _childHosts = [];
    private readonly List<DictionaryLookupPopup> _childHostPool = [];
    private readonly SemaphoreSlim _redirectSemaphore = new(1, 1);
    private long _redirectVersion;
    private long _dismissVersion;
    private string _lastRedirectQuery = "";
    private DictionaryLookupPopup? _lastRedirectParent;
    private DictionaryLookupPopup _rootHost = null!;
    private bool _rootWarm;
    private bool _rootVisible;
    private bool _dismissPending;
    private Panel? _embeddedPanel;
    private Panel? _inPlaceDialogHost;
    private XamlRoot? _currentXamlRoot;
    private Task? _prewarmTask;
    private double _rootReadyOpacity = 1;
    private bool _useStandaloneWindowVisuals;
    private bool _useNakedFloatingWindowVisuals;
    private bool _useExternalChildWindows;
    private bool _useImmediateOpacityTransitions;
    private readonly DictionaryPopupRootStateCoordinator<
        DictionaryPopupRootContext,
        DictionaryPopupAnchorRect,
        DictionaryPopupLayoutResult> _rootStateCoordinator = new();

    public event EventHandler? Dismissed;
    public event EventHandler<DictionaryPopupContentCommittedEventArgs>? RootContentCommitted;
    public event EventHandler<DictionaryPopupContentCommittedEventArgs>? RootContentAborted;
    public event EventHandler<DictionaryPopupShowDroppedEventArgs>? RootShowDropped;
    public event EventHandler? VisibleHostBoundsChanged;
    public event EventHandler<DictionaryPopupExternalChildRequest>? ExternalChildRequested;
    public event EventHandler? ExternalTapInsideRequested;

    public DictionaryPopupOverlay()
    {
        _lookupService = App.GetService<IDictionaryLookupService>();

        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
            Visibility = Visibility.Visible,
        };
        _canvas.PointerPressed += OnOverlayPointerPressed;
    }

    /// <summary>Use the given canvas as the overlay surface (reader page). Must be called before PrewarmAsync.</summary>
    public void UseCanvas(
        Canvas overlayCanvas,
        DictionaryPopupCanvasInputMode inputMode = DictionaryPopupCanvasInputMode.ModalSurface)
    {
        if (!ReferenceEquals(_canvas, overlayCanvas))
        {
            _canvas.PointerPressed -= OnOverlayPointerPressed;
            _canvas = overlayCanvas;
            _canvas.PointerPressed += OnOverlayPointerPressed;
        }

        _canvas.Background = inputMode switch
        {
            DictionaryPopupCanvasInputMode.ModalSurface =>
                new SolidColorBrush(Colors.Transparent),
            DictionaryPopupCanvasInputMode.VisibleHostsOnly => null,
            _ => throw new ArgumentOutOfRangeException(nameof(inputMode)),
        };
        _canvas.IsHitTestVisible = false;
        _canvas.Visibility = Visibility.Visible;
    }

    /// <summary>Embed the root popup directly in a page panel (standalone lookup).</summary>
    public void EmbedRoot(Panel panel)
    {
        _embeddedPanel = panel;
    }

    /// <summary>
    /// Hosts dialogs inside an existing top-layer visual tree. Video uses this
    /// to keep popup dialogs above the native libmpv child HWND.
    /// </summary>
    public void UseInPlaceDialogHost(Panel? host)
    {
        _inPlaceDialogHost = host;
        if (_rootWarm)
            _rootHost.SetInPlaceDialogHost(host);

        foreach (var child in _childHostPool)
            child.SetInPlaceDialogHost(host);
    }

    public void UseStandaloneWindowVisuals()
    {
        _useStandaloneWindowVisuals = true;
        if (_rootWarm)
            _rootHost.UseStandaloneWindowVisuals();
    }

    public void UseNakedFloatingWindowVisuals()
    {
        _useNakedFloatingWindowVisuals = true;
        if (_rootWarm)
            _rootHost.UseNakedFloatingWindowVisuals();

        foreach (var child in _childHostPool)
            child.UseNakedFloatingWindowVisuals();
    }

    public void UseExternalChildWindows()
    {
        _useExternalChildWindows = true;
    }

    public void UseImmediateOpacityTransitions()
    {
        _useImmediateOpacityTransitions = true;
        if (_rootWarm)
            _rootHost.UseImmediateOpacityTransitions();

        foreach (var child in _childHostPool)
            child.UseImmediateOpacityTransitions();
    }

    public async Task PrewarmAsync(XamlRoot xamlRoot, ThemeMode themeMode = ThemeMode.System)
    {
        if (_rootWarm)
            return;

        if (_prewarmTask is not null)
        {
            await _prewarmTask;
            return;
        }

        _prewarmTask = PrewarmCoreAsync(xamlRoot, themeMode);
        try
        {
            await _prewarmTask;
        }
        finally
        {
            if (!_rootWarm)
                _prewarmTask = null;
        }
    }

    private async Task PrewarmCoreAsync(XamlRoot xamlRoot, ThemeMode themeMode)
    {
        _currentXamlRoot = xamlRoot;
        CollapseCanvasBounds();
        _rootHost = CreateHost();
        if (_useStandaloneWindowVisuals)
            _rootHost.UseStandaloneWindowVisuals();
        else if (_useNakedFloatingWindowVisuals)
            _rootHost.UseNakedFloatingWindowVisuals();
        if (_useImmediateOpacityTransitions)
            _rootHost.UseImmediateOpacityTransitions();
        _rootHost.SetReadyOpacity(_rootReadyOpacity);
        _rootHost.RedirectRequested += OnRootRedirectRequested;
        _rootHost.TapOutsideRequested += OnRootTapOutsideRequested;
        _rootHost.DismissRequested += OnPopupDismissRequested;
        _rootHost.Scrolled += OnRootScrolled;
        _rootHost.ContentCommitted += OnRootContentCommitted;
        _rootHost.ContentCommitAborted += OnRootContentCommitAborted;
        _rootHost.QueuedShowDropped += OnRootShowDropped;

        if (_embeddedPanel != null)
        {
            _embeddedPanel.Children.Add(_rootHost.VisualRoot);
        }

        await _rootHost.WarmAsync(themeMode);
        if (!_useExternalChildWindows)
            await PrewarmChildHostPoolAsync(PrewarmedChildHostCount, themeMode);

        _rootWarm = true;
        Log.Information("[DictOverlay] Root popup prewarmed (embedded={Embedded})", _embeddedPanel != null);
    }

    private async Task PrewarmChildHostPoolAsync(int targetCount, ThemeMode themeMode)
    {
        if (targetCount <= 0)
            return;

        var sw = Stopwatch.StartNew();
        while (_childHostPool.Count < targetCount)
        {
            var child = CreateChildHost();
            EnsureHostOnCanvas(child);
            await child.WarmAsync(themeMode);
        }

        Log.Information(
            "[LookupTrace] trace=- child popup pool prewarmed in {Ms}ms target={TargetCount} pool={PoolCount}",
            sw.ElapsedMilliseconds,
            targetCount,
            _childHostPool.Count);
    }

    public async Task ShowLookupAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        double pointX, double pointY,
        double selectionWidth, double selectionHeight,
        XamlRoot xamlRoot,
        bool isVertical,
        ThemeMode themeMode = ThemeMode.System,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        AnkiMiningContext? miningContext = null,
        string? traceId = null,
        CancellationToken cancellationToken = default,
        double? layoutViewportWidth = null,
        double? layoutViewportHeight = null)
    {
        if (results.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _dismissVersion);
        _dismissPending = false;

        var sw = Stopwatch.StartNew();
        Log.Information(
            "[LookupTrace] trace={TraceId} overlay show start entries={EntryCount} styles={StyleCount} embedded={Embedded}",
            traceId ?? "-", results.Count, styles.Count, _embeddedPanel != null);
        var audio = audioSettings ?? App.GetService<ISettingsService>().Current.AudioSettings;
        var anki = ankiSettings ?? App.GetService<ISettingsService>().Current.AnkiSettings;
        var context = new DictionaryPopupRootContext(
            traceId,
            styles,
            displaySettings,
            themeMode,
            audio,
            anki,
            miningContext ?? new AnkiMiningContext(),
            isVertical);
        var anchor = new DictionaryPopupAnchorRect(
            pointX,
            pointY,
            selectionWidth,
            selectionHeight);

        await EnsureWarmAsync(xamlRoot, themeMode);
        cancellationToken.ThrowIfCancellationRequested();
        Log.Information(
            "[LookupTrace] trace={TraceId} overlay warmed in {Ms}ms embedded={Embedded}",
            traceId ?? "-", sw.ElapsedMilliseconds, _embeddedPanel != null);

        ResetRedirectDeduplication();
        var clearSw = Stopwatch.StartNew();
        ClearChildren();
        Log.Information(
            "[LookupTrace] trace={TraceId} overlay cleared children in {Ms}ms total={TotalMs}ms",
            traceId ?? "-", clearSw.ElapsedMilliseconds, sw.ElapsedMilliseconds);

        DictionaryPopupLayoutResult targetLayout;
        if (_embeddedPanel != null)
        {
            targetLayout = new DictionaryPopupLayoutResult(
                0,
                0,
                _embeddedPanel.ActualWidth > 0 ? _embeddedPanel.ActualWidth : double.NaN,
                _embeddedPanel.ActualHeight > 0 ? _embeddedPanel.ActualHeight : double.NaN);
        }
        else
        {
            EnsureHostOnCanvas(_rootHost);
            ApplyPopupZOrder();
            UpdateCanvasBounds(xamlRoot);
            targetLayout = ResolveHostLayout(
                anchor.X,
                anchor.Y,
                anchor.Width,
                anchor.Height,
                isVertical,
                displaySettings,
                isRoot: true,
                viewportWidth: layoutViewportWidth,
                viewportHeight: layoutViewportHeight);
        }

        _rootHost.SetMiningContext(context.MiningContext);
        var injectSw = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        await _rootHost.ShowResultsWarmAsync(
            results,
            styles,
            displaySettings,
            themeMode,
            audio,
            anki,
            traceId: traceId,
            cancellationToken: cancellationToken,
            generationStarted: generation =>
            {
                if (!_rootStateCoordinator.TryStage(
                        generation,
                        traceId,
                        context,
                        anchor,
                        targetLayout))
                {
                    throw new InvalidOperationException(
                        $"Popup generation {generation} could not stage root ownership.");
                }
                if (!_rootHost.HasCommittedContent)
                    ApplyHostLayout(_rootHost, targetLayout);
            });
        cancellationToken.ThrowIfCancellationRequested();
        Log.Information(
            "[LookupTrace] trace={TraceId} root popup content injected in {Ms}ms total={TotalMs}ms entries={EntryCount}",
            traceId ?? "-", injectSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, results.Count);

        Log.Information(
            "[LookupTrace] trace={TraceId} overlay root content staged total={TotalMs}ms",
            traceId ?? "-", sw.ElapsedMilliseconds);

        Log.Information(
            "[Lifecycle] Popup staged: entries={EntryCount} at=({X:F0},{Y:F0}) vertical={Vertical} embedded={Embedded}",
            results.Count, pointX, pointY, isVertical, _embeddedPanel != null);
    }

    private async Task EnsureWarmAsync(XamlRoot xamlRoot, ThemeMode themeMode)
    {
        _currentXamlRoot = xamlRoot;
        await PrewarmAsync(xamlRoot, themeMode);
    }

    private void OnRootRedirectRequested(object? sender, DictionaryPopupRedirectRequest request)
    {
        _ = HandleRedirectAsync(request, sender as DictionaryLookupPopup);
    }

    private void OnRootTapOutsideRequested(object? sender, EventArgs e)
    {
        if (_useExternalChildWindows)
            ExternalTapInsideRequested?.Invoke(this, EventArgs.Empty);
        else
            ClearChildren();
    }

    private void OnRootScrolled(object? sender, EventArgs e)
    {
        if (_useExternalChildWindows)
            ExternalTapInsideRequested?.Invoke(this, EventArgs.Empty);
        else
            ClearChildren();
    }

    private void OnPopupDismissRequested(object? sender, EventArgs e)
    {
        Dismiss();
    }

    private void OnRootContentCommitted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        if (!_rootStateCoordinator.TryCommit(
                e.Generation,
                e.TraceId,
                out var committed))
        {
            return;
        }

        InvalidateRootRedirects();
        var committedLayout = ResolveCommittedRootLayout(committed.Layout);
        ApplyHostLayout(_rootHost, committedLayout);
        _rootVisible = true;
        _canvas.IsHitTestVisible = true;
        if (_embeddedPanel != null)
        {
            _embeddedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _canvas.Visibility = Visibility.Visible;
        }

        RootContentCommitted?.Invoke(this, e);
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private DictionaryPopupLayoutResult ResolveCommittedRootLayout(
        DictionaryPopupLayoutResult stagedLayout)
    {
        if (_embeddedPanel is null)
            return stagedLayout;

        return new DictionaryPopupLayoutResult(
            0,
            0,
            _embeddedPanel.ActualWidth > 0
                ? _embeddedPanel.ActualWidth
                : stagedLayout.Width,
            _embeddedPanel.ActualHeight > 0
                ? _embeddedPanel.ActualHeight
                : stagedLayout.Height);
    }

    private void OnRootContentCommitAborted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        if (_rootStateCoordinator.TryAbort(e.Generation, e.TraceId))
            RootContentAborted?.Invoke(this, e);
    }

    private void OnRootShowDropped(
        object? sender,
        DictionaryPopupShowDroppedEventArgs e)
    {
        RootShowDropped?.Invoke(this, e);
    }

    private async Task HandleRedirectAsync(DictionaryPopupRedirectRequest request, DictionaryLookupPopup? parentHost)
    {
        var totalSw = Stopwatch.StartNew();
        var query = request.Query.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        if (!_rootStateCoordinator.TryGetCommitted(out var rootSnapshot))
            return;
        var parent = parentHost ?? _rootHost;
        if (!TryGetRedirectParentGeneration(
                parent,
                rootSnapshot.Generation,
                out var expectedParentGeneration))
        {
            return;
        }

        var context = rootSnapshot.Context;
        var expectedRootGeneration = rootSnapshot.Generation;
        var expectedRootTraceId = rootSnapshot.TraceId;
        var redirectVersion = Interlocked.Increment(ref _redirectVersion);
        var parentKind = parentHost is null || ReferenceEquals(parentHost, _rootHost) ? "root" : "child";
        var traceId = $"{context.TraceId ?? "popup-redirect"}-child-{redirectVersion}";

        Log.Information(
            "[LookupTrace] trace={TraceId} child redirect received query='{Query}' parent={ParentKind} source={Source} selectedLength={SelectedLength} selectMs={SelectMs:F1} rectMs={RectMs:F1}",
            traceId,
            query,
            parentKind,
            request.Source ?? "-",
            request.SelectedLength,
            request.SelectMs,
            request.RectMs);

        var waitSw = Stopwatch.StartNew();
        await _redirectSemaphore.WaitAsync();
        Log.Information(
            "[LookupTrace] trace={TraceId} child redirect semaphore acquired in {Ms}ms",
            traceId, waitSw.ElapsedMilliseconds);
        try
        {
            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                return;
            }

            var redirectMode = DictionaryPopupRedirectRouter.Resolve(request);
            if (redirectMode == DictionaryPopupRedirectMode.Nested
                && ReferenceEquals(parent, _lastRedirectParent)
                && string.Equals(query, _lastRedirectQuery, StringComparison.Ordinal))
            {
                Log.Debug("[DictOverlay] Ignored duplicate redirect '{Query}' from same parent", query);
                return;
            }

            if (redirectMode == DictionaryPopupRedirectMode.Nested)
            {
                _lastRedirectParent = parent;
                _lastRedirectQuery = query;
            }
            else
            {
                ResetRedirectDeduplication();
            }

            var lookupSw = Stopwatch.StartNew();
            var redirectMaxResults = redirectMode == DictionaryPopupRedirectMode.InPlace
                ? context.DisplaySettings.MaxResults
                : Math.Min(context.DisplaySettings.MaxResults, NestedLookupMaxResults);
            var results = await _lookupService.LookupAsync(
                query,
                redirectMaxResults,
                context.DisplaySettings.ScanLength,
                traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} child redirect lookup finished in {Ms}ms query='{Query}' max={MaxResults} results={Count}",
                traceId, lookupSw.ElapsedMilliseconds, query, redirectMaxResults, results.Count);
            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                return;
            }
            if (results.Count == 0) return;

            if (redirectMode == DictionaryPopupRedirectMode.InPlace)
            {
                CloseChildrenOfParent(parent);
                var applied = await parent.ShowRedirectResultsAsync(
                    results,
                    context.Styles,
                    context.DisplaySettings,
                    context.Theme,
                    expectedParentGeneration,
                    context.AudioSettings,
                    context.AnkiSettings,
                    traceId);
                if (!applied
                    || !IsRedirectCurrent(
                        redirectVersion,
                        expectedRootGeneration,
                        expectedRootTraceId,
                        parent,
                        expectedParentGeneration))
                {
                    return;
                }
                return;
            }

            var highlightSw = Stopwatch.StartNew();
            var highlighted = await HighlightPopupSelectionAsync(
                parent,
                results[0].Matched,
                expectedParentGeneration);
            if (!highlighted
                || !IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                return;
            }
            Log.Information(
                "[LookupTrace] trace={TraceId} child parent highlight finished in {Ms}ms total={TotalMs}ms matched='{Matched}'",
                traceId, highlightSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results[0].Matched);

            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                return;
            }
            var closeSw = Stopwatch.StartNew();
            CloseChildrenOfParent(parent);
            Log.Information(
                "[LookupTrace] trace={TraceId} child previous popups closed in {Ms}ms total={TotalMs}ms",
                traceId, closeSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);

            if (_useExternalChildWindows)
            {
                var anchor = ResolveChildAnchor(parent, request);
                ExternalChildRequested?.Invoke(
                    this,
                    new DictionaryPopupExternalChildRequest(
                        new DictionaryPopupRequest(
                            query,
                            results,
                            context.Styles,
                            context.DisplaySettings,
                            context.Theme,
                            context.AudioSettings,
                            context.AnkiSettings,
                            context.MiningContext,
                            traceId),
                        anchor.X,
                        anchor.Y,
                        anchor.Width,
                        anchor.Height));
                return;
            }

            var hostSw = Stopwatch.StartNew();
            var child = GetReusableChildHost();
            Log.Information(
                "[LookupTrace] trace={TraceId} child host acquired in {Ms}ms warmed={Warmed} pool={PoolCount} active={ActiveCount}",
                traceId, hostSw.ElapsedMilliseconds, child.IsWarmed, _childHostPool.Count, _childHosts.Count);
            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                return;
            }
            EnsureHostOnCanvas(child);
            if (!_childHosts.Contains(child))
                _childHosts.Add(child);
            ApplyPopupZOrder();

            child.SetMiningContext(context.MiningContext);
            var injectSw = Stopwatch.StartNew();
            long? childGeneration = null;
            try
            {
                await child.ShowResultsWarmAsync(
                    results,
                    context.Styles,
                    context.DisplaySettings,
                    context.Theme,
                    context.AudioSettings,
                    context.AnkiSettings,
                    traceId: traceId,
                    generationStarted: generation => childGeneration = generation);
            }
            catch when (!IsRedirectCurrent(
                redirectVersion,
                expectedRootGeneration,
                expectedRootTraceId,
                parent,
                expectedParentGeneration))
            {
                DiscardStaleChild(child, childGeneration, traceId);
                return;
            }

            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                DiscardStaleChild(child, childGeneration, traceId);
                return;
            }
            Log.Information(
                "[LookupTrace] trace={TraceId} child popup content injected in {Ms}ms total={TotalMs}ms entries={EntryCount}",
                traceId, injectSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results.Count);

            // Make sure canvas is visible for child popups
            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                DiscardStaleChild(child, childGeneration, traceId);
                return;
            }
            _canvas.Visibility = Visibility.Visible;
            _canvas.IsHitTestVisible = true;

            var positionSw = Stopwatch.StartNew();
            if (!IsRedirectCurrent(
                    redirectVersion,
                    expectedRootGeneration,
                    expectedRootTraceId,
                    parent,
                    expectedParentGeneration))
            {
                DiscardStaleChild(child, childGeneration, traceId);
                return;
            }
            PositionChildHost(
                child,
                parent,
                request,
                context.DisplaySettings);
            VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
            Log.Information(
                "[LookupTrace] trace={TraceId} child positioned in {Ms}ms total={TotalMs}ms",
                traceId, positionSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);

            Log.Information("[DictOverlay] Child popup reused for redirect '{Query}' total={TotalMs}ms", query, totalSw.ElapsedMilliseconds);
        }
        finally
        {
            _redirectSemaphore.Release();
        }
    }

    private void OnChildRedirectRequested(object? sender, DictionaryPopupRedirectRequest request)
    {
        _ = HandleRedirectAsync(request, sender as DictionaryLookupPopup);
    }

    private void OnChildTapOutsideRequested(object? sender, EventArgs e)
    {
        if (sender is not DictionaryLookupPopup host) return;
        ClearChildrenAfter(host);
    }

    private void OnChildScrolled(object? sender, EventArgs e)
    {
        if (sender is DictionaryLookupPopup host)
            ClearChildrenAfter(host);
    }

    private void RemoveChild(DictionaryLookupPopup host)
    {
        ClearChildrenAfter(host);
        HideChildHost(host);
        _childHosts.Remove(host);
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearChildren()
    {
        foreach (var child in _childHosts)
        {
            HideChildHost(child);
        }
        _childHosts.Clear();
        ApplyPopupZOrder();
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearChildrenAfter(DictionaryLookupPopup parent)
    {
        var parentIndex = _childHosts.IndexOf(parent);
        if (parentIndex < 0)
        {
            ClearChildren();
            return;
        }

        for (var i = _childHosts.Count - 1; i > parentIndex; i--)
        {
            HideChildHost(_childHosts[i]);
            _childHosts.RemoveAt(i);
        }
        ApplyPopupZOrder();
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseChildrenOfParent(DictionaryLookupPopup parent)
    {
        if (ReferenceEquals(parent, _rootHost))
            ClearChildren();
        else
            ClearChildrenAfter(parent);
    }

    private void ResetRedirectDeduplication()
    {
        _lastRedirectQuery = "";
        _lastRedirectParent = null;
    }

    private DictionaryLookupPopup CreateHost()
    {
        var host = new DictionaryLookupPopup();
        host.SetInPlaceDialogHost(_inPlaceDialogHost);
        return host;
    }

    private DictionaryLookupPopup GetReusableChildHost()
    {
        foreach (var host in _childHostPool)
        {
            if (!_childHosts.Contains(host))
                return host;
        }

        var child = CreateChildHost();
        return child;
    }

    private DictionaryLookupPopup CreateChildHost()
    {
        var child = CreateHost();
        if (_useStandaloneWindowVisuals)
            child.UseStandaloneWindowVisuals();
        else if (_useNakedFloatingWindowVisuals)
            child.UseNakedFloatingWindowVisuals();
        if (_useImmediateOpacityTransitions)
            child.UseImmediateOpacityTransitions();
        child.RedirectRequested += OnChildRedirectRequested;
        child.TapOutsideRequested += OnChildTapOutsideRequested;
        child.DismissRequested += OnPopupDismissRequested;
        child.Scrolled += OnChildScrolled;
        _childHostPool.Add(child);
        return child;
    }

    private static void HideChildHost(DictionaryLookupPopup host)
    {
        host.Hide();
    }

    private void EnsureHostOnCanvas(DictionaryLookupPopup host)
    {
        if (!_canvas.Children.Contains(host.VisualRoot))
            _canvas.Children.Add(host.VisualRoot);
    }

    private void ApplyPopupZOrder()
    {
        if (_rootWarm && _embeddedPanel == null)
            Canvas.SetZIndex(_rootHost.VisualRoot, RootPopupZIndex);

        for (var i = 0; i < _childHosts.Count; i++)
            Canvas.SetZIndex(_childHosts[i].VisualRoot, ChildPopupZIndexBase + i * PopupZIndexStep);
    }

    private void OnOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_rootVisible) return;

            var point = e.GetCurrentPoint(_canvas).Position;
            if (IsPointInsideVisibleHost(point.X, point.Y))
                return;

            Dismiss();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictOverlay] OnOverlayPointerPressed crashed");
        }
    }

    private bool IsPointInsideVisibleHost(double x, double y)
    {
        return IsPointInsideHost(_rootHost, x, y)
            || _childHosts.Exists(host => IsPointInsideHost(host, x, y));
    }

    private bool IsPointInsideHost(DictionaryLookupPopup host, double x, double y)
    {
        if (host.VisualRoot.Visibility != Visibility.Visible
            || host.VisualRoot.Opacity <= 0
            || !host.VisualRoot.IsHitTestVisible)
            return false;

        var (left, top) = GetHostCanvasPosition(host);

        var width = host.VisualRoot.ActualWidth > 0 ? host.VisualRoot.ActualWidth : host.VisualRoot.Width;
        var height = host.VisualRoot.ActualHeight > 0 ? host.VisualRoot.ActualHeight : host.VisualRoot.Height;
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    private void UpdateCanvasBounds(XamlRoot xamlRoot)
    {
        _currentXamlRoot = xamlRoot;
        if (_canvas.Parent != null)
        {
            _canvas.Width = double.NaN;
            _canvas.Height = double.NaN;
            return;
        }

        _canvas.Width = xamlRoot.Size.Width;
        _canvas.Height = xamlRoot.Size.Height;
    }

    private void CollapseCanvasBounds()
    {
        if (_canvas.Parent != null)
        {
            _canvas.Width = double.NaN;
            _canvas.Height = double.NaN;
        }
        else
        {
            _canvas.Width = 0;
            _canvas.Height = 0;
        }

        if (_embeddedPanel == null)
            _canvas.Visibility = Visibility.Collapsed;
    }

    // --- Positioning (aligned with Niratan Reader Mac PopupLayout) ---

    private void PositionHost(
        DictionaryLookupPopup host,
        double selectionX, double selectionY,
        double selectionWidth, double selectionHeight,
        bool isVertical,
        DictionaryDisplaySettings displaySettings,
        bool isRoot = false)
    {
        ApplyHostLayout(
            host,
            ResolveHostLayout(
                selectionX,
                selectionY,
                selectionWidth,
                selectionHeight,
                isVertical,
                displaySettings,
                isRoot));
    }

    private DictionaryPopupLayoutResult ResolveHostLayout(
        double selectionX,
        double selectionY,
        double selectionWidth,
        double selectionHeight,
        bool isVertical,
        DictionaryDisplaySettings displaySettings,
        bool isRoot = false,
        double? viewportWidth = null,
        double? viewportHeight = null)
    {
        var (measuredWidth, measuredHeight) = GetOverlaySize();
        var screenWidth = viewportWidth is > 0 ? viewportWidth.Value : measuredWidth;
        var screenHeight = viewportHeight is > 0 ? viewportHeight.Value : measuredHeight;

        var maxWidth = Math.Min(
            Math.Max(0, screenWidth - ScreenBorderPadding * 2),
            ConfiguredPopupWidth(displaySettings));
        var maxHeight = Math.Min(
            Math.Max(0, screenHeight - ScreenBorderPadding * 2),
            ConfiguredPopupHeight(displaySettings));

        return DictionaryPopupLayoutCalculator.Resolve(
            new DictionaryPopupAnchorRect(selectionX, selectionY, selectionWidth, selectionHeight),
            screenWidth,
            screenHeight,
            maxWidth,
            maxHeight,
            DictionaryPopupAppearanceConstraints.MinWidth,
            isVertical,
            displaySettings.PopupFullWidth);
    }

    private static void ApplyHostLayout(
        DictionaryLookupPopup host,
        DictionaryPopupLayoutResult layout)
    {
        host.SetSize(layout.Width, layout.Height);
        Canvas.SetLeft(host.VisualRoot, layout.Left);
        Canvas.SetTop(host.VisualRoot, layout.Top);
    }

    private void PositionChildHost(
        DictionaryLookupPopup host,
        DictionaryLookupPopup parentHost,
        DictionaryPopupRedirectRequest request,
        DictionaryDisplaySettings displaySettings)
    {
        var anchor = ResolveChildAnchor(parentHost, request);
        PositionHost(
            host,
            anchor.X,
            anchor.Y,
            anchor.Width,
            anchor.Height,
            isVertical: false,
            displaySettings: displaySettings);
    }

    private (double X, double Y, double Width, double Height) ResolveChildAnchor(
        DictionaryLookupPopup parentHost,
        DictionaryPopupRedirectRequest request)
    {
        var (parentLeft, parentTop) = GetHostCanvasPosition(parentHost);
        if (request.X is double x && request.Y is double y)
        {
            var contentOffset = parentHost.GetWebContentOffset();
            var anchor = (
                parentLeft + contentOffset.X + x,
                parentTop + contentOffset.Y + y,
                Math.Max(1, request.Width.GetValueOrDefault(1)),
                Math.Max(1, request.Height.GetValueOrDefault(1)));

            Log.Debug(
                "[DictOverlay] Child anchor parent=({ParentX:F1},{ParentY:F1}) contentOffset=({OffsetX:F1},{OffsetY:F1}) webRect=({WebX:F1},{WebY:F1},{WebWidth:F1},{WebHeight:F1}) resolved=({AnchorX:F1},{AnchorY:F1},{AnchorWidth:F1},{AnchorHeight:F1}) external={External}",
                parentLeft,
                parentTop,
                contentOffset.X,
                contentOffset.Y,
                x,
                y,
                request.Width.GetValueOrDefault(1),
                request.Height.GetValueOrDefault(1),
                anchor.Item1,
                anchor.Item2,
                anchor.Item3,
                anchor.Item4,
                _useExternalChildWindows);
            return anchor;
        }

        var (_, _, parentWidth, parentHeight) = GetHostBounds(parentHost);
        return (parentLeft, parentTop, parentWidth, parentHeight);
    }

    private static async Task<bool> HighlightPopupSelectionAsync(
        DictionaryLookupPopup parent,
        string matchedText,
        long expectedCommittedGeneration)
    {
        try
        {
            return await parent.HighlightSelectionAsync(
                matchedText,
                expectedCommittedGeneration);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictOverlay] Failed to highlight popup selection");
            return false;
        }
    }

    private bool TryGetRedirectParentGeneration(
        DictionaryLookupPopup parent,
        long expectedRootGeneration,
        out long generation)
    {
        generation = default;
        if (ReferenceEquals(parent, _rootHost))
        {
            if (parent.CommittedGeneration != expectedRootGeneration)
                return false;

            generation = expectedRootGeneration;
            return true;
        }

        if (!_childHosts.Contains(parent)
            || parent.CommittedGeneration is not long childGeneration)
        {
            return false;
        }

        generation = childGeneration;
        return true;
    }

    private bool IsRedirectCurrent(
        long redirectVersion,
        long expectedRootGeneration,
        string? expectedRootTraceId,
        DictionaryLookupPopup parent,
        long expectedParentGeneration)
    {
        if (redirectVersion != Volatile.Read(ref _redirectVersion)
            || !_rootStateCoordinator.IsCommitted(
                expectedRootGeneration,
                expectedRootTraceId)
            || parent.CommittedGeneration != expectedParentGeneration)
        {
            return false;
        }

        return ReferenceEquals(parent, _rootHost)
            || _childHosts.Contains(parent);
    }

    private void DiscardStaleChild(
        DictionaryLookupPopup child,
        long? generation,
        string? traceId)
    {
        if (generation is long pendingGeneration)
            child.CancelPendingContent(pendingGeneration, traceId);
        HideChildHost(child);
        _childHosts.Remove(child);
        ApplyPopupZOrder();
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateRootRedirects()
    {
        Interlocked.Increment(ref _redirectVersion);
        ClearChildren();
        ResetRedirectDeduplication();
    }

    private (double left, double top) GetHostCanvasPosition(DictionaryLookupPopup host)
    {
        if (_embeddedPanel != null && ReferenceEquals(host, _rootHost))
        {
            try
            {
                var transform = host.VisualRoot.TransformToVisual(_canvas);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                return (point.X, point.Y);
            }
            catch
            {
                return (0, 0);
            }
        }

        var left = Canvas.GetLeft(host.VisualRoot);
        var top = Canvas.GetTop(host.VisualRoot);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left, top);
    }

    private static (double x, double y) GetHostOffset(DictionaryLookupPopup host)
    {
        var left = Canvas.GetLeft(host.VisualRoot);
        var top = Canvas.GetTop(host.VisualRoot);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left + host.VisualRoot.Width / 2, top + host.VisualRoot.Height / 2);
    }

    private (double left, double top, double width, double height) GetHostBounds(DictionaryLookupPopup host)
    {
        var (left, top) = GetHostCanvasPosition(host);
        var width = host.VisualRoot.ActualWidth > 0 ? host.VisualRoot.ActualWidth : host.VisualRoot.Width;
        var height = host.VisualRoot.ActualHeight > 0 ? host.VisualRoot.ActualHeight : host.VisualRoot.Height;
        if (double.IsNaN(width) || width <= 0)
            width = DictionaryPopupAppearanceConstraints.MinWidth;
        if (double.IsNaN(height) || height <= 0)
            height = DictionaryPopupAppearanceConstraints.MinHeight;
        return (left, top, width, height);
    }

    private static double ConfiguredPopupWidth(DictionaryDisplaySettings displaySettings) =>
        DictionaryPopupAppearanceConstraints.NormalizeWidth(displaySettings.PopupMaxWidth);

    private static double ConfiguredPopupHeight(DictionaryDisplaySettings displaySettings) =>
        DictionaryPopupAppearanceConstraints.NormalizeHeight(displaySettings.PopupMaxHeight);

    private (double width, double height) GetOverlaySize()
    {
        var width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : _canvas.Width;
        var height = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : _canvas.Height;
        if (double.IsNaN(width) || width <= 0)
            width = _currentXamlRoot?.Size.Width ?? 1920;
        if (double.IsNaN(height) || height <= 0)
            height = _currentXamlRoot?.Size.Height ?? 1080;
        return (width, height);
    }

    public void UpdateRootSize(double width, double height)
    {
        if (!_rootWarm)
            return;

        var hasRootState = false;
        if (_rootStateCoordinator.TryGetCommitted(out var committedSnapshot))
        {
            hasRootState = true;
            var committedLayout = ResolveResizedRootLayout(
                committedSnapshot,
                width,
                height);
            if (_rootStateCoordinator.TryUpdateCommittedLayout(
                    committedSnapshot.Generation,
                    committedSnapshot.TraceId,
                    committedLayout,
                    out var resizedCommitted))
            {
                ApplyHostLayout(_rootHost, resizedCommitted.Layout);
            }
        }

        if (_rootStateCoordinator.TryGetPending(out var pendingSnapshot))
        {
            hasRootState = true;
            var pendingLayout = ResolveResizedRootLayout(
                pendingSnapshot,
                width,
                height);
            _rootStateCoordinator.TryUpdatePendingLayout(
                pendingSnapshot.Generation,
                pendingSnapshot.TraceId,
                pendingLayout,
                out _);
        }

        if (!hasRootState && _embeddedPanel != null)
        {
            _rootHost.SetSize(
                width > 0 ? width : double.NaN,
                height > 0 ? height : double.NaN);
        }
    }

    private DictionaryPopupLayoutResult ResolveResizedRootLayout(
        DictionaryPopupRootState<
            DictionaryPopupRootContext,
            DictionaryPopupAnchorRect,
            DictionaryPopupLayoutResult> snapshot,
        double width,
        double height)
    {
        if (_embeddedPanel != null)
        {
            return new DictionaryPopupLayoutResult(
                0,
                0,
                width > 0 ? width : double.NaN,
                height > 0 ? height : double.NaN);
        }

        var anchor = snapshot.Anchor;
        return ResolveHostLayout(
            anchor.X,
            anchor.Y,
            anchor.Width,
            anchor.Height,
            snapshot.Context.IsVertical,
            snapshot.Context.DisplaySettings,
            isRoot: true,
            viewportWidth: width,
            viewportHeight: height);
    }

    public DictionaryPopupHostBounds? GetRootPopupBounds()
    {
        if (!_rootWarm || !_rootVisible || _embeddedPanel != null)
            return null;

        var (left, top, width, height) = GetHostBounds(_rootHost);
        return new DictionaryPopupHostBounds(left, top, width, height);
    }

    public IReadOnlyList<DictionaryPopupHostBounds> GetVisiblePopupBounds()
    {
        var bounds = new List<DictionaryPopupHostBounds>(_childHosts.Count + 1);
        if (_rootWarm && _rootVisible && _embeddedPanel == null
            && IsHostVisible(_rootHost))
        {
            var (left, top, width, height) = GetHostBounds(_rootHost);
            bounds.Add(new DictionaryPopupHostBounds(left, top, width, height));
        }

        foreach (var child in _childHosts)
        {
            if (!IsHostVisible(child))
                continue;

            var (left, top, width, height) = GetHostBounds(child);
            bounds.Add(new DictionaryPopupHostBounds(left, top, width, height));
        }

        return bounds;
    }

    private static bool IsHostVisible(DictionaryLookupPopup host) =>
        host.VisualRoot.Visibility == Visibility.Visible
        && host.VisualRoot.Opacity > 0
        && host.VisualRoot.IsHitTestVisible;

    public void MoveRootPopupToOrigin()
    {
        if (!_rootWarm || _embeddedPanel != null)
            return;

        Canvas.SetLeft(_rootHost.VisualRoot, 0);
        Canvas.SetTop(_rootHost.VisualRoot, 0);
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRootPopupSize(double width, double height)
    {
        if (!_rootWarm || _embeddedPanel != null)
            return;

        _rootHost.SetSize(width, height);
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRootReadyOpacity(double opacity)
    {
        _rootReadyOpacity = Math.Clamp(opacity, 0, 1);
        if (_rootWarm)
            _rootHost.SetReadyOpacity(_rootReadyOpacity);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _dismissVersion);
        _dismissPending = false;
        _canvas.PointerPressed -= OnOverlayPointerPressed;
        if (_rootWarm)
        {
            _rootHost.RedirectRequested -= OnRootRedirectRequested;
            _rootHost.TapOutsideRequested -= OnRootTapOutsideRequested;
            _rootHost.DismissRequested -= OnPopupDismissRequested;
            _rootHost.Scrolled -= OnRootScrolled;
            _rootHost.ContentCommitted -= OnRootContentCommitted;
            _rootHost.ContentCommitAborted -= OnRootContentCommitAborted;
            _rootHost.QueuedShowDropped -= OnRootShowDropped;
            if (_embeddedPanel != null)
                _embeddedPanel.Children.Remove(_rootHost.VisualRoot);
            _rootHost.Dispose();
        }
        ClearChildren();
        ResetRedirectDeduplication();
        foreach (var pooledHost in _childHostPool)
        {
            pooledHost.RedirectRequested -= OnChildRedirectRequested;
            pooledHost.TapOutsideRequested -= OnChildTapOutsideRequested;
            pooledHost.DismissRequested -= OnPopupDismissRequested;
            pooledHost.Scrolled -= OnChildScrolled;
            if (_canvas.Children.Contains(pooledHost.VisualRoot))
                _canvas.Children.Remove(pooledHost.VisualRoot);
            pooledHost.Dispose();
        }
        _childHostPool.Clear();
        _redirectSemaphore.Dispose();
        _canvas.IsHitTestVisible = false;
        CollapseCanvasBounds();
    }

    public void Dismiss()
    {
        if (_dismissPending)
            return;

        Log.Information("[Lifecycle] Popup dismissed: wasVisible={WasVisible}", _rootVisible);
        var wasVisible = _rootVisible;
        var dismissVersion = Interlocked.Increment(ref _dismissVersion);
        _dismissPending = true;
        InvalidateRootRedirects();
        _rootStateCoordinator.Clear();
        _rootVisible = false;
        _canvas.IsHitTestVisible = false;
        var hideTask = _rootWarm
            ? _rootHost.HideAnimatedAsync()
            : Task.FromResult(true);
        _ = CompleteDismissAsync(dismissVersion, wasVisible, hideTask);
    }

    private async Task CompleteDismissAsync(
        long dismissVersion,
        bool wasVisible,
        Task<bool> hideTask)
    {
        var animationCompleted = await hideTask;
        if (!animationCompleted
            || dismissVersion != Volatile.Read(ref _dismissVersion)
            || _rootVisible)
        {
            return;
        }

        _dismissPending = false;
        CollapseCanvasBounds();
        VisibleHostBoundsChanged?.Invoke(this, EventArgs.Empty);

        if (_embeddedPanel != null)
            _embeddedPanel.Visibility = Visibility.Collapsed;

        if (wasVisible)
            Dismissed?.Invoke(this, EventArgs.Empty);
    }

    public DictionaryPopupShowCancellationResult CancelShow(string? traceId)
    {
        if (!_rootStateCoordinator.TryGetPendingGeneration(
                traceId,
                out var generation))
            return DictionaryPopupShowCancellationResult.NoOwnership;

        Log.Debug(
            "[LookupTrace] trace={TraceId} overlay show cancelled before ownership commit",
            traceId ?? "-");
        var contentCancelled = _rootHost.CancelPendingContent(generation, traceId);
        if (contentCancelled)
        {
            _rootStateCoordinator.TryAbort(generation, traceId);
            if (!_rootHost.HasCommittedContent)
                Dismiss();
            return DictionaryPopupShowCancellationResult.Cancelled;
        }

        if (_rootStateCoordinator.TryGetPendingGeneration(
                traceId,
                out var retainedGeneration)
            && retainedGeneration == generation)
        {
            return DictionaryPopupShowCancellationResult.CommitAccepted;
        }

        return DictionaryPopupShowCancellationResult.NoOwnership;
    }
}
