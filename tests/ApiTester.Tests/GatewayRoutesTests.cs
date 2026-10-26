using ApiTester.Core;

namespace ApiTester.Tests;

public class GatewayRoutesTests
{
    private static GatewayRoutes Routes(params (string Prefix, string Upstream)[] routes) =>
        new(routes.Select(r => new GatewayRoute(r.Prefix, new Uri(r.Upstream))).ToList());

    [Fact]
    public void Longest_matching_prefix_wins()
    {
        var table = Routes(
            ("/", "https://root.example/"),
            ("/api", "https://api.example/"),
            ("/api/v2", "https://v2.example/"));

        var route = table.Resolve("/api/v2/orders", out _);

        Assert.NotNull(route);
        Assert.Equal("/api/v2", route!.PathPrefix);
        Assert.Equal(new Uri("https://v2.example/"), route.Upstream);
    }

    [Theory]
    [InlineData("/api/orders?x=1", "/orders?x=1")]
    [InlineData("/api/orders", "/orders")]
    [InlineData("/api/", "/")]
    [InlineData("/api", "/")]
    [InlineData("/api?x=1", "/?x=1")]
    public void Matched_prefix_is_stripped_and_the_query_preserved(string target, string expected)
    {
        var table = Routes(("/api", "https://api.example/"));

        var route = table.Resolve(target, out var forwarded);

        Assert.NotNull(route);
        Assert.Equal(expected, forwarded);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/x?q=1")]
    [InlineData("/deep/path/with/segments")]
    public void Root_route_forwards_the_path_unchanged(string target)
    {
        var table = Routes(("/", "https://root.example/"));

        var route = table.Resolve(target, out var forwarded);

        Assert.NotNull(route);
        Assert.Equal(target, forwarded);
    }

    [Fact]
    public void Prefix_only_matches_whole_path_segments()
    {
        var table = Routes(("/api", "https://api.example/"));

        Assert.Null(table.Resolve("/apiary", out _));
        Assert.Null(table.Resolve("/apiary/hives?x=1", out _));
        Assert.NotNull(table.Resolve("/api/orders", out _));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("api/")]
    public void Prefixes_are_normalized_to_one_leading_and_no_trailing_slash(string written)
    {
        var table = Routes((written, "https://api.example/"));

        var route = table.Resolve("/api/orders?x=1", out var forwarded);

        Assert.NotNull(route);
        Assert.Equal("/api", route!.PathPrefix);
        Assert.Equal("/orders?x=1", forwarded);
    }

    [Fact]
    public void Unmatched_path_resolves_to_no_route_and_is_left_unchanged()
    {
        var table = Routes(("/api", "https://api.example/"), ("/auth", "https://auth.example/"));

        var route = table.Resolve("/static/app.js?v=2", out var forwarded);

        Assert.Null(route);
        Assert.Equal("/static/app.js?v=2", forwarded);
    }

    [Theory]
    [InlineData("/API/Orders")]
    [InlineData("/Api/Orders")]
    public void Matching_ignores_case(string target)
    {
        var table = Routes(("/api", "https://api.example/"));

        var route = table.Resolve(target, out var forwarded);

        Assert.NotNull(route);
        Assert.Equal("/Orders", forwarded);
    }

    [Theory]
    [InlineData("/api", "/api")]
    [InlineData("/api", "api/")]
    [InlineData("/api", "/API")]
    public void Two_routes_on_the_same_mount_point_are_rejected(string first, string second)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Routes((first, "https://one.example/"), (second, "https://two.example/")));

        Assert.Contains("/api", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_route_table_is_allowed_and_matches_nothing()
    {
        // A gateway running only dynamic targets (`serve --allow`) has no static mounts.
        var routes = new GatewayRoutes(Array.Empty<GatewayRoute>());
        Assert.Empty(routes.All);
        Assert.Null(routes.Resolve("/anything", out _));
    }
}
