using FluentAssertions;
using Moq;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class VideoPlayerViewModelVideoTransformTests
{
    [Fact]
    public void VideoTransformState_FollowsNiratanAspectRatioAndRotateFlow()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var setAspectRatio = type.GetMethod("SetAspectRatio", [typeof(string)]);
        var rotateClockwise = type.GetMethod("RotateClockwise", []);

        setAspectRatio.Should().NotBeNull();
        rotateClockwise.Should().NotBeNull();

        Get<string>(sut, "AspectRatioValue").Should().Be("-1");
        Get<string>(sut, "AspectRatioText").Should().Be("Automatic");
        Get<int>(sut, "VideoRotationDegrees").Should().Be(0);
        Get<string>(sut, "VideoRotationText").Should().Be("0°");

        setAspectRatio!.Invoke(sut, ["16:9"]);

        Get<string>(sut, "AspectRatioValue").Should().Be("16:9");
        Get<string>(sut, "AspectRatioText").Should().Be("16:9");

        rotateClockwise!.Invoke(sut, []);
        Get<int>(sut, "VideoRotationDegrees").Should().Be(90);
        Get<string>(sut, "VideoRotationText").Should().Be("90°");

        rotateClockwise.Invoke(sut, []);
        rotateClockwise.Invoke(sut, []);
        rotateClockwise.Invoke(sut, []);

        Get<int>(sut, "VideoRotationDegrees").Should().Be(0);
        Get<string>(sut, "VideoRotationText").Should().Be("0°");
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
