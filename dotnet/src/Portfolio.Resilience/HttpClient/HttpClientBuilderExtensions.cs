// filepath: src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.8.0
// purpose: Adds AddResilientHandler() and AddStandardResilienceHandler() to IHttpClientBuilder.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IHttpClientBuilder, IResilienceExecutor, ResilientHttpMessageHandler,
//                HttpClientOptions, StandardPolicy, ResilienceOptions, PolicyDefinition
//   Used by    : every consumer service wiring an HttpClient
//   See also   : docs/http-integration.md, docs/idempotency.md, SPEC.md section 3
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;

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
    /// Adds a <see cref="ResilientHttpMessageHandler"/> to the HttpClient's handler
    /// chain, routing every request through the resilience pipeline for
    /// <paramref name="policyName"/> using default handler options.
    /// </summary>
    public static IHttpClientBuilder AddResilientHandler(
        this IHttpClientBuilder builder,
        string policyName)
        => AddResilientHandler(builder, policyName, configure: null);

    /// <summary>
    /// Adds a <see cref="ResilientHttpMessageHandler"/> with configurable options.
    /// </summary>
    /// <param name="builder">The HttpClient builder to extend.</param>
    /// <param name="policyName">The policy name (as registered with <c>AddPolicy</c>).</param>
    /// <param name="configure">Optional callback to configure handler options (for example, the idempotency header name).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    public static IHttpClientBuilder AddResilientHandler(
        this IHttpClientBuilder builder,
        string policyName,
        Action<HttpClientOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        // Build a fresh options instance at registration time, applying any
        // user configuration, and capture it in the handler factory.
        var options = new HttpClientOptions();
        configure?.Invoke(options);

        builder.AddHttpMessageHandler(_ =>
        {
            var executor = _.GetRequiredService<IResilienceExecutor>();
            return new ResilientHttpMessageHandler(executor, policyName, options);
        });

        return builder;
    }

    /// <summary>
    /// Adds a <see cref="ResilientHttpMessageHandler"/> that routes every request
    /// through the built-in <c>standard</c> policy. If no policy named
    /// <c>standard</c> is registered, one is created with safe defaults
    /// (retry + circuit + timeout).
    /// </summary>
    /// <param name="builder">The HttpClient builder to extend.</param>
    /// <param name="configure">
    /// Optional callback to customize the standard policy before it is registered.
    /// Ignored if the caller has already registered a policy named <c>standard</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="builder"/> is null.</exception>
    public static IHttpClientBuilder AddStandardResilienceHandler(
        this IHttpClientBuilder builder,
        Action<PolicyDefinition>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<Action<ResilienceOptions>>(_ =>
            options =>
            {
                if (options.Policies.ContainsKey(StandardPolicy.Name))
                {
                    return;
                }

                var policy = StandardPolicy.Create();
                configure?.Invoke(policy);
                options.Policies[StandardPolicy.Name] = policy;
            });

        builder.AddHttpMessageHandler(sp =>
        {
            var executor = sp.GetRequiredService<IResilienceExecutor>();
            return new ResilientHttpMessageHandler(executor, StandardPolicy.Name);
        });

        return builder;
    }
}
