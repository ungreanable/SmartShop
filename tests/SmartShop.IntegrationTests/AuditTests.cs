using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public sealed record AuditDto(Guid Id, string Action, Guid? ActorId, string? ActorName, string SubjectType, Guid SubjectId, string? SubjectName, string? Detail, DateTimeOffset At);

public class AuditTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Admin_actions_are_recorded_in_the_village_audit_log()
    {
        var (plant, admin, _, shopId) = await factory.CreateShopAsync();
        var member = await factory.JoinAsync(plant, admin, "ลุงมี");
        var row = (await admin.GetAsync<PagedDto<MemberRowDto>>("/api/plant/admin/members?status=Active")).Items.Single(r => r.UserId == member.UserId);
        await admin.PostOkAsync($"/api/plant/admin/members/{row.MembershipId}/suspend", new { reason = "ย้ายออก" });

        await FactoryExtensions.EventuallyAsync(async () =>
        {
            var log = await admin.GetAsync<PagedDto<AuditDto>>("/api/plant/admin/audit");
            log.Items.Select(i => i.Action).ShouldContain("shop_application.approved");
            log.Items.Count(i => i.Action == "membership.approved").ShouldBe(2);
            var suspended = log.Items.First(); // newest first
            suspended.Action.ShouldBe("membership.suspended");
            suspended.SubjectName.ShouldBe("ลุงมี");
            suspended.ActorName.ShouldBe(admin.DisplayName);
            suspended.Detail.ShouldBe("ย้ายออก");
            log.Items.Single(i => i.Action == "shop_application.approved").SubjectId.ShouldBe(shopId);
        });

        var filtered = await admin.GetAsync<PagedDto<AuditDto>>("/api/plant/admin/audit?action=membership.");
        filtered.Items.ShouldAllBe(i => i.Action.StartsWith("membership."));

        var other = await factory.JoinAsync(plant, admin);
        (await other.GetRawAsync("/api/plant/admin/audit")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
