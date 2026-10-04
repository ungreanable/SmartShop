using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Notifications;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Data;
using SmartShop.Modules.Shops.Domain;
using SmartShop.Modules.Shops.Services;
using SmartShop.SharedKernel;
using Wolverine.EntityFrameworkCore;

namespace SmartShop.Modules.Shops.Endpoints;

public sealed record ShopMemberDto(Guid UserId, string DisplayName, string? PictureUrl, ShopRole Role, bool ReceiveOrderNotifications,
    string? DisplayLabel, DateTimeOffset JoinedAt);
public sealed record InviteRequest(ShopRole Role);
public sealed record InviteDto(string Code, ShopRole Role, DateTimeOffset ExpiresAt, string Url);
public sealed record MemberRoleRequest(ShopRole Role);
public sealed record NotificationToggleRequest(bool Enabled);
public sealed record LabelRequest(string? Label);
public sealed record TransferRequest(Guid UserId);
public sealed record MemberChannelDto(Guid UserId, string DisplayName, string? DisplayLabel, bool ReceiveOrderNotifications,
    bool LineFriend, int WebPushDevices, int MobileDevices, bool HasPush);
public sealed record NotificationHealthDto(PushRequirement Requirement, bool AnyRecipientHasPush, IReadOnlyList<MemberChannelDto> Members);
public sealed record AcceptedInviteDto(Guid ShopId, string ShopName, ShopRole Role);

internal static class MerchantMemberEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var m = api.MapGroup("/merchant/shops/{shopId:guid}").WithTags("Merchant").RequirePlantMember();

        m.MapGet("/members", async (Guid shopId, MerchantContext ctx, IUserDirectory users, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Staff, ct);
            var profiles = await users.GetProfilesAsync(shop.Members.Select(x => x.UserId), ct);
            return shop.Members.OrderByDescending(x => x.Role).ThenBy(x => x.JoinedAt).Select(x =>
            {
                var p = profiles.GetValueOrDefault(x.UserId);
                return new ShopMemberDto(x.UserId, p?.DisplayName ?? "?", p?.PictureUrl, x.Role, x.ReceiveOrderNotifications, x.DisplayLabel, x.JoinedAt);
            }).ToList();
        });

        m.MapPost("/invites", async (Guid shopId, InviteRequest req, MerchantContext ctx, IConfiguration config, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            var invite = new ShopInvite(shop.Id, req.Role, ctx.UserId, ctx.Now);
            ctx.Db.Invites.Add(invite);
            await ctx.Db.SaveChangesAsync(ct);
            return new InviteDto(invite.Code, invite.Role, invite.ExpiresAt, $"{(config["App:PublicUrl"] ?? "").TrimEnd('/')}/shop-invite/{invite.Code}");
        });

        m.MapPut("/members/{userId:guid}/role", async (Guid shopId, Guid userId, MemberRoleRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.ChangeRole(ctx.UserId, userId, req.Role);
            await ctx.SaveAsync(shop, ct, false, new ShopMemberRoleChanged(shop.PlantId, shop.Id, userId, req.Role));
            await ctx.InvalidateRoleAsync(shop.Id, userId, ct);
            return Results.NoContent();
        });

        // Each member decides for themselves (managers may change it for others).
        m.MapPut("/members/{userId:guid}/notifications", async (Guid shopId, Guid userId, NotificationToggleRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, userId == ctx.UserId ? ShopRole.Staff : ShopRole.Manager, ct);
            shop.SetNotifications(userId, req.Enabled);
            await ctx.SaveAsync(shop, ct, false, new ShopMemberNotificationToggled(shop.PlantId, shop.Id, userId, req.Enabled));
            await ctx.InvalidateRecipientsAsync(shop.Id, ct);
            return Results.NoContent();
        });

        m.MapPut("/members/{userId:guid}/label", async (Guid shopId, Guid userId, LabelRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, userId == ctx.UserId ? ShopRole.Staff : ShopRole.Manager, ct);
            (shop.Member(userId) ?? throw new NotFoundException("Shop member", userId)).SetLabel(req.Label);
            await ctx.SaveAsync(shop, ct);
            return Results.NoContent();
        });

        m.MapDelete("/members/{userId:guid}", async (Guid shopId, Guid userId, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, userId == ctx.UserId ? ShopRole.Staff : ShopRole.Manager, ct);
            shop.RemoveMember(ctx.UserId, userId);
            await ctx.SaveAsync(shop, ct, false, new ShopMemberRemoved(shop.PlantId, shop.Id, userId, shop.Name));
            await ctx.InvalidateRoleAsync(shop.Id, userId, ct);
            await ctx.InvalidateRecipientsAsync(shop.Id, ct);
            return Results.NoContent();
        });

        m.MapPost("/transfer-ownership", async (Guid shopId, TransferRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Owner, ct);
            shop.TransferOwnership(ctx.UserId, req.UserId);
            await ctx.SaveAsync(shop, ct, false, new ShopOwnershipTransferred(shop.PlantId, shop.Id, ctx.UserId, req.UserId));
            await ctx.InvalidateRoleAsync(shop.Id, ctx.UserId, ct);
            await ctx.InvalidateRoleAsync(shop.Id, req.UserId, ct);
            return Results.NoContent();
        });

        // Which members can actually be reached when a new order arrives?
        m.MapGet("/notification-health", async (Guid shopId, MerchantContext ctx, IUserDirectory users, [Microsoft.AspNetCore.Mvc.FromServices] INotificationChannelDirectory channels,
            IPlantDirectory plants, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Staff, ct);
            var profiles = await users.GetProfilesAsync(shop.Members.Select(x => x.UserId), ct);
            var status = (await channels.GetAsync(shop.Members.Select(x => x.UserId), ct)).ToDictionary(c => c.UserId);
            var plant = await plants.GetPlantAsync(shop.PlantId, ct);
            var rows = shop.Members.OrderByDescending(x => x.Role).Select(x =>
            {
                var c = status.GetValueOrDefault(x.UserId) ?? new ChannelStatus(x.UserId, false, 0, 0);
                return new MemberChannelDto(x.UserId, profiles.GetValueOrDefault(x.UserId)?.DisplayName ?? "?", x.DisplayLabel,
                    x.ReceiveOrderNotifications, c.LineFriend, c.WebPushDevices, c.MobileDevices, c.HasPush);
            }).ToList();
            return new NotificationHealthDto(plant?.PushRequirement ?? PushRequirement.Warn,
                rows.Any(r => r.ReceiveOrderNotifications && r.HasPush), rows);
        });

        // ---- accepting an invitation (any active member of the same plant) ----
        api.MapPost("/shop-invites/{code}/accept", async (string code, ICurrentPlant plant, ICurrentUser user,
            IDbContextOutbox<ShopsDbContext> outbox, TimeProvider clock, Microsoft.Extensions.Caching.Hybrid.HybridCache cache, CancellationToken ct) =>
        {
            var db = outbox.DbContext;
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.Code == code, ct) ?? throw new NotFoundException("Invitation", code);
            var shop = await db.Shops.FirstOrDefaultAsync(s => s.Id == invite.ShopId && s.PlantId == plant.PlantId, ct)
                       ?? throw new DomainException("invite_other_village", "This invitation belongs to a shop in another village.");
            var now = clock.GetUtcNow();
            invite.Accept(user.Id, now);
            var member = shop.AddMember(user.Id, invite.Role, now);
            db.Members.Add(member);
            await outbox.PublishAsync(new ShopMemberAdded(shop.PlantId, shop.Id, user.Id, invite.Role, shop.Name));
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
            await cache.RemoveAsync(Infrastructure.Caching.CacheKeys.ShopRole(shop.Id, user.Id), ct);
            await cache.RemoveAsync(Infrastructure.Caching.CacheKeys.ShopRecipients(shop.Id), ct);
            return new AcceptedInviteDto(shop.Id, shop.Name, invite.Role);
        }).WithTags("Shops").RequirePlantMember();
    }
}
