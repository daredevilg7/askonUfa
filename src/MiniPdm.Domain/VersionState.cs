namespace MiniPdm.Domain;

public enum VersionState
{
    InWork = 1,
    Approved = 2,
    Cancelled = 3
}

public static class VersionRules
{
    public static bool CanTransition(VersionState from, VersionState to) =>
        from == VersionState.InWork && (to == VersionState.Approved || to == VersionState.Cancelled)
        || from == VersionState.Approved && to == VersionState.Cancelled;
}
