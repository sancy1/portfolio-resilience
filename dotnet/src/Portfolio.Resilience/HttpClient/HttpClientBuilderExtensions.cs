// filepath: src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.4.0
// purpose: Adds AddResilientHandler() to IHttpClientBuilder — attach the resilient handler with one line.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IHttpClientBuilder, IResilienceExecutor, ResilientHttpMessageHandler
//   Used by    : every consumer service wiring an HttpClient
//   See also   : docs/http-integration.md, SPEC.md §HttpIntegration
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.HttpClient;

/// <summary>
/// Extends <see cref="IHttpClientBuilder"/> with the resilient handler. Attach
/// the resilience pipeline to a named or typed HttpClient with one line:
/// <code>
/// builder.Services
///     .AddHttpClient("auth-service", c =&gt; c.BaseAddress = new Uri(url))
///     .AddResilientHandler("auth-service");
/// </code>
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="ResilientHttpMessageHandler"/> to the HttpClient's handler chain,
    /// routing every request through the resilience pipeline for <paramref name="policyName"/>.
    /// </summary>
    /// <param name="builder">The HttpClient builder to extend.</param>
    /// <param name="policyName">The policy name (as registered with <c>AddPolicy</c>).</param>
    public static IHttpClientBuilder AddResilientHandler(
        this IHttpClientBuilder builder,
        string policyName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        builder.AddHttpMessageHandler(sp =>
        {
            var executor = sp.GetRequiredService<IResilienceExecutor>();
            return new ResilientHttpMessageHandler(executor, policyName);
        });

        return builder;
    }
}
