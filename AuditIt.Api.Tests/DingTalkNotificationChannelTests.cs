using System.Text;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuditIt.Api.Tests;

public class DingTalkNotificationChannelTests
{
    [Fact]
    public async Task DeliverBatchAsync_mergesMatchingRecipientDigests_and_keepsOrderAndChangeDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.AddRange(
            new User { Name = "操作员甲", DingTalkUserId = "ding-a", Status = UserStatus.Active },
            new User { Name = "操作员乙", DingTalkUserId = "ding-b", Status = UserStatus.Active });
        await db.SaveChangesAsync();

        var dingTalk = new RecordingDingTalkService();
        var channel = new DingTalkNotificationChannel(
            db,
            dingTalk,
            NullLogger<DingTalkNotificationChannel>.Instance);
        var createdAt = new DateTime(2026, 8, 27, 4, 0, 0, DateTimeKind.Utc);
        var reminders = new[]
        {
            BuildReminder("操作员甲", "R20260827-0001", "收货地址：宁波 -> 杭州", createdAt),
            BuildReminder("操作员甲", "R20260827-0002", "负责人：操作员甲 -> 操作员乙", createdAt.AddSeconds(1)),
            BuildReminder("操作员乙", "R20260827-0001", "收货地址：宁波 -> 杭州", createdAt),
            BuildReminder("操作员乙", "R20260827-0002", "负责人：操作员甲 -> 操作员乙", createdAt.AddSeconds(1))
        };

        await channel.DeliverBatchAsync(reminders, default);

        var send = Assert.Single(dingTalk.Sends);
        Assert.Equal(new[] { "ding-a", "ding-b" }, send.UserIds.OrderBy(id => id));
        Assert.Contains("【工作提醒汇总】", send.Content);
        Assert.Contains("R20260827-0001", send.Content);
        Assert.Contains("收货地址：宁波 -> 杭州", send.Content);
        Assert.Contains("R20260827-0002", send.Content);
        Assert.Contains("负责人：操作员甲 -> 操作员乙", send.Content);
        Assert.True(Encoding.UTF8.GetByteCount(send.Content) <= 2048);
    }

    [Fact]
    public async Task DeliverBatchAsync_splitsLongDigests_withoutLosingDetails_orExceedingDingTalkLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Name = "操作员甲", DingTalkUserId = "ding-a", Status = UserStatus.Active });
        await db.SaveChangesAsync();

        var dingTalk = new RecordingDingTalkService();
        var channel = new DingTalkNotificationChannel(
            db,
            dingTalk,
            NullLogger<DingTalkNotificationChannel>.Instance);
        var detailMarker = "保留到消息末尾的修改详情";
        var reminders = new[]
        {
            BuildReminder("操作员甲", "R20260827-0101", new string('修', 900) + detailMarker, DateTime.UtcNow),
            BuildReminder("操作员甲", "R20260827-0102", new string('改', 900) + detailMarker, DateTime.UtcNow.AddSeconds(1))
        };

        await channel.DeliverBatchAsync(reminders, default);

        Assert.True(dingTalk.Sends.Count >= 2);
        Assert.All(dingTalk.Sends, send => Assert.True(Encoding.UTF8.GetByteCount(send.Content) <= 2048));
        var combined = string.Join("", dingTalk.Sends.Select(send => send.Content));
        Assert.Contains("R20260827-0101", combined);
        Assert.Contains("R20260827-0102", combined);
        Assert.Equal(2, CountOccurrences(combined, detailMarker));
    }

    private static Reminder BuildReminder(string target, string rentalNumber, string changes, DateTime createdAt) => new()
    {
        Type = ReminderType.Manual,
        Level = ReminderLevel.Info,
        RelatedEntityType = "Rental",
        RelatedEntityId = Guid.NewGuid().ToString(),
        TargetUser = target,
        Title = $"租赁单 {rentalNumber} | 信息已更新",
        Message = $"操作：信息已更新\n具体修改：{changes}",
        DueAt = createdAt,
        CreatedAt = createdAt
    };

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private sealed class RecordingDingTalkService : IDingTalkService
    {
        public List<(IReadOnlyList<string> UserIds, string Content)> Sends { get; } = new();

        public Task SendWorkNoticeAsync(IEnumerable<string> userIds, string content, CancellationToken ct = default)
        {
            Sends.Add((userIds.ToList(), content));
            return Task.CompletedTask;
        }

        public Task<string> GetAccessTokenAsync() => throw new NotImplementedException();
        public Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code) => throw new NotImplementedException();
        public Task<DingTalkContactUser> GetSsoUserInfoByCodeAsync(string ssoCode) => throw new NotImplementedException();
        public Task<IReadOnlyList<DingTalkUserDetail>> GetDirectoryUsersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DingTalkUserDetail>>(Array.Empty<DingTalkUserDetail>());
        public Task<bool> SendRobotMarkdownAsync(string title, string markdownText, string? msgUuid = null, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
