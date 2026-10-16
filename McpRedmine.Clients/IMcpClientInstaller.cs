namespace McpRedmine.Clients;

public enum PlanAction
{
    /// <summary>Entry does not exist yet.</summary>
    Add,

    /// <summary>Entry exists but differs (other version/command) and will be replaced.</summary>
    Update,

    /// <summary>Entry already matches; nothing will be written.</summary>
    NoChange,
}

public sealed record InstallPlan(PlanAction Action, IReadOnlyList<string> Steps);

public interface IMcpClientInstaller
{
    /// <summary>Stable id used by <c>--client</c>: claude-code, claude-desktop, cursor.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Null when the client looks installed, otherwise a short reason why it was skipped.</summary>
    string? NotDetectedReason();

    InstallPlan Plan(ServerRegistration registration);

    /// <summary>Applies the plan computed for the same registration. Safe to run repeatedly.</summary>
    void Apply(ServerRegistration registration);
}
