// filepath: src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.7.0
// purpose: Adds AddResilientHandler() and AddStandardResilienceHandler() to IHttpClientBuilder.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IHttpClientBuilder, IResilienceExecutor, ResilientHttpMessageHandler,
//                StandardPolicy, ResilienceOptions, PolicyDefinition
//   Used by    : every consumer service wiring an HttpClient
//   See also   : docs/http-integration.md, SPEC.md section 3
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
/// <remarks>
/// For a quick-start with a built-in safe default policy, use
/// <see cref="AddStandardResilienceHandler(IHttpClientBuilder, Action{PolicyDefinition}?)"/>
/// instead.
/// </remarks>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="ResilientHttpMessageHandler"/> to the HttpClient's handler chain,
    /// routing every request through the resilience pipeline for <paramref name="policyName"/>.
    /// </summary>
    /// <param name="builder">The HttpClient builder to extend.</param>
    /// <param name="policyName">The policy name (as registered with <c>AddPolicy</c>).</param>
    /// <exception cref="ArgumentNullException">If <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
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

    /// <summary>
    /// Adds a <see cref="ResilientHttpMessageHandler"/> that routes every request
    /// through the built-in <c>standard</c> policy. If no policy named
    /// <c>standard</c> is registered, one is created with safe defaults (retry +
    /// circuit + timeout). If the caller has already registered their own
    /// <c>standard</c> policy, that policy wins and no defaults are applied.
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

        // Register a post-configuration action that installs the standard policy
        // at resolve time, unless the caller has already defined one.
        builder.Services.AddSingleton<Action<ResilienceOptions>>(_ =>
            options =>
            {
                if (options.Policies.ContainsKey(StandardPolicy.Name))
                {
                    // User's registration wins.
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
