using LogAgent.Routing;
using LogAgent.Shared.Chat;
using LogAgent.Shared.Configuration;
using Xunit;

namespace LogAgent.Tests;

internal sealed class FakeEmbeddings(bool succeed) : IEmbeddingsClient
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return succeed
            ? Task.FromResult(new[] { 0.1f, 0.2f, 0.3f })
            : Task.FromException<float[]>(new HttpRequestException("fake embedding failure"));
    }
}

/// <summary>
/// 向量检索的故障切换（docs/08 P2）：此前只尝试 chain[0]，Embedding provider 挂了就直接退回关键词；
/// 现在与 Chat 一致地沿链切换、并计入熔断。
/// </summary>
public class ModelRouterEmbeddingTests
{
    private static AppConfig Config(params string[] chain) => new()
    {
        Providers =
        [
            new ProviderConfig { Id = "p1", Name = "P1", ChatModel = "m1", EmbeddingModel = "e1", Enabled = true },
            new ProviderConfig { Id = "p2", Name = "P2", ChatModel = "m2", EmbeddingModel = "e2", Enabled = true },
        ],
        Rules = [new RouterRule { TaskType = TaskType.Embedding, ProviderChain = [.. chain] }],
        CircuitBreakerThreshold = 2,
    };

    [Fact]
    public async Task Embed_FailsOverToNextProvider()
    {
        var first = new FakeEmbeddings(succeed: false);
        var second = new FakeEmbeddings(succeed: true);
        var router = new ModelRouter(Config("p1", "p2"), _ => new FakeClient("chat", true),
            p => p.Id == "p1" ? first : second);

        var vector = await router.EmbedAsync("OutOfMemoryError");

        Assert.NotNull(vector);
        Assert.Equal(3, vector!.Length);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Embed_AllFail_ReturnsNull()
    {
        var router = new ModelRouter(Config("p1", "p2"), _ => new FakeClient("chat", true),
            _ => new FakeEmbeddings(succeed: false));

        Assert.Null(await router.EmbedAsync("x"));
    }

    [Fact]
    public async Task Embed_OpenCircuit_IsSkipped()
    {
        var config = Config("p1", "p2");
        config.CircuitBreakerThreshold = 1; // 一次失败即熔断
        var first = new FakeEmbeddings(succeed: false);
        var second = new FakeEmbeddings(succeed: true);
        var router = new ModelRouter(config, _ => new FakeClient("chat", true),
            p => p.Id == "p1" ? first : second);

        await router.EmbedAsync("a"); // p1 失败 → 熔断
        await router.EmbedAsync("b"); // 应直接跳过 p1

        Assert.Equal(1, first.Calls);
        Assert.Equal(2, second.Calls);
    }
}
