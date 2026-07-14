using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelABLoopTests
{
    [Fact]
    public void ABLoopState_FollowsNiratanPendingStartThenCommittedLoopFlow()
    {
        var sut = CreateSut();
        var type = sut.GetType();

        var setStart = type.GetMethod("SetABLoopStart", [typeof(TimeSpan)]);
        var trySetEnd = type.GetMethod("TrySetABLoopEnd", [typeof(TimeSpan)]);
        var clear = type.GetMethod("ClearABLoop");

        setStart.Should().NotBeNull();
        trySetEnd.Should().NotBeNull();
        clear.Should().NotBeNull();

        setStart!.Invoke(sut, [TimeSpan.FromSeconds(5)]);

        Get<TimeSpan?>(sut, "PendingABLoopStart").Should().Be(TimeSpan.FromSeconds(5));
        Get<object?>(sut, "ABLoop").Should().BeNull();
        Get<bool>(sut, "CanSetABLoopEnd").Should().BeTrue();
        Get<bool>(sut, "CanClearABLoop").Should().BeTrue();

        trySetEnd!.Invoke(sut, [TimeSpan.FromSeconds(3)]).Should().BeNull();
        Get<TimeSpan?>(sut, "PendingABLoopStart").Should().Be(TimeSpan.FromSeconds(5));
        Get<object?>(sut, "ABLoop").Should().BeNull();

        var loop = trySetEnd.Invoke(sut, [TimeSpan.FromSeconds(8)]);

        loop.Should().NotBeNull();
        Get<TimeSpan?>(sut, "PendingABLoopStart").Should().BeNull();
        Get<object?>(sut, "ABLoop").Should().BeSameAs(loop);
        Get<TimeSpan>(loop!, "Start").Should().Be(TimeSpan.FromSeconds(5));
        Get<TimeSpan>(loop!, "End").Should().Be(TimeSpan.FromSeconds(8));
        Get<bool>(sut, "CanSetABLoopEnd").Should().BeTrue();
        Get<bool>(sut, "CanClearABLoop").Should().BeTrue();
        Get<string>(sut, "ABLoopText").Should().Contain("00:00:05.000");
        Get<string>(sut, "ABLoopText").Should().Contain("00:00:08.000");

        setStart.Invoke(sut, [TimeSpan.FromSeconds(2)]);

        Get<TimeSpan?>(sut, "PendingABLoopStart").Should().Be(TimeSpan.FromSeconds(2));
        Get<object?>(sut, "ABLoop").Should().BeNull();

        clear!.Invoke(sut, []);

        Get<TimeSpan?>(sut, "PendingABLoopStart").Should().BeNull();
        Get<object?>(sut, "ABLoop").Should().BeNull();
        Get<bool>(sut, "CanSetABLoopEnd").Should().BeFalse();
        Get<bool>(sut, "CanClearABLoop").Should().BeFalse();
    }

    [Fact]
    public void ShouldRestartABLoopPlayback_WhenCurrentPositionReachedLoopEnd()
    {
        var sut = CreateSut();
        var method = sut.GetType().GetMethod("ShouldRestartABLoopPlayback", [typeof(VideoABLoop)]);

        method.Should().NotBeNull();

        var loop = new VideoABLoop(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));

        sut.UpdatePosition(TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(20));
        method!.Invoke(sut, [loop]).Should().Be(false);

        sut.UpdatePosition(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20));
        method.Invoke(sut, [loop]).Should().Be(true);

        sut.UpdatePosition(TimeSpan.FromSeconds(8.2), TimeSpan.FromSeconds(20));
        method.Invoke(sut, [loop]).Should().Be(true);
    }

    private static T Get<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"property {propertyName} should exist");
        var value = property!.GetValue(instance);
        if (value == null)
            return default!;

        return value.Should().BeAssignableTo<T>().Subject;
    }

    private static VideoPlayerViewModel CreateSut()
    {
        return new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());
    }
}
