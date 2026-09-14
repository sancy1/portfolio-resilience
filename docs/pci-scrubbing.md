<!--
filepath: docs/pci-scrubbing.md
package:  Portfolio.Resilience | since: v0.8.0
purpose:  Explains PCI-safe event scrubbing - how sensitive patterns are masked before events reach any log sink.
-->

# PCI-Safe Event Scrubbing

## What it is

An opt-in pipeline step that redacts sensitive patterns from every
`ResilienceEvent` **before any log sink sees it**. The scrubbing runs once in
`CompositeLogSink` and applies to `ErrorMessage`, `ErrorType`, and every
string value in `Metadata`.

Three pattern families are masked out of the box:

- **PAN** - primary account number: 13 to 19 consecutive digits, optionally
  separated by single spaces or dashes (`4111111111111111`,
  `4111-1111-1111-1111`, `4111 1111 1111 1111`).
- **CVV/CVC** - 3 or 4 digits adjacent to the tokens `cvv` or `cvc`
  (`cvv 123`, `CVC: 4567`, `cvc=999`).
- **SSN** - US Social Security Number in the canonical `ddd-dd-dddd` format.

Every match is replaced with `[REDACTED]`.

## Why it exists

**For any service that handles sensitive data - not just payments.**

A decline message from a payment provider often includes the last four digits
of a card, or a truncated PAN, or the CVV echoed back by a buggy integration.
A validation error might include an SSN. An upstream service might echo an
API key or a bearer token into an exception message.

Without scrubbing, those strings flow into every log sink: console, file,
OpenTelemetry, Datadog, Application Insights. Once they are there, they are
subject to retention policies, log-search access controls, and compliance
audits. PCI-DSS explicitly forbids logging full PANs, CVVs, or SSNs.

v0.8.0 makes scrubbing one flag.

## When you need it

**Always, if your service is in PCI scope** (handles card data directly or
through a payment provider). Turn it on at registration:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("stripe-charge", p =>
        {
            p.Logging.ScrubSensitiveData = true;
            // other policy settings...
        }));

**Any service that handles PII, tokens, or API keys** also benefits. The
scrubber does not know the difference between a card number and an account
number - it masks any 13-19 digit run, which is the conservative choice.

**Not needed for:** services whose operations only touch non-sensitive data
(public content, internal caches, health checks). Scrubbing has a small
per-event cost (a few regex matches) and adds a dictionary copy per event.
Disabling it for non-sensitive services is reasonable.

## How it works

### The `IEventScrubber` interface

    public interface IEventScrubber
    {
        ResilienceEvent Scrub(ResilienceEvent evt);
    }

One method. The default implementation is `DefaultPciScrubber`. A custom
implementation can be registered to add provider-specific patterns, replace
the default masking format, or integrate with a data-classification service.

### Where it runs

Scrubbing runs in `CompositeLogSink.Emit`, **once**, at the top of the method:

    public void Emit(ResilienceEvent evt)
    {
        var effective = _scrubber is null ? evt : _scrubber.Scrub(evt);
        foreach (var sink in _sinks) { sink.Emit(effective); }
    }

Every child sink receives the scrubbed copy - none of them ever sees the
original. When `ScrubSensitiveData` is enabled and a single sink is registered,
the DI wiring wraps that sink in a `CompositeLogSink` so the scrubber runs
regardless of how many sinks are present.

### The default scrubber

`DefaultPciScrubber` uses three compiled regexes:

    PAN:  (?<!\d)(?:\d[\- ]?){12,18}\d(?!\d)
    CVV:  \b(?:cvv|cvc)\b\s*[:=]?\s*\d{3,4}\b
    SSN:  \b\d{3}-\d{2}-\d{4}\b

Each match is replaced with `[REDACTED]`. The scrubbing is applied to:

- `ResilienceEvent.ErrorMessage`
- `ResilienceEvent.ErrorType`
- Every string value in `ResilienceEvent.Metadata` (non-string values are
  preserved as-is)

The record is never mutated - a scrubbed copy is returned.

### Payment example

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new FileLogSink("/var/log/resilience"))
        .AddPolicy("stripe-charge", p =>
        {
            p.Logging.ScrubSensitiveData = true;
        }));

Every event from the `stripe-charge` policy is scrubbed before it reaches the
file sink. A decline message that would have logged `card 4111111111111111
declined` now logs `card [REDACTED] declined`.

### Generic microservice example

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("user-service", p =>
        {
            p.Logging.ScrubSensitiveData = true;
        }));

Any service that handles PII - user profiles, addresses, national ID numbers -
benefits from the same flag. The scrubber masks SSN patterns, long digit runs,
and CVV-like tokens before they hit the console sink or the Docker log stream.

## Configuration

### Opting in

Scrubbing is **off by default**. Enable it per policy:

    p.Logging.ScrubSensitiveData = true;

Or for every unknown policy via the default policy:

    builder.Services.AddPortfolioResilience(r =>
    {
        r.UseDefaultPolicy(p => p.Logging.ScrubSensitiveData = true);
        r.AddLogSink(new ConsoleLogSink());
    });

If **any** policy (default or named) opts in, the DI wiring installs the
default scrubber globally. Events from policies that did not opt in are also
scrubbed - scrubbing is a sink-level concern, not a per-policy filter. The
opt-in is a **signal** that at least one policy needs it; the enforcement is
applied to every event once installed.

### Replacing the scrubber

To use a custom scrubber, register it before calling `AddPortfolioResilience`,
or provide one in the pipeline directly:

    var composite = new CompositeLogSink(
        sinks: new ILogSink[] { new ConsoleLogSink() },
        logger: null,
        scrubber: new MyScrubber());

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(composite));

The default scrubber is intentionally conservative (broad digit patterns). If
your service needs tighter rules, or if you have a data-classification service
that knows exactly which fields are sensitive, implement `IEventScrubber` and
plug it in.

## What this does NOT do

- **It is not a compliance guarantee.** The default scrubber catches common
  patterns. It cannot catch provider-specific formats, locale-specific
  identity numbers, or sensitive data embedded in unexpected shapes. It is
  **one layer** of a PCI strategy, not the whole strategy.
- **It does not prevent sensitive data from entering events.** The library
  cannot know what your operation's error messages will contain. The scrubber
  is a **last line of defense** - the primary defense is not putting sensitive
  data into events in the first place.
- **It does not scrub non-string metadata values.** A byte array containing a
  card number will pass through. Callers who put sensitive data in non-string
  metadata accept responsibility for their choice.
- **It does not redact IDs that look like PANs.** A 16-digit order number is
  masked. If your business identifiers can be 13-19 digits long, use
  distinguishing prefixes or separators so they don't match.

## Common mistakes

**Mistake 1 - relying on the scrubber as your only control.**
The scrubber is a safety net. The primary control is designing operations so
sensitive data never appears in error messages or metadata. Review what your
services log.

**Mistake 2 - enabling scrubbing only for the policies you think are risky.**
Scrubbing is a sink-level filter. Enabling it once anywhere installs it
everywhere. If any policy in your service touches sensitive data, enable it -
the others are scrubbed for free.

**Mistake 3 - expecting the scrubber to redact non-string metadata.**
`Metadata["card"] = new byte[] { ... }` passes through. Convert non-string
sensitive values to strings with a prefix that the scrubber will match, or do
not put them in metadata.

**Mistake 4 - configuring a custom scrubber that throws.**
An `IEventScrubber` that throws will propagate the exception up through
`CompositeLogSink.Emit`. The pipeline's own exception isolation protects
against **sink** failures, not scrubber failures. Keep scrubber logic
exception-free.

**Mistake 5 - thinking scrubbing is free.**
Each event runs three regex matches against the message, the error type, and
each string metadata value. For high-volume services, measure the cost. It is
small (microseconds per event) but non-zero. If every event is non-sensitive
by design, scrubbing is unnecessary overhead.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/DefaultPciScrubberTests.cs`
(20 tests), `CompositeLogSinkScrubberTests.cs` (9 tests), and
`ResilienceIntegrationTestsV08_Scrubbing.cs` (2 end-to-end tests).

## See also

- [logging.md](logging.md) - the sink and event model
- [error-classification.md](error-classification.md) - what ends up in `ErrorMessage`
- [../SPEC.md](../SPEC.md) section 19 - the normative contract
