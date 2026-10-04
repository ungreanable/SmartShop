using System.Net;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

public class PlantTests(SmartShopFactory factory)
{
    private sealed record JoinPreviewDto(Guid PlantId, string Name);
    private sealed record MyMembershipDto(Guid PlantId, string PlantName, string Role, string Status, string? DecisionReason);
    private sealed record PlantDetailsDto(Guid Id, string Name, MyMembershipDto Me);
    private sealed record JoinLinkDto(string JoinCode, string Url);

    [Fact]
    public async Task Only_system_admins_can_create_villages()
    {
        var user = await factory.LoginAsync();
        var response = await user.PostRawAsync("/api/admin/plants", new { name = "X" });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creator_becomes_active_plant_admin()
    {
        var (plant, admin) = await factory.CreatePlantAsync("หมู่บ้านสุขใจ");

        var details = await admin.GetAsync<PlantDetailsDto>("/api/plant");
        details.Name.ShouldBe("หมู่บ้านสุขใจ");
        details.Me.Role.ShouldBe("PlantAdmin");
        details.Me.Status.ShouldBe("Active");
        plant.JoinCode.Length.ShouldBe(8);
    }

    [Fact]
    public async Task Joining_requires_admin_approval_before_seeing_anything()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var newcomer = await factory.LoginAsync();

        var preview = await newcomer.GetAsync<JoinPreviewDto>($"/api/plants/join/{plant.JoinCode.ToLowerInvariant()}");
        preview.PlantId.ShouldBe(plant.Id);

        await newcomer.PostOkAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo = "12/3", soi = "5", nickname = "แดง" });
        newcomer.InPlant(plant.Id);

        // Pending members are locked out of the village.
        (await newcomer.GetRawAsync("/api/plant")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var mine = await newcomer.GetAsync<List<MyMembershipDto>>("/api/me/memberships");
        mine.Single().Status.ShouldBe("Pending");

        var pending = await admin.GetAsync<PagedDto<MemberRowDto>>("/api/plant/admin/members?status=Pending");
        var row = pending.Items.Single(r => r.UserId == newcomer.UserId);
        row.HouseNo.ShouldBe("12/3");
        await admin.PostOkAsync($"/api/plant/admin/members/{row.MembershipId}/approve");

        var details = await newcomer.GetAsync<PlantDetailsDto>("/api/plant");
        details.Me.Status.ShouldBe("Active");
    }

    [Fact]
    public async Task Rejected_user_sees_reason_and_may_request_again()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var user = await factory.LoginAsync();
        await user.PostOkAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo = "1" });
        var row = (await admin.GetAsync<PagedDto<MemberRowDto>>("/api/plant/admin/members?status=Pending")).Items.Single(r => r.UserId == user.UserId);

        await admin.PostOkAsync($"/api/plant/admin/members/{row.MembershipId}/reject", new { reason = "ไม่ใช่คนในหมู่บ้าน" });

        var mine = (await user.GetAsync<List<MyMembershipDto>>("/api/me/memberships")).Single();
        mine.Status.ShouldBe("Rejected");
        mine.DecisionReason.ShouldBe("ไม่ใช่คนในหมู่บ้าน");

        await user.PostOkAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo = "1", message = "ย้ายมาใหม่ครับ" });
        (await user.GetAsync<List<MyMembershipDto>>("/api/me/memberships")).Single().Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task Suspended_member_loses_access_immediately()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var member = await factory.JoinAsync(plant, admin);
        (await member.GetRawAsync("/api/plant")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = (await admin.GetAsync<PagedDto<MemberRowDto>>("/api/plant/admin/members?status=Active")).Items.Single(r => r.UserId == member.UserId);
        await admin.PostOkAsync($"/api/plant/admin/members/{row.MembershipId}/suspend", new { reason = "ย้ายออก" });

        (await member.GetRawAsync("/api/plant")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var again = await member.PostRawAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo = "1" });
        (await TestClient.ProblemCodeAsync(again)).ShouldBe("membership_suspended");
    }

    [Fact]
    public async Task Members_cannot_use_admin_endpoints()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var member = await factory.JoinAsync(plant, admin);
        (await member.GetRawAsync("/api/plant/admin/members")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Last_admin_cannot_leave_or_be_demoted()
    {
        var (plant, admin) = await factory.CreatePlantAsync();

        var leave = await admin.PostRawAsync($"/api/plants/{plant.Id}/leave");
        (await TestClient.ProblemCodeAsync(leave)).ShouldBe("last_admin");
    }

    [Fact]
    public async Task Resetting_the_join_link_invalidates_the_old_code()
    {
        var (plant, admin) = await factory.CreatePlantAsync();
        var link = await admin.PostAsync<JoinLinkDto>("/api/plant/admin/join-link/reset");
        link.JoinCode.ShouldNotBe(plant.JoinCode);
        link.Url.ShouldEndWith($"/join/{link.JoinCode}");

        var user = await factory.LoginAsync();
        var response = await user.PostRawAsync("/api/plants/join", new { joinCode = plant.JoinCode, houseNo = "1" });
        (await TestClient.ProblemCodeAsync(response)).ShouldBe("join_code_invalid");
    }

    [Fact]
    public async Task Requests_without_plant_header_are_rejected()
    {
        var user = await factory.LoginAsync();
        var response = await user.GetRawAsync("/api/plant");
        (await TestClient.ProblemCodeAsync(response)).ShouldBe("plant_required");
    }
}
