using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Auth;
using SmartShop.Infrastructure.Persistence;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.IntegrationTests.Infrastructure;

namespace SmartShop.IntegrationTests;

/// <summary>The factory enables Database:RowLevelSecurity, so these prove the database itself enforces village isolation.</summary>
public class RowLevelSecurityTests(SmartShopFactory factory)
{
    [Fact]
    public async Task Village_scoped_requests_cannot_read_other_villages_even_without_a_where_clause()
    {
        var (plantA, adminA, ownerA, shopA) = await factory.CreateShopAsync("ร้านหมู่บ้าน A");
        var (_, _, _, shopB) = await factory.CreateShopAsync("ร้านหมู่บ้าน B");

        // Simulate a request inside village A, the way the endpoint filters resolve it.
        using var scope = factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers[ICurrentPlant.Header] = plantA.Id.ToString();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SmartShopClaims.UserId, ownerA.UserId.ToString())], "test"));
        accessor.HttpContext = http;
        try
        {
            var shops = scope.ServiceProvider.GetRequiredService<IShopDirectory>();
            // Not yet village scoped: the directory sees both shops.
            (await shops.GetOrderingInfoAsync(shopB)).ShouldNotBeNull();

            await scope.ServiceProvider.GetRequiredService<ICurrentPlant>().RequireMemberAsync();
            using var scoped = factory.Services.CreateScope(); // fresh DbContexts, same request
            http.RequestServices = scoped.ServiceProvider;
            await scoped.ServiceProvider.GetRequiredService<ICurrentPlant>().RequireMemberAsync();
            var directory = scoped.ServiceProvider.GetRequiredService<IShopDirectory>();
            (await directory.GetOrderingInfoAsync(shopA)).ShouldNotBeNull();
            (await directory.GetOrderingInfoAsync(shopB)).ShouldBeNull(); // filtered by PostgreSQL, not by the query
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task Policies_exist_on_tenant_tables_and_the_app_role_is_restricted()
    {
        var (plantA, _, _, shopA) = await factory.CreateShopAsync();
        var (_, _, _, shopB) = await factory.CreateShopAsync();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();

        await using (var policies = new NpgsqlCommand($"SELECT count(*) FROM pg_policies WHERE policyname = '{RowLevelSecurity.PolicyName}'", connection))
            ((long)(await policies.ExecuteScalarAsync())!).ShouldBeGreaterThan(10);

        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL ROLE {RowLevelSecurity.AppRole}; SELECT set_config('{RowLevelSecurity.PlantSetting}', '{plantA.Id}', true)", connection, tx))
            await set.ExecuteNonQueryAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM shops.shops WHERE id = ANY(@ids)", connection, tx);
        count.Parameters.AddWithValue("ids", new[] { shopA, shopB });
        ((long)(await count.ExecuteScalarAsync())!).ShouldBe(1);
        await tx.RollbackAsync();
    }
}
