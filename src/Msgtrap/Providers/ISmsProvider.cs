namespace Msgtrap.Providers;

/// <summary>One HTTP route a provider exposes, shown in the UI and docs.</summary>
/// <param name="Method">HTTP verb, e.g. <c>POST</c>.</param>
/// <param name="Path">Path template as the provider publishes it, e.g. <c>/api/v2/sms/send</c>.</param>
/// <param name="Description">What the route does.</param>
public sealed record ProviderEndpoint(string Method, string Path, string Description);

/// <summary>One credential value the provider expects.</summary>
/// <param name="Field">Stable id used by <c>--credential provider.field=value</c>.</param>
/// <param name="Label">Human label, e.g. <c>API key</c>.</param>
/// <param name="Where">Where the value goes on the wire, e.g. <c>api-key header</c>.</param>
public sealed record CredentialField(string Field, string Label, string Where);

/// <summary>
/// A provider adapter: owns its routes and translates its wire format into
/// <see cref="Core.InboundSms"/>. Everything else is shared.
/// </summary>
/// <remarks>
/// To add a provider: implement this interface in one file under
/// <c>Providers/</c>, register it in <c>Program.cs</c>, and add its defaults to
/// <see cref="Core.DefaultCredentials"/>. See CONTRIBUTING.md for the checklist.
/// </remarks>
public interface ISmsProvider
{
    /// <summary>Stable id used on the command line and in the API (<c>arkesel</c>).</summary>
    string Id { get; }

    /// <summary>Display name for the UI (<c>Arkesel</c>).</summary>
    string DisplayName { get; }

    /// <summary>One line describing which API is emulated.</summary>
    string Summary { get; }

    /// <summary>Link to the provider's API docs, so fidelity claims can be checked.</summary>
    string DocsUrl { get; }

    /// <summary>Routes this adapter serves.</summary>
    IReadOnlyList<ProviderEndpoint> Endpoints { get; }

    /// <summary>Credential values the adapter authenticates with.</summary>
    IReadOnlyList<CredentialField> Credentials { get; }

    /// <summary>Registers the adapter's routes.</summary>
    void Map(IEndpointRouteBuilder app);
}
