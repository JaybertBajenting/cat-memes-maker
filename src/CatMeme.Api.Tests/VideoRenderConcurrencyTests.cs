using CatMeme.Api.Domain;
using CatMeme.Api.Jobs;
using CatMeme.Api.Services;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class VideoRenderConcurrencyTests
{
    [Theory]
    [InlineData(VideoStatus.Failed)]
    [InlineData(VideoStatus.Cancelled)]
    public void Retry_policy_allows_only_retryable_terminal_states(string status)
    {
        Assert.True(VideoRetryPolicy.Check(status).Allowed);
    }

    [Theory]
    [InlineData(VideoStatus.Rendering)]
    [InlineData(VideoStatus.AwaitingApproval)]
    [InlineData(VideoStatus.Ready)]
    [InlineData(VideoStatus.Posted)]
    [InlineData(VideoStatus.Rejected)]
    [InlineData("pending")]
    public void Retry_policy_rejects_active_completed_and_unknown_states(string status)
    {
        var result = VideoRetryPolicy.Check(status);

        Assert.False(result.Allowed);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Only_video_edit_jobs_are_routed_to_the_serial_render_queue()
    {
        Assert.True(VideoRenderQueueFilter.Routes(typeof(VideoEditJob)));
        Assert.False(VideoRenderQueueFilter.Routes(typeof(TopicGenerationJob)));
    }

    [Theory]
    [InlineData(VideoStatus.Rendering, null, "42", true)]
    [InlineData(VideoStatus.Rendering, "42", "42", true)]
    [InlineData(VideoStatus.Failed, "42", "42", true)]
    [InlineData(VideoStatus.Rendering, "old", "42", false)]
    [InlineData(VideoStatus.AwaitingApproval, "42", "42", false)]
    [InlineData(VideoStatus.Ready, null, "42", false)]
    [InlineData(null, null, "42", false)]
    public void Render_guard_accepts_only_current_runnable_delivery(
        string? status, string? recordedJobId, string currentJobId, bool expected)
    {
        Assert.Equal(expected, VideoRenderGuard.CanRun(status, recordedJobId, currentJobId));
    }
}
